// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace ByteExplorer.Core.Platform
{
    /// <summary>
    /// Where an item went in the trash, enough to put it back: its original path, where its content is now, and the
    /// metadata file that lists it in the trash (freedesktop.org .trashinfo, Windows $I file; none on macOS).
    /// </summary>
    public sealed class TrashedItem
    {
        public TrashedItem(string originalPath, string trashPath, string infoPath)
        {
            OriginalPath = originalPath;
            TrashPath = trashPath;
            InfoPath = infoPath;
        }

        public string OriginalPath { get; }
        public string TrashPath { get; }

        /// <summary>The trash's record of the item; null where the trash keeps none (macOS).</summary>
        public string InfoPath { get; }
    }
}
