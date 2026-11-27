// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using ByteExplorer.Core;
using Xunit;

namespace ByteExplorer.Tests
{
    public sealed class CorePathTests
    {
        [Fact]
        public void TrimTrailingSeparator_KeepsRoots()
        {
            if (OperatingSystem.IsWindows())
            {
                Assert.Equal(@"C:\", PathUtil.TrimTrailingSeparator(@"C:\"));
                Assert.Equal(@"C:\", PathUtil.TrimTrailingSeparator("C:"));
                Assert.Equal(@"C:\Temp", PathUtil.TrimTrailingSeparator(@"C:\Temp\\"));
            }
            else
            {
                Assert.Equal("/", PathUtil.TrimTrailingSeparator("/"));
                Assert.Equal("/", PathUtil.TrimTrailingSeparator("//"));
                Assert.Equal("/home/u", PathUtil.TrimTrailingSeparator("/home/u/"));
                // '\' is an ordinary file-name character on Unix, never a separator.
                Assert.Equal(@"/tmp/a\", PathUtil.TrimTrailingSeparator(@"/tmp/a\"));
            }
        }

        [Fact]
        public void IsInside_RespectsSegmentBoundaries()
        {
            string root = Path.Combine(Path.GetTempPath(), "be-root");
            Assert.True(PathUtil.IsInside(Path.Combine(root, "child"), root));
            Assert.False(PathUtil.IsInside(root + "sibling", root));
            Assert.False(PathUtil.IsInside(root, root));
            Assert.True(PathUtil.IsSameOrInside(root, root + Path.DirectorySeparatorChar));
        }

        [Fact]
        public void Comparison_IsCaseSensitiveOnLinuxOnly()
        {
            bool same = PathUtil.AreSame("/tmp/Folder", "/tmp/folder");
            Assert.Equal(!OperatingSystem.IsLinux(), same);
        }

        [Fact]
        public void GetName_HandlesTrailingSeparators()
        {
            string folder = Path.Combine(Path.GetTempPath(), "be-name");
            Assert.Equal("be-name", PathUtil.GetName(folder + Path.DirectorySeparatorChar));
        }

        [Theory]
        [InlineData("", false)]
        [InlineData("   ", false)]
        [InlineData(".", false)]
        [InlineData("..", false)]
        [InlineData("a/b", false)]
        [InlineData("report.txt", true)]
        [InlineData("résumé (final).pdf", true)]
        public void FileNameRules_CommonRules(string name, bool valid)
        {
            Assert.Equal(valid, FileNameRules.Validate(name) == null);
        }

        [Fact]
        public void FileNameRules_LengthLimit()
        {
            Assert.Null(FileNameRules.Validate(new string('a', 255)));
            Assert.NotNull(FileNameRules.Validate(new string('a', 256)));
        }

        [Fact]
        public void FileNameRules_PlatformSpecific()
        {
            if (OperatingSystem.IsWindows())
            {
                Assert.NotNull(FileNameRules.Validate("CON"));
                Assert.NotNull(FileNameRules.Validate("nul.txt"));
                Assert.NotNull(FileNameRules.Validate("a:b"));
                Assert.NotNull(FileNameRules.Validate("trailing."));
            }
            else if (OperatingSystem.IsMacOS())
            {
                Assert.NotNull(FileNameRules.Validate("a:b"));
                Assert.Null(FileNameRules.Validate("CON"));
            }
            else
            {
                Assert.Null(FileNameRules.Validate("CON"));
                Assert.Null(FileNameRules.Validate("a:b*?"));
            }
        }
    }
}
