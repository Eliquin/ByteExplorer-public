// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using Avalonia.Headless.XUnit;
using Xunit;

namespace ByteExplorer.Tests
{
    public sealed class MediaEngineTests
    {
        /// <summary>
        /// Where libvlc is installed (Linux: libvlc.so.5 present), the app's startup must load it even without the
        /// unversioned libvlc.so from the -dev package. Skipped silently where VLC is not installed.
        /// </summary>
        [AvaloniaFact]
        public void LibVlc_LoadsFromVersionedSonameOnLinux()
        {
            if (!OperatingSystem.IsLinux() || !File.Exists("/usr/lib/x86_64-linux-gnu/libvlc.so.5"))
            {
                return;
            }

            Assert.True(MediaEngine.IsAvailable);
        }
    }
}
