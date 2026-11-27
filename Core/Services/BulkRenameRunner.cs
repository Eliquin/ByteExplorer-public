// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using ByteExplorer.Core.Services.FileOperations;

namespace ByteExplorer.Core.Services
{
    /// <summary>What a bulk rename did: the moves it made (for the undo record), what was renamed, and what failed.</summary>
    public sealed class BulkRenameResult
    {
        /// <summary>Every move made, in order, temporary names included, so undoing them last to first puts everything back.</summary>
        public List<MovedAction> Steps { get; } = new List<MovedAction>();

        /// <summary>The renames that reached their new name.</summary>
        public List<BulkRenameMove> Renamed { get; } = new List<BulkRenameMove>();

        /// <summary>The items that couldn't be renamed (their old name) and why.</summary>
        public List<(string Name, string Message)> Failures { get; } = new List<(string, string)>();
    }

    /// <summary>
    /// Performs the renames of a bulk rename, never replacing anything. Two cases need a temporary name first, so they go
    /// in two steps (old name, temporary name, new name):
    /// <list type="bullet">
    /// <item>a new name that is the old name of another item of the batch (a→b while b→c, or the cycle a→b, b→a);</item>
    /// <item>a change of letter case only on a case-insensitive file system (Windows, macOS), where the new name "exists"
    /// because it is the item itself (Directory.Move refuses that).</item>
    /// </list>
    /// The others are renamed directly, before the second steps, so the names they free are free when needed. A failure
    /// leaves that item alone (an item already at its temporary name gets its old name back) and the rest go on.
    /// </summary>
    public static class BulkRenameRunner
    {
        private sealed class State
        {
            public BulkRenameMove Move;
            public string Current;
            public MovedAction FirstStep;
            public bool Failed;
        }

        public static BulkRenameResult Run(IReadOnlyList<BulkRenameMove> moves, bool caseInsensitive)
        {
            var result = new BulkRenameResult();
            if (moves == null || moves.Count == 0)
            {
                return result;
            }

            StringComparer comparer = caseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var sources = new HashSet<string>(comparer);
            foreach (BulkRenameMove move in moves)
            {
                sources.Add(move.From);
            }

            var direct = new List<State>();
            var viaTemporary = new List<State>();
            foreach (BulkRenameMove move in moves)
            {
                if (string.Equals(move.From, move.To, StringComparison.Ordinal))
                {
                    continue;
                }

                var state = new State { Move = move, Current = move.From };
                (sources.Contains(move.To) ? viaTemporary : direct).Add(state);
            }

            // Step 1: out of the way, under a temporary name.
            foreach (State state in viaTemporary)
            {
                string temporary = TemporaryName(state.Move.From);
                if (TryMove(state, temporary, result))
                {
                    state.FirstStep = result.Steps[result.Steps.Count - 1];
                }
            }

            // Step 2: the direct renames, which free their old names.
            foreach (State state in direct)
            {
                if (TryMove(state, state.Move.To, result))
                {
                    result.Renamed.Add(state.Move);
                }
            }

            // Step 3: from the temporary names to the new names.
            foreach (State state in viaTemporary)
            {
                if (state.Failed)
                {
                    continue;
                }

                if (TryMove(state, state.Move.To, result))
                {
                    result.Renamed.Add(state.Move);
                    continue;
                }

                // The new name is still taken (its item failed to move): back to the old name.
                try
                {
                    if (Exists(state.Move.From))
                    {
                        throw new IOException("Its old name was taken meanwhile.");
                    }

                    Move(state.Current, state.Move.From, state.Move.IsDirectory);
                    result.Steps.Remove(state.FirstStep); // out and back again: nothing for the undo to do
                }
                catch (Exception ex) when (IsFileError(ex))
                {
                    // Never leave it silently under the temporary name: say where it is (the undo also puts it back).
                    int last = result.Failures.Count - 1;
                    result.Failures[last] = (result.Failures[last].Name,
                        result.Failures[last].Message + $" It couldn't get its old name back and is now \"{Path.GetFileName(state.Current)}\".");
                }
            }

            return result;
        }

        private static bool TryMove(State state, string destination, BulkRenameResult result)
        {
            try
            {
                if (Exists(destination))
                {
                    throw new IOException($"\"{Path.GetFileName(destination)}\" already exists.");
                }

                Move(state.Current, destination, state.Move.IsDirectory);
                result.Steps.Add(new MovedAction(state.Current, destination));
                state.Current = destination;
                return true;
            }
            catch (Exception ex) when (IsFileError(ex))
            {
                state.Failed = true;
                result.Failures.Add((Path.GetFileName(state.Move.From), ex.Message));
                return false;
            }
        }

        private static void Move(string from, string to, bool isDirectory)
        {
            if (isDirectory)
            {
                Directory.Move(from, to);
            }
            else
            {
                File.Move(from, to, overwrite: false);
            }
        }

        private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

        private static bool IsFileError(Exception ex) =>
            ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException;

        /// <summary>A free hidden name beside the item ("." first: hidden on Unix while it lasts).</summary>
        private static string TemporaryName(string path)
        {
            string folder = Path.GetDirectoryName(path) ?? string.Empty;
            string candidate;
            do
            {
                candidate = Path.Combine(folder, ".renaming-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            }
            while (Exists(candidate));

            return candidate;
        }
    }
}
