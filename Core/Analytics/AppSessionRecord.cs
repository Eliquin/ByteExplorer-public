// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;

namespace ByteExplorer.Core.Analytics
{
    /// <summary>
    /// One app run. LastAliveUtc does double duty: on a clean shutdown
    /// it's set to the true end time; while the app is still running,
    /// it's refreshed periodically (see UserAnalyticsService's
    /// heartbeat) so an ungraceful termination — a crash, a forced
    /// close, a Windows logoff — still leaves a reasonably accurate end
    /// time on disk rather than a session that looks like it never
    /// ended, or one silently missing from the history altogether.
    /// </summary>
    public sealed class AppSessionRecord
    {
        public DateTime StartUtc { get; set; }
        public DateTime LastAliveUtc { get; set; }
        public int FoldersVisited { get; set; }
    }
}
