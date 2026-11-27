// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>The application icon (Assets/Icon, built by Tools/Icon/make_icon.py) is embedded and complete.</summary>
    public sealed class AppIconTests
    {
        [AvaloniaFact]
        public void Icon_IsAnAvaloniaResource_WithEveryWindowsSize()
        {
            using Stream stream = AssetLoader.Open(new Uri("avares://ByteExplorer/Assets/Icon/ByteExplorer.ico"));
            using var reader = new BinaryReader(stream);
            Assert.Equal(0, reader.ReadUInt16()); // reserved
            Assert.Equal(1, reader.ReadUInt16()); // type: icon
            int count = reader.ReadUInt16();
            var sizes = new List<int>();
            for (int i = 0; i < count; i++)
            {
                int width = reader.ReadByte();
                reader.ReadBytes(15);
                sizes.Add(width == 0 ? 256 : width);
            }

            Assert.Equal(new[] { 16, 20, 24, 32, 40, 48, 64, 256 }, sizes);
        }

        [AvaloniaFact]
        public void Window_UsesTheIcon()
        {
            var window = new ModernMainWindow();
            Assert.NotNull(window.Icon);
        }
    }
}
