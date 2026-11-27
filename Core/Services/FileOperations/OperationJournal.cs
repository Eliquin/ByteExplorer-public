// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Native;
using ByteExplorer.Core.Platform;
using Serilog;

namespace ByteExplorer.Core.Services.FileOperations
{
    /// <summary>
    /// The write-ahead journal of one running copy or move, so a crash, a killed process or a power cut can be cleaned
    /// up at the next start (<see cref="OperationRecovery"/>). One small text file per operation in the journal folder,
    /// one line per step that leaves something to undo if the process dies in the middle of it:
    /// <list type="bullet">
    /// <item><c>H</c>: the operation (kind, destination, sources, start time).</item>
    /// <item><c>T</c> / <c>C</c>: a hidden temporary file was created / is gone again (renamed to its name, or deleted).</item>
    /// <item><c>R</c> / <c>r</c>: a Replace moved the old item to the trash (where to), and the new one is not in place
    /// yet / the Replace finished or was rolled back.</item>
    /// </list>
    /// Lines are flushed to the operating system at once (a killed process loses nothing); the Replace lines, rare and
    /// the ones that matter after a power cut, are also put on the disk. The file is deleted when the operation ends
    /// normally, so a journal found at startup is an operation that was interrupted. Journaling never stops an
    /// operation: if the journal can't be written, the operation goes on without it (and logs why).
    /// </summary>
    public sealed class OperationJournal : IDisposable
    {
        private const char Separator = '\t';
        private const char ListSeparator = '\u001F';
        private readonly FileStream _stream;
        private readonly object _gate = new object();
        private bool _disposed;

        private OperationJournal(string path, FileStream stream)
        {
            Path = path;
            _stream = stream;
        }

        /// <summary>The journal file.</summary>
        public string Path { get; }

        /// <summary>Starts the journal of an operation, or returns null (no journal folder, or it can't be written).</summary>
        public static OperationJournal Start(string folder, FileOperationKind kind, IReadOnlyList<string> sources, string destination)
        {
            if (string.IsNullOrEmpty(folder))
            {
                return null;
            }

            try
            {
                Directory.CreateDirectory(folder);
                string path = System.IO.Path.Combine(folder, $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.journal");
                // Exclusive (FileShare.None: a sharing lock on Windows, an flock on Linux and macOS) for as long as the
                // operation runs: the recovery of another ByteExplorer window leaves a journal it can't lock alone, so it
                // never touches a running operation. The system drops the lock when the process dies.
                var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.None);
                var journal = new OperationJournal(path, stream);
                journal.Write(true, "H", kind.ToString(), destination, string.Join(ListSeparator, sources),
                    DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                return journal;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Logger.Warning(ex, "Could not start the operation journal in {Folder}; continuing without one", folder);
                return null;
            }
        }

        /// <summary>A hidden temporary file is about to be written.</summary>
        public void TemporaryCreated(string temporary) => Write(false, "T", temporary);

        /// <summary>The temporary file took its name, or was deleted.</summary>
        public void TemporaryGone(string temporary) => Write(false, "C", temporary);

        /// <summary>A Replace put the old item in the trash; the new one (in <paramref name="temporary"/>, if any) is not in place yet.</summary>
        public void Replacing(string destination, TrashedItem trashed, string temporary) =>
            Write(true, "R", destination, trashed?.TrashPath ?? string.Empty, trashed?.InfoPath ?? string.Empty, temporary ?? string.Empty);

        /// <summary>The Replace of <paramref name="destination"/> finished (or was rolled back).</summary>
        public void Replaced(string destination) => Write(false, "r", destination);

        /// <summary>Tests only: what a crash leaves: the lock is released, the journal stays.</summary>
        internal void AbandonForTests()
        {
            lock (_gate)
            {
                _disposed = true;
                _stream.Dispose();
            }
        }

        /// <summary>The operation ended normally: nothing to recover.</summary>
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                try
                {
                    _stream.Dispose();
                    File.Delete(Path);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Log.Logger.Warning(ex, "Could not remove the finished operation journal {Path}", Path);
                }
            }
        }

        private void Write(bool toDisk, string kind, params string[] fields)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                try
                {
                    var line = new StringBuilder(kind);
                    foreach (string field in fields)
                    {
                        line.Append(Separator).Append(Escape(field));
                    }

                    line.Append('\n');
                    byte[] bytes = Encoding.UTF8.GetBytes(line.ToString());
                    _stream.Write(bytes, 0, bytes.Length);
                    _stream.Flush(flushToDisk: toDisk);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ObjectDisposedException)
                {
                    Log.Logger.Warning(ex, "Could not write to the operation journal {Path}", Path);
                }
            }
        }

        // Paths can't hold a newline on Windows but can on Linux; tabs and newlines are escaped so a line stays a line.
        private static string Escape(string value) =>
            (value ?? string.Empty).Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "\\r");

        internal static string Unescape(string value)
        {
            var result = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] == '\\' && i + 1 < value.Length)
                {
                    char next = value[++i];
                    result.Append(next switch { 't' => '\t', 'n' => '\n', 'r' => '\r', _ => next });
                }
                else
                {
                    result.Append(value[i]);
                }
            }

            return result.ToString();
        }

        /// <summary>
        /// The journal's whole lines, or null while its operation is still running (another window holds its lock).
        /// </summary>
        internal static List<string[]> ReadLinesIfAbandoned(string path)
        {
            string text;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                text = reader.ReadToEnd();
            }
            catch (IOException)
            {
                return null; // locked: a live operation
            }

            return ParseLines(text).ToList();
        }

        private static IEnumerable<string[]> ParseLines(string text)
        {
            // The last line may be cut off by the crash: only whole lines (ending in a newline) count.
            int end = text.LastIndexOf('\n');
            if (end < 0)
            {
                yield break;
            }

            foreach (string line in text.Substring(0, end).Split('\n'))
            {
                if (line.Length > 0)
                {
                    yield return line.Split(Separator).Select(Unescape).ToArray();
                }
            }
        }

        internal static IReadOnlyList<string> SplitList(string value) =>
            value.Length == 0 ? Array.Empty<string>() : value.Split(ListSeparator);
    }

    /// <summary>What the startup recovery found and did for one interrupted operation.</summary>
    public sealed class RecoveredOperation
    {
        public RecoveredOperation(string description, DateTime startedUtc, int temporaryFilesRemoved, int replacesCompleted, int replacesRolledBack, IReadOnlyList<string> problems)
        {
            Description = description;
            StartedUtc = startedUtc;
            TemporaryFilesRemoved = temporaryFilesRemoved;
            ReplacesCompleted = replacesCompleted;
            ReplacesRolledBack = replacesRolledBack;
            Problems = problems;
        }

        /// <summary>"Copy 3 items to Backup".</summary>
        public string Description { get; }

        public DateTime StartedUtc { get; }

        public int TemporaryFilesRemoved { get; }

        /// <summary>Replaces whose new copy was complete: it was put in place.</summary>
        public int ReplacesCompleted { get; }

        /// <summary>Replaces whose new copy was not complete: the old item came back from the trash.</summary>
        public int ReplacesRolledBack { get; }

        /// <summary>Steps that could not be put right (a message each); empty when everything was.</summary>
        public IReadOnlyList<string> Problems { get; }
    }

    /// <summary>
    /// Puts right what interrupted operations left (<see cref="OperationJournal"/>), at startup before any new operation:
    /// <list type="bullet">
    /// <item>A Replace caught between "old item to the trash" and "new item in place": if the new copy was complete (its
    /// temporary file is there, or the source was being renamed and the destination is still free), it is put in place;
    /// otherwise the old item comes back from the trash. Either way the name holds a whole item again.</item>
    /// <item>Every hidden temporary file that was never committed is deleted (the source it was copied from is intact:
    /// nothing is deleted before its copy is committed).</item>
    /// </list>
    /// Each journal is removed once handled; one that can't be handled (a recovery step failing) is kept and reported.
    /// </summary>
    public static class OperationRecovery
    {
        public static IReadOnlyList<RecoveredOperation> Recover(string folder)
        {
            var recovered = new List<RecoveredOperation>();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                return recovered;
            }

            string[] journals;
            try
            {
                journals = Directory.GetFiles(folder, "*.journal");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Logger.Warning(ex, "Could not read the operation journals in {Folder}", folder);
                return recovered;
            }

            foreach (string journal in journals.OrderBy(j => j, StringComparer.Ordinal))
            {
                try
                {
                    RecoveredOperation operation = RecoverOne(journal);
                    if (operation != null)
                    {
                        recovered.Add(operation);
                    }
                }
                catch (Exception ex)
                {
                    Log.Logger.Error(ex, "Recovering the interrupted operation in {Journal} failed", journal);
                    recovered.Add(new RecoveredOperation("A file operation", DateTime.MinValue, 0, 0, 0,
                        new[] { "Its recovery failed (" + ex.Message + "); its journal was kept: " + journal }));
                }
            }

            return recovered;
        }

        private static RecoveredOperation RecoverOne(string journal)
        {
            var temporaries = new List<string>();
            var gone = new HashSet<string>(StringComparer.Ordinal);
            var replacing = new Dictionary<string, (string TrashPath, string InfoPath, string Temporary)>(StringComparer.Ordinal);
            string description = "A file operation";
            DateTime started = DateTime.MinValue;

            List<string[]> lines = OperationJournal.ReadLinesIfAbandoned(journal);
            if (lines == null)
            {
                Log.Logger.Information("Operation journal {Journal} belongs to a running operation (another window); left alone", journal);
                return null;
            }

            foreach (string[] fields in lines)
            {
                switch (fields[0])
                {
                    case "H" when fields.Length >= 5:
                        IReadOnlyList<string> sources = OperationJournal.SplitList(fields[3]);
                        string what = sources.Count == 1 ? $"\"{PathUtil.GetName(sources[0])}\"" : $"{sources.Count:N0} items";
                        description = $"{fields[1]} {what} to \"{PathUtil.GetName(fields[2])}\"";
                        DateTime.TryParse(fields[4], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out started);
                        break;
                    case "T" when fields.Length >= 2:
                        temporaries.Add(fields[1]);
                        break;
                    case "C" when fields.Length >= 2:
                        gone.Add(fields[1]);
                        break;
                    case "R" when fields.Length >= 5:
                        replacing[fields[1]] = (fields[2], fields[3], fields[4]);
                        break;
                    case "r" when fields.Length >= 2:
                        replacing.Remove(fields[1]);
                        break;
                }
            }

            int completed = 0, rolledBack = 0, removed = 0;
            var problems = new List<string>();

            // Replaces first: a complete temporary file is the new item, needed before the leftovers are deleted.
            foreach ((string destination, (string trashPath, string infoPath, string temporary)) in replacing)
            {
                try
                {
                    if (File.Exists(destination) || Directory.Exists(destination))
                    {
                        continue; // the new item got its name before the crash; only the journal line was missing
                    }

                    if (temporary.Length > 0 && File.Exists(temporary) && !gone.Contains(temporary))
                    {
                        NativeFileOperations.MoveFile(temporary, destination, writeThrough: true); // the verified new copy
                        gone.Add(temporary);
                        completed++;
                    }
                    else if (trashPath.Length == 0)
                    {
                        continue; // the volume had no trash: the old item was overwritten on purpose; the source is intact
                    }
                    else if (File.Exists(trashPath) || Directory.Exists(trashPath))
                    {
                        Trash.Restore(new TrashedItem(destination, trashPath, infoPath.Length > 0 ? infoPath : null));
                        rolledBack++;
                    }
                    else
                    {
                        problems.Add($"\"{PathUtil.GetName(destination)}\" was being replaced; its earlier version is no longer in the trash.");
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    problems.Add($"\"{PathUtil.GetName(destination)}\" could not be put back: {ex.Message}");
                }
            }

            foreach (string temporary in temporaries)
            {
                if (!gone.Contains(temporary) && File.Exists(temporary) && IsOurTemporary(temporary))
                {
                    NativeFileOperations.TryDeleteTemporaryFile(temporary);
                    if (!File.Exists(temporary))
                    {
                        removed++;
                    }
                }
            }

            if (problems.Count == 0)
            {
                File.Delete(journal);
            }

            Log.Logger.Information("Recovered an interrupted operation ({Description}): {Removed} temporary file(s) removed, {Completed} replace(s) completed, {RolledBack} rolled back, {Problems} problem(s)",
                description, removed, completed, rolledBack, problems.Count);
            return new RecoveredOperation(description, started, removed, completed, rolledBack, problems);
        }

        /// <summary>Only files named as the engine names its temporaries are ever deleted (a damaged journal can't delete anything else).</summary>
        private static bool IsOurTemporary(string path)
        {
            return OperationTemporaries.IsTemporaryName(System.IO.Path.GetFileName(path));
        }
    }
}
