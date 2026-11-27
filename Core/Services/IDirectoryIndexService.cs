// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Core.Models;

namespace ByteExplorer.Core.Services
{
    /// <summary>
    /// Builds the full, sorted DirectoryIndex for a folder — the
    /// engine behind the middle pane. Isolated behind an interface so
    /// the file-list view model never depends on the native enumerator
    /// directly.
    ///
    /// Transparently caches recent results (see DirectoryIndexService's
    /// remarks) — callers don't need to know or care whether a given
    /// call actually touched disk. bypassCache exists for the
    /// benchmark tool, which needs real, uncached numbers.
    /// </summary>
    public interface IDirectoryIndexService
    {
        Task<DirectoryIndex> BuildIndexAsync(string path, int networkTimeoutMs, bool bypassCache = false, CancellationToken cancellationToken = default);

        /// <summary>
        /// Evicts a specific folder's cached index, if present. Needed
        /// after a Move: the destination folder gets reloaded directly
        /// (LoadFolderAsync with bypassCache), but the source folder
        /// isn't otherwise touched at all, so without this its cached
        /// listing would keep showing an item that's already gone
        /// until the TTL happened to expire on its own.
        /// </summary>
        void InvalidateCache(string path);
    }
}
