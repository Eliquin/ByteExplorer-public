// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Core.Models;
using ByteExplorer.Icons;
using ByteExplorer.ViewModels.Omnibar;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>The omnibar's suggestions for each kind of input, against a fake context (no UI, no disk).</summary>
    public sealed class OmnibarEngineTests
    {
        private static readonly string Root = Path.Combine(Path.GetTempPath(), "omni");
        private static readonly string Home = Path.Combine(Root, "home");
        private readonly FakeContext _context = new FakeContext();
        private readonly OmnibarSuggestionEngine _engine;

        public OmnibarEngineTests()
        {
            _engine = new OmnibarSuggestionEngine(_context);
            _context.Folders[Home] = new[]
            {
                Dir("Documents"), Dir("Downloads"), Dir("Desktop"), Dir(".config", hidden: true), File("notes.txt", 2048), Dir("Music")
            };
            _context.Folders[Path.Combine(Home, "Documents")] = new[] { Dir("Taxes"), File("cv.pdf", 100) };
            _context.CurrentFolder = Home;
        }

        [Fact]
        public async Task Path_PrefixMatchesFirst_FoldersBeforeFiles_WithGhost()
        {
            var list = await _engine.GetSuggestionsAsync(Path.Combine(Home, "Do"), CancellationToken.None);
            Assert.Equal(new[] { "Downloads", "Documents" }.OrderBy(n => n.Length).ThenBy(n => n), list.Take(2).Select(s => s.Title).OrderBy(n => n.Length).ThenBy(n => n));
            OmnibarSuggestion top = list[0];
            Assert.Equal(OmnibarActionKind.Navigate, top.Action);
            Assert.NotNull(top.GhostSuffix);
            Assert.EndsWith(Path.DirectorySeparatorChar.ToString(), top.CompletionText);
            Assert.StartsWith(Path.Combine(Home, "Do"), top.CompletionText);
            Assert.Equal(new[] { 0, 1 }, top.TitleHighlights);
        }

        [Fact]
        public async Task Path_KeepsTheTypedStyle_ForCompletion()
        {
            _context.HomeFolder = Home;
            var list = await _engine.GetSuggestionsAsync("~/Doc", CancellationToken.None);
            Assert.Equal("Documents", list[0].Title);
            Assert.Equal("~/Documents" + Path.DirectorySeparatorChar, list[0].CompletionText);
            Assert.Equal("uments" + Path.DirectorySeparatorChar, list[0].GhostSuffix);
        }

        [Fact]
        public async Task Path_AfterASeparator_ListsTheFolder_WithoutHiddenItems()
        {
            var list = await _engine.GetSuggestionsAsync(Home + Path.DirectorySeparatorChar, CancellationToken.None);
            Assert.DoesNotContain(list, s => s.Title == ".config");
            Assert.Contains(list, s => s.Title == "notes.txt" && s.Action == OmnibarActionKind.Reveal);

            var dot = await _engine.GetSuggestionsAsync(Path.Combine(Home, ".c"), CancellationToken.None);
            Assert.Equal(".config", dot[0].Title); // typing a dot asks for hidden items
        }

        [Fact]
        public async Task Path_InAFolderThatDoesNotExist_SaysSo()
        {
            var list = await _engine.GetSuggestionsAsync(Path.Combine(Root, "nope", "x"), CancellationToken.None);
            Assert.Single(list);
            Assert.Equal(OmnibarActionKind.None, list[0].Action);
        }

        [Fact]
        public async Task Words_MatchPlacesRecentHereCommandsAndSearch()
        {
            _context.Places.Add(new OmnibarPlace("Downloads", Path.Combine(Home, "Downloads"), Symbol.ArrowDownload, OmnibarPlaceKind.Place, OmnibarActionKind.Navigate));
            _context.Recent.Add(Path.Combine(Root, "work", "download-stats"));
            _context.Commands.Add(new OmnibarCommand("New folder", "mkdir create", Symbol.FolderAdd, "Ctrl+Shift+N", null, () => { }));

            var list = await _engine.GetSuggestionsAsync("downl", CancellationToken.None);

            Assert.Equal("Downloads", list[0].Title);
            Assert.Equal("Places", list[0].Group);
            Assert.True(list[0].IsGroupStart);
            Assert.Contains(list, s => s.Group == "Recent" && s.Title == "download-stats");
            Assert.DoesNotContain(list, s => s.Group == "In this folder" && s.Title == "Downloads"); // already offered as a place
            Assert.Equal(OmnibarActionKind.SearchEverywhere, list[list.Count - 2].Action);
            Assert.Equal(OmnibarActionKind.FilterHere, list[list.Count - 1].Action);
            Assert.DoesNotContain(list, s => s.Group == "Commands"); // "downl" is nothing like a command
        }

        [Fact]
        public async Task Commands_ByTitleOrKeyword_SkippingOnesThatCantRun()
        {
            bool canPaste = false;
            _context.Commands.Add(new OmnibarCommand("New folder", "mkdir create directory", Symbol.FolderAdd, "Ctrl+Shift+N", null, () => { }));
            _context.Commands.Add(new OmnibarCommand("Paste", "clipboard", Symbol.ClipboardPaste, "Ctrl+V", () => canPaste, () => { }));

            var byTitle = await _engine.GetSuggestionsAsync("> new fo", CancellationToken.None);
            Assert.Equal("New folder", byTitle[0].Title);
            Assert.Equal("Ctrl+Shift+N", byTitle[0].Hint);

            var byKeyword = await _engine.GetSuggestionsAsync(">mkdir", CancellationToken.None);
            Assert.Equal("New folder", byKeyword[0].Title);

            var all = await _engine.GetSuggestionsAsync(">", CancellationToken.None);
            Assert.DoesNotContain(all, s => s.Title == "Paste");
        }

        [Fact]
        public async Task Empty_OrTheCurrentPath_ShowsRecentAndPinned()
        {
            _context.Recent.AddRange(new[] { Path.Combine(Root, "a"), Home, Path.Combine(Root, "b") });
            _context.Places.Add(new OmnibarPlace("Taxes", Path.Combine(Home, "Documents", "Taxes"), Symbol.Pin, OmnibarPlaceKind.Pinned, OmnibarActionKind.Navigate));

            var list = await _engine.GetSuggestionsAsync(Home, CancellationToken.None); // what editing starts with
            Assert.Equal(new[] { "a", "b" }, list.Where(s => s.Group == "Recent").Select(s => s.Title)); // not the current folder
            Assert.Contains(list, s => s.Group == "Pinned" && s.Title == "Taxes");
        }

        [Fact]
        public async Task WebAndNetworkAddresses()
        {
            var web = await _engine.GetSuggestionsAsync("https://example.com/docs", CancellationToken.None);
            Assert.Equal(OmnibarActionKind.OpenWebAddress, web.Single().Action);
            var network = await _engine.GetSuggestionsAsync("smb://nas/photos", CancellationToken.None);
            Assert.Equal(OmnibarActionKind.OpenNetwork, network.Single().Action);
        }

        [Fact]
        public async Task Cancellation_Stops()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _engine.GetSuggestionsAsync(Home + Path.DirectorySeparatorChar, cts.Token));
        }

        [Fact]
        public async Task LargeFolder_IsCappedAndFast()
        {
            string big = Path.Combine(Root, "big");
            _context.Folders[big] = Enumerable.Range(0, 100_000).Select(i => File($"file{i:D6}.dat", i)).ToArray();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var list = await _engine.GetSuggestionsAsync(Path.Combine(big, "file09"), CancellationToken.None);
            watch.Stop();
            Assert.Equal(OmnibarSuggestionEngine.MaxPathMatches, list.Count);
            Assert.True(watch.ElapsedMilliseconds < 1500, $"{watch.ElapsedMilliseconds} ms");
        }

        private static DirectoryIndexEntry Dir(string name, bool hidden = false) =>
            new DirectoryIndexEntry(name, true, 0, DateTime.UtcNow, FileAttributes.Directory | (hidden ? FileAttributes.Hidden : 0));

        private static DirectoryIndexEntry File(string name, long size) =>
            new DirectoryIndexEntry(name, false, size, DateTime.UtcNow, FileAttributes.Normal);

        private sealed class FakeContext : IOmnibarContext
        {
            public Dictionary<string, DirectoryIndexEntry[]> Folders { get; } = new Dictionary<string, DirectoryIndexEntry[]>();
            public List<string> Recent { get; } = new List<string>();
            public List<OmnibarPlace> Places { get; } = new List<OmnibarPlace>();
            public List<OmnibarCommand> Commands { get; } = new List<OmnibarCommand>();
            public string CurrentFolder { get; set; }
            public string HomeFolder { get; set; } = Home;
            public bool ShowHidden { get; set; }
            IReadOnlyList<string> IOmnibarContext.RecentFolders => Recent;
            IReadOnlyList<OmnibarPlace> IOmnibarContext.Places => Places;
            IReadOnlyList<OmnibarCommand> IOmnibarContext.Commands => Commands;

            public Task<IReadOnlyList<DirectoryIndexEntry>> ListAsync(string folder, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult<IReadOnlyList<DirectoryIndexEntry>>(Folders.TryGetValue(folder, out var entries) ? entries : null);
            }
        }
    }
}
