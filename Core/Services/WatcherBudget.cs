// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Threading;

namespace ByteExplorer.Core.Services
{
    /// <summary>
    /// A shared, thread-safe cap on how many live folder-change watchers
    /// (see <see cref="FolderChangeWatcher"/>) may be active at the same
    /// time across the navigation tree.
    ///
    /// Why this exists: the tree only ever watches folders that are
    /// currently expanded (bounded, visible state — collapsing a node
    /// releases its watcher), which is already a sane scope on its own.
    /// This budget is the defense for the case that scope doesn't
    /// cover: a person who expands a genuinely large number of nodes at
    /// once (expand-all on a deep tree, or simply a lot of drives each
    /// with several expanded folders). Each live watcher holds a real OS
    /// handle; there's no reason to let that grow unbounded when a firm
    /// cap costs nothing for the overwhelmingly common case and fails
    /// safely (a folder beyond the cap simply doesn't auto-refresh,
    /// exactly as if this feature didn't exist for it) for the rare one.
    ///
    /// Implemented as a lock-free compare-and-swap counter rather than a
    /// SemaphoreSlim specifically because acquisition here is a single
    /// non-blocking "is there room right now" check — a node that can't
    /// get a slot doesn't want to wait for one, it wants to know
    /// immediately and move on.
    /// </summary>
    public sealed class WatcherBudget
    {
        private readonly int _maxConcurrent;
        private int _current;

        public WatcherBudget(int maxConcurrent)
        {
            _maxConcurrent = maxConcurrent < 1 ? 1 : maxConcurrent;
        }

        /// <summary>Current number of active watchers counted against this budget. For diagnostics only.</summary>
        public int Current => Volatile.Read(ref _current);

        public int MaxConcurrent => _maxConcurrent;

        /// <summary>
        /// Attempts to reserve one slot. Returns false immediately,
        /// without blocking, if the budget is exhausted. Every
        /// successful call MUST be paired with exactly one later call to
        /// <see cref="Release"/> once that watcher stops.
        /// </summary>
        public bool TryAcquire()
        {
            while (true)
            {
                int current = Volatile.Read(ref _current);
                if (current >= _maxConcurrent)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _current, current + 1, current) == current)
                {
                    return true;
                }

                // Another thread changed _current between the read and
                // the compare-and-swap — retry against the fresh value
                // rather than assuming failure.
            }
        }

        /// <summary>
        /// Releases a previously-acquired slot. Safe to call at most
        /// once per successful <see cref="TryAcquire"/> — callers are
        /// responsible for that pairing (this class has no way to
        /// distinguish a legitimate release from a double-release, so
        /// guarding against calling this more than once per acquire is
        /// the caller's job, same as any other acquire/release resource).
        /// </summary>
        public void Release()
        {
            Interlocked.Decrement(ref _current);
        }
    }
}
