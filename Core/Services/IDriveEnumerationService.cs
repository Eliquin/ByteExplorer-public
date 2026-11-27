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
    /// Detects the drives attached to the machine. Isolated behind an
    /// interface so the navigation view model never depends on
    /// System.IO.DriveInfo directly, and so this can be swapped for a
    /// faster native implementation later without touching the UI layer.
    /// </summary>
    public interface IDriveEnumerationService
    {
        Task<IReadOnlyList<DriveNodeInfo>> GetDrivesAsync(CancellationToken cancellationToken = default);
    }
}
