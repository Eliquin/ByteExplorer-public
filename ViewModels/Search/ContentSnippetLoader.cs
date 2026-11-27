// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ByteSearcher;

namespace ByteExplorer.ViewModels.Search
{
    /// <summary>
    /// Reads the "Contains: …" snippets of content results in the background, for the rows on screen only: a snippet
    /// means extracting the file's text again (the index keeps terms, not text), so it is done once per file and words,
    /// one file at a time on a low-priority thread, the most recently shown rows first (rows scrolled past are dropped
    /// from the queue), and kept in a small cache so scrolling back costs nothing.
    /// </summary>
    internal static class ContentSnippetLoader
    {
        private const int QueueLimit = 48;
        private const int CacheLimit = 1024;

        private static readonly object Gate = new object();
        private static readonly LinkedList<(SearchResultRow Row, Action<SearchResultRow> Done)> Queue = new LinkedList<(SearchResultRow, Action<SearchResultRow>)>();
        private static readonly Dictionary<string, string> Cache = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Queue<string> CacheOrder = new Queue<string>();
        private static bool _running;

        /// <summary>
        /// Asks for the row's snippet; <paramref name="done"/> runs on the UI thread once <see cref="SearchResultRow.Snippet"/>
        /// is set. UI thread.
        /// </summary>
        public static void Request(SearchResultRow row, Action<SearchResultRow> done)
        {
            if (row == null || row.SnippetRequested)
            {
                return;
            }

            row.SnippetRequested = true;
            string key = KeyOf(row);
            lock (Gate)
            {
                if (Cache.TryGetValue(key, out string cached))
                {
                    row.Snippet = cached ?? string.Empty;
                    done?.Invoke(row);
                    return;
                }

                Queue.AddFirst((row, done));
                while (Queue.Count > QueueLimit)
                {
                    Queue.Last.Value.Row.SnippetRequested = false; // asked again if it scrolls back into view
                    Queue.RemoveLast();
                }

                if (_running)
                {
                    return;
                }

                _running = true;
            }

            var thread = new Thread(Work) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Content snippets" };
            thread.Start();
        }

        private static void Work()
        {
            while (true)
            {
                (SearchResultRow Row, Action<SearchResultRow> Done) item;
                lock (Gate)
                {
                    if (Queue.Count == 0)
                    {
                        _running = false;
                        return;
                    }

                    item = Queue.First.Value;
                    Queue.RemoveFirst();
                }

                string snippet = null;
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    snippet = ContentMatcher.Snippet(item.Row.FullPath, item.Row.ContentWords, 160, cts.Token);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Logger.Debug(ex, "No snippet for {Path}", item.Row.FullPath);
                }

                string key = KeyOf(item.Row);
                lock (Gate)
                {
                    if (!Cache.ContainsKey(key))
                    {
                        Cache[key] = snippet;
                        CacheOrder.Enqueue(key);
                        while (CacheOrder.Count > CacheLimit)
                        {
                            Cache.Remove(CacheOrder.Dequeue());
                        }
                    }
                }

                SearchResultRow row = item.Row;
                Action<SearchResultRow> done = item.Done;
                Dispatcher.UIThread.Post(() =>
                {
                    row.Snippet = snippet ?? string.Empty;
                    done?.Invoke(row);
                }, DispatcherPriority.Background);
            }
        }

        private static string KeyOf(SearchResultRow row) =>
            row.FullPath + "\u0001" + row.ModifiedUtc.Ticks + "\u0001" + string.Join("\u0002", row.ContentWords);
    }
}
