// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Core.Models;

namespace ByteExplorer.Core.Services
{
    /// <summary>
    /// Enumerates the immediate subdirectories of a path, off the UI
    /// thread. Deliberately narrow — this is the tree's "expand a node"
    /// primitive, not a general filesystem browser. The main file list
    /// (Phase 3) gets its own service so the two can be tuned
    /// independently (the tree only ever needs directory names; the
    /// main view needs full metadata for potentially millions of
    /// entries).
    ///
    /// includeHiddenAndSystem is passed in by the caller (rather than
    /// this service reading settings itself) so the service stays a
    /// pure, stateless, independently testable function of its inputs.
    /// </summary>
    public interface IFileSystemNavigationService
    {
        Task<IReadOnlyList<DirectoryNodeInfo>> GetSubdirectoriesAsync(string path, bool includeHiddenAndSystem, CancellationToken cancellationToken = default);
    }
}
