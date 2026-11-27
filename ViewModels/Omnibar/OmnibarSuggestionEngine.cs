// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Core;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Omnibar;
using ByteExplorer.Icons;
using ByteExplorer.Infrastructure;

namespace ByteExplorer.ViewModels.Omnibar
{
    /// <summary>
    /// Turns what was typed into ranked, grouped suggestions. What it offers depends on what the text reads as
    /// (<see cref="OmnibarInput"/>):
    /// <list type="bullet">
    /// <item><b>Nothing</b> (or the current path, as when editing starts): recent folders and pinned places.</item>
    /// <item><b>A path</b>: the entries of the folder being typed, those starting with the partial name first, then fuzzy
    /// matches; the top one becomes the inline completion (Tab).</item>
    /// <item><b>Words</b>: places, recent folders and items of the current folder, fuzzy matched; a few commands; and
    /// "search everywhere" / "filter this folder" for the text, so Enter always does something useful.</item>
    /// <item><b>"&gt;" commands</b>, <b>"?" search</b>, <b>web</b> and <b>network addresses</b>.</item>
    /// </list>
    /// Runs on a worker thread (the caller's), lists folders through the context (cached), and stops at cancellation:
    /// each keystroke cancels the previous query.
    /// </summary>
    public sealed class OmnibarSuggestionEngine
    {
        public const int MaxPathMatches = 40;
        private const int MaxPlaces = 4;
        private const int MaxRecent = 5;
        private const int MaxRecentWhenEmpty = 8;
        private const int MaxPinnedWhenEmpty = 4;
        private const int MaxHere = 6;
        private const int MaxCommandsInText = 3;
        private const int MaxCommands = 40;
        private const int MinCommandScoreInText = 45;

        private readonly IOmnibarContext _context;

        public OmnibarSuggestionEngine(IOmnibarContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public async Task<IReadOnlyList<OmnibarSuggestion>> GetSuggestionsAsync(string text, CancellationToken cancellationToken)
        {
            string current = _context.CurrentFolder;
            bool isCurrentPath = !string.IsNullOrEmpty(current) && string.Equals(text?.Trim(), current, PathUtil.Comparison);
            OmnibarInput input = isCurrentPath
                ? OmnibarInput.Parse(string.Empty, current, _context.HomeFolder)
                : OmnibarInput.Parse(text, current, _context.HomeFolder);

            var results = new List<OmnibarSuggestion>();
            switch (input.Kind)
            {
                case OmnibarInputKind.Empty:
                    AddRecent(results, string.Empty, MaxRecentWhenEmpty, exclude: null);
                    AddPinned(results);
                    break;
                case OmnibarInputKind.Command:
                    AddCommands(results, input.Query, MaxCommands, minScore: 0);
                    break;
                case OmnibarInputKind.Search:
                    AddSearch(results, input.Query);
                    break;
                case OmnibarInputKind.WebAddress:
                    results.Add(new OmnibarSuggestion($"Open {input.Query}", Symbol.Globe, OmnibarActionKind.OpenWebAddress, input.Query)
                    {
                        Subtitle = "In your web browser",
                        Group = "Web",
                        Hint = "Enter"
                    });
                    break;
                case OmnibarInputKind.NetworkAddress:
                    results.Add(new OmnibarSuggestion($"Connect to {Core.Platform.NetworkLocations.DisplayName(input.Query)}", Symbol.GlobeDesktop,
                        OmnibarActionKind.OpenNetwork, input.Query)
                    {
                        Subtitle = input.Query,
                        Group = "Network",
                        Hint = "Enter"
                    });
                    break;
                case OmnibarInputKind.Path:
                    await AddPathAsync(results, input, text, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    await AddTextAsync(results, input.Query, cancellationToken).ConfigureAwait(false);
                    break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            MarkGroups(results);
            return results;
        }

        // ------------------------------------------------------------------ paths

        private async Task AddPathAsync(List<OmnibarSuggestion> results, OmnibarInput input, string typed, CancellationToken token)
        {
            IReadOnlyList<DirectoryIndexEntry> entries = await _context.ListAsync(input.Folder, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (entries == null)
            {
                results.Add(new OmnibarSuggestion($"There's no folder at {input.Folder}", Symbol.ErrorCircle, OmnibarActionKind.None, null)
                {
                    Subtitle = "Check the path, or type words to search",
                    Group = "Location"
                });
                return;
            }

            string partial = input.Partial ?? string.Empty;
            bool showHidden = _context.ShowHidden || partial.StartsWith(".", StringComparison.Ordinal);
            string typedTrimmed = (typed ?? string.Empty).TrimEnd();
            string typedStem = partial.Length > 0 && typedTrimmed.Length >= partial.Length
                ? typedTrimmed.Substring(0, typedTrimmed.Length - partial.Length)
                : typedTrimmed;
            if (partial.Length == 0 && typedStem.Length > 0 && !PathUtil.IsSeparator(typedStem[typedStem.Length - 1]) && !input.EndsWithSeparator)
            {
                typedStem += PathUtil.Separator; // "~" or "C:" → completions continue after a separator
            }
            else if (partial.Length == 0 && input.EndsWithSeparator && typedStem.Length > 0 && !PathUtil.IsSeparator(typedStem[typedStem.Length - 1]))
            {
                typedStem += PathUtil.Separator; // "..": the folder itself, completions inside it
            }

            var scored = new List<(DirectoryIndexEntry Entry, int Score, int[] Positions, bool Prefix)>();
            foreach (DirectoryIndexEntry entry in entries)
            {
                if (!showHidden && entry.IsHidden)
                {
                    continue;
                }

                if (partial.Length == 0)
                {
                    scored.Add((entry, entry.IsDirectory ? 2 : 1, Array.Empty<int>(), true));
                    continue;
                }

                if (FuzzyMatcher.IsPrefix(partial, entry.Name))
                {
                    var positions = new int[partial.Length];
                    for (int i = 0; i < positions.Length; i++)
                    {
                        positions[i] = i;
                    }

                    scored.Add((entry, 10_000 + (entry.IsDirectory ? 500 : 0) - entry.Name.Length, positions, true));
                }
                else
                {
                    int score = FuzzyMatcher.Score(partial, entry.Name, out int[] positions);
                    if (score > 0)
                    {
                        scored.Add((entry, score + (entry.IsDirectory ? 20 : 0), positions, false));
                    }
                }
            }

            token.ThrowIfCancellationRequested();
            scored.Sort((a, b) =>
            {
                int byScore = b.Score.CompareTo(a.Score);
                return byScore != 0 ? byScore : NaturalCompare(a.Entry.Name, b.Entry.Name);
            });

            int count = Math.Min(scored.Count, MaxPathMatches);
            for (int i = 0; i < count; i++)
            {
                (DirectoryIndexEntry entry, int score, int[] positions, bool prefix) = scored[i];
                string full = PathUtil.Combine(input.Folder, entry.Name);
                bool browsable = entry.IsDirectory || Core.Services.ArchiveBrowser.IsArchiveFile(entry.Name);
                string suffix = entry.IsDirectory ? PathUtil.Separator.ToString() : string.Empty;
                results.Add(new OmnibarSuggestion(entry.Name, FileIconGlyphs.For(entry.Name, entry.IsDirectory),
                    browsable ? OmnibarActionKind.Navigate : OmnibarActionKind.Reveal, full)
                {
                    TitleHighlights = positions,
                    Subtitle = entry.IsDirectory ? null : FileOperations.FileOperationJob.FormatBytes(entry.SizeBytes),
                    Group = partial.Length == 0 ? "In " + (PathUtil.GetName(input.Folder) is { Length: > 0 } name ? name : input.Folder) : "Matches",
                    Hint = i == 0 ? (browsable ? "Open" : "Show") : null,
                    CompletionText = typedStem + entry.Name + suffix,
                    GhostSuffix = prefix && partial.Length > 0 ? entry.Name.Substring(partial.Length) + suffix : null,
                    Score = score
                });
            }

            if (count == 0)
            {
                results.Add(new OmnibarSuggestion(partial.Length == 0 ? "This folder is empty" : $"Nothing here matches \"{partial}\"",
                    Symbol.Info, OmnibarActionKind.None, null)
                {
                    Group = "Matches"
                });
            }
        }

        // ------------------------------------------------------------------ words

        private async Task AddTextAsync(List<OmnibarSuggestion> results, string query, CancellationToken token)
        {
            var seen = new HashSet<string>(PathUtil.Comparer);
            AddPlaces(results, query, seen);
            AddRecent(results, query, MaxRecent, seen);

            string current = _context.CurrentFolder;
            if (!string.IsNullOrEmpty(current))
            {
                IReadOnlyList<DirectoryIndexEntry> entries = await _context.ListAsync(current, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (entries != null)
                {
                    var here = new List<(DirectoryIndexEntry Entry, int Score, int[] Positions)>();
                    foreach (DirectoryIndexEntry entry in entries)
                    {
                        if (!_context.ShowHidden && entry.IsHidden)
                        {
                            continue;
                        }

                        int score = FuzzyMatcher.Score(query, entry.Name, out int[] positions);
                        if (score > 0)
                        {
                            here.Add((entry, score + (entry.IsDirectory ? 15 : 0), positions));
                        }
                    }

                    here.Sort((a, b) => b.Score.CompareTo(a.Score));
                    for (int i = 0; i < Math.Min(here.Count, MaxHere); i++)
                    {
                        (DirectoryIndexEntry entry, int score, int[] positions) = here[i];
                        string full = Path.Combine(current, entry.Name);
                        if (!seen.Add(full))
                        {
                            continue;
                        }

                        bool browsable = entry.IsDirectory || Core.Services.ArchiveBrowser.IsArchiveFile(entry.Name);
                        results.Add(new OmnibarSuggestion(entry.Name, FileIconGlyphs.For(entry.Name, entry.IsDirectory),
                            browsable ? OmnibarActionKind.Navigate : OmnibarActionKind.Reveal, full)
                        {
                            TitleHighlights = positions,
                            Subtitle = entry.IsDirectory ? null : FileOperations.FileOperationJob.FormatBytes(entry.SizeBytes),
                            Group = "In this folder",
                            CompletionText = full + (entry.IsDirectory ? PathUtil.Separator.ToString() : string.Empty),
                            GhostSuffix = Ghost(query, entry.Name),
                            Score = score
                        });
                    }
                }
            }

            AddCommands(results, query, MaxCommandsInText, MinCommandScoreInText);
            AddSearch(results, query);
        }

        private void AddPlaces(List<OmnibarSuggestion> results, string query, HashSet<string> seen)
        {
            var matches = new List<(OmnibarPlace Place, int Score, int[] Positions)>();
            foreach (OmnibarPlace place in _context.Places ?? Array.Empty<OmnibarPlace>())
            {
                int score = FuzzyMatcher.Score(query, place.Name, out int[] positions);
                if (score > 0)
                {
                    // Pinned and named places are deliberate: a small lead over equally good matches elsewhere.
                    matches.Add((place, score + 10, positions));
                }
            }

            matches.Sort((a, b) => b.Score.CompareTo(a.Score));
            int added = 0;
            foreach ((OmnibarPlace place, int score, int[] positions) in matches)
            {
                if (added == MaxPlaces)
                {
                    break;
                }

                if (place.Target != null && !seen.Add(place.Target))
                {
                    continue;
                }

                results.Add(new OmnibarSuggestion(place.Name, place.Icon, place.Action, place.Target)
                {
                    TitleHighlights = positions,
                    Subtitle = place.Subtitle ?? place.Target,
                    Group = "Places",
                    CompletionText = place.Action == OmnibarActionKind.Navigate && place.Target != null && !Core.Platform.NetworkLocations.IsNetworkAddress(place.Target)
                        ? PathUtil.WithTrailingSeparator(place.Target)
                        : null,
                    GhostSuffix = Ghost(query, place.Name),
                    Score = score
                });
                added++;
            }
        }

        private void AddRecent(List<OmnibarSuggestion> results, string query, int max, HashSet<string> exclude)
        {
            string current = _context.CurrentFolder;
            var matches = new List<(string Path, int Score, int[] Positions, string Name)>();
            int order = 0;
            foreach (string path in _context.RecentFolders ?? Array.Empty<string>())
            {
                order++;
                if (string.IsNullOrEmpty(path) || (current != null && PathUtil.AreSame(path, current)) || (exclude != null && exclude.Contains(path)))
                {
                    continue;
                }

                string name = PathUtil.GetName(path);
                if (string.IsNullOrEmpty(name))
                {
                    name = path;
                }

                if (query.Length == 0)
                {
                    matches.Add((path, 1000 - order, Array.Empty<int>(), name));
                    continue;
                }

                int score = FuzzyMatcher.Score(query, name, out int[] positions);
                if (score <= 0)
                {
                    // The folder's path also counts ("proj" finds ~/work/projects/app), well below a name match.
                    int pathScore = FuzzyMatcher.Score(query, path, out _);
                    if (pathScore <= 0)
                    {
                        continue;
                    }

                    score = pathScore / 3;
                    positions = Array.Empty<int>();
                }

                // More recent ones win ties.
                matches.Add((path, score - Math.Min(order, 20), positions, name));
            }

            matches.Sort((a, b) => b.Score.CompareTo(a.Score));
            int added = 0;
            foreach ((string path, int score, int[] positions, string name) in matches)
            {
                if (added == max)
                {
                    break;
                }

                if (exclude != null && !exclude.Add(path))
                {
                    continue;
                }

                results.Add(new OmnibarSuggestion(name, Symbol.History, OmnibarActionKind.Navigate, path)
                {
                    TitleHighlights = positions,
                    Subtitle = PathUtil.GetParent(path) ?? path,
                    Group = "Recent",
                    CompletionText = PathUtil.WithTrailingSeparator(path),
                    GhostSuffix = Ghost(query, name),
                    Score = score
                });
                added++;
            }
        }

        private void AddPinned(List<OmnibarSuggestion> results)
        {
            int added = 0;
            foreach (OmnibarPlace place in _context.Places ?? Array.Empty<OmnibarPlace>())
            {
                if (place.Kind != OmnibarPlaceKind.Pinned)
                {
                    continue;
                }

                if (added++ == MaxPinnedWhenEmpty)
                {
                    break;
                }

                results.Add(new OmnibarSuggestion(place.Name, place.Icon, place.Action, place.Target)
                {
                    Subtitle = place.Subtitle ?? place.Target,
                    Group = "Pinned"
                });
            }
        }

        private void AddCommands(List<OmnibarSuggestion> results, string query, int max, int minScore)
        {
            var matches = new List<(OmnibarCommand Command, int Score, int[] Positions)>();
            int order = 0;
            foreach (OmnibarCommand command in _context.Commands ?? Array.Empty<OmnibarCommand>())
            {
                order++;
                if (!SafeCanRun(command))
                {
                    continue;
                }

                if (string.IsNullOrEmpty(query))
                {
                    matches.Add((command, 1000 - order, Array.Empty<int>()));
                    continue;
                }

                int score = FuzzyMatcher.Score(query, command.Title, out int[] positions);
                int keywordScore = BestKeywordScore(query, command.Keywords) - 8;
                if (keywordScore > score)
                {
                    score = keywordScore;
                    positions = Array.Empty<int>();
                }

                if (score >= Math.Max(1, minScore))
                {
                    matches.Add((command, score, positions));
                }
            }

            matches.Sort((a, b) => b.Score.CompareTo(a.Score));
            for (int i = 0; i < Math.Min(max, matches.Count); i++)
            {
                (OmnibarCommand command, int score, int[] positions) = matches[i];
                results.Add(new OmnibarSuggestion(command.Title, command.Icon, OmnibarActionKind.RunCommand, null)
                {
                    TitleHighlights = positions,
                    Group = "Commands",
                    Hint = command.Gesture,
                    Command = command,
                    CompletionText = "> " + command.Title,
                    Score = score
                });
            }

            if (matches.Count == 0 && minScore == 0)
            {
                results.Add(new OmnibarSuggestion($"No command matches \"{query}\"", Symbol.Info, OmnibarActionKind.None, null) { Group = "Commands" });
            }
        }

        private void AddSearch(List<OmnibarSuggestion> results, string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                results.Add(new OmnibarSuggestion("Type what to search for", Symbol.Search, OmnibarActionKind.None, null) { Group = "Search" });
                return;
            }

            results.Add(new OmnibarSuggestion($"Search everywhere for \"{query}\"", Symbol.Search, OmnibarActionKind.SearchEverywhere, query)
            {
                Subtitle = "All indexed folders",
                Group = "Search"
            });

            if (!string.IsNullOrEmpty(_context.CurrentFolder))
            {
                results.Add(new OmnibarSuggestion($"Filter this folder by \"{query}\"", Symbol.Filter, OmnibarActionKind.FilterHere, query)
                {
                    Subtitle = PathUtil.GetName(_context.CurrentFolder),
                    Group = "Search"
                });
            }
        }

        // ------------------------------------------------------------------ helpers

        private static int BestKeywordScore(string query, string keywords)
        {
            int best = -1;
            if (string.IsNullOrEmpty(keywords))
            {
                return best;
            }

            foreach (string keyword in keywords.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                best = Math.Max(best, FuzzyMatcher.Score(query, keyword, out _));
            }

            return best;
        }

        private static bool SafeCanRun(OmnibarCommand command)
        {
            try
            {
                return command.CanRun();
            }
            catch (Exception)
            {
                return false; // a predicate that throws is a command that can't run now
            }
        }

        /// <summary>The faded rest of a name when the text typed so far is its beginning.</summary>
        private static string Ghost(string typed, string name) =>
            !string.IsNullOrEmpty(typed) && name != null && name.Length > typed.Length && name.StartsWith(typed, StringComparison.OrdinalIgnoreCase)
                ? name.Substring(typed.Length)
                : null;

        private static int NaturalCompare(string a, string b)
        {
            try
            {
                return Core.Native.NaturalStringComparer.Instance.Compare(a, b);
            }
            catch (Exception)
            {
                return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static void MarkGroups(List<OmnibarSuggestion> results)
        {
            string previous = null;
            foreach (OmnibarSuggestion suggestion in results)
            {
                suggestion.IsGroupStart = !string.Equals(suggestion.Group, previous, StringComparison.Ordinal);
                previous = suggestion.Group;
            }
        }
    }
}
