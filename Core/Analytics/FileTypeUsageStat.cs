// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;

namespace ByteExplorer.Core.Analytics
{
    /// <summary>Running open-count totals for one file extension (e.g. ".jpg"), or "(none)" for extensionless files.</summary>
    public sealed class FileTypeUsageStat
    {
        public string Extension { get; set; }
        public int OpenCount { get; set; }
        public DateTime LastOpenedUtc { get; set; }
    }
}
