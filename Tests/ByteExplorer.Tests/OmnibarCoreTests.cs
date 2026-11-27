// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using System.Linq;
using ByteExplorer.Core.Omnibar;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>The omnibar's pure pieces: fuzzy matching, reading what was typed, and breadcrumbs.</summary>
    public sealed class OmnibarCoreTests
    {
        // ------------------------------------------------------------------ fuzzy

        [Fact]
        public void Fuzzy_MatchesInOrder_CaseInsensitive()
        {
            Assert.True(FuzzyMatcher.Score("dwn", "Downloads", out int[] positions) > 0);
            Assert.Equal(new[] { 0, 2, 3 }, positions);
            Assert.Equal(-1, FuzzyMatcher.Score("xyz", "Downloads", out _));
            Assert.Equal(-1, FuzzyMatcher.Score("sd", "ds", out _)); // order matters
        }

        [Fact]
        public void Fuzzy_PrefersPrefixesWordStartsAndRuns()
        {
            int prefix = FuzzyMatcher.Score("doc", "Documents", out _);
            int inner = FuzzyMatcher.Score("doc", "MyDocuments", out _);
            int scattered = FuzzyMatcher.Score("doc", "dead-owl-cat", out _);
            Assert.True(prefix > inner, $"{prefix} > {inner}");
            Assert.True(inner > scattered, $"{inner} > {scattered}");

            int exact = FuzzyMatcher.Score("music", "Music", out _);
            Assert.True(exact > FuzzyMatcher.Score("music", "Music Library", out _));
        }

        [Fact]
        public void Fuzzy_HighlightsTheTightestWindow()
        {
            // Scattered letters in the middle of words lose to the same letters as a word.
            FuzzyMatcher.Score("doc", "xdxoxc docs", out int[] positions);
            Assert.Equal(new[] { 7, 8, 9 }, positions);
        }

        [Fact]
        public void Fuzzy_EmptyPatternMatchesEverything()
        {
            Assert.Equal(1, FuzzyMatcher.Score("", "anything", out int[] positions));
            Assert.Empty(positions);
        }

        // ------------------------------------------------------------------ input

        [Theory]
        [InlineData("", OmnibarInputKind.Empty)]
        [InlineData("   ", OmnibarInputKind.Empty)]
        [InlineData("> new folder", OmnibarInputKind.Command)]
        [InlineData("?invoice", OmnibarInputKind.Search)]
        [InlineData("https://example.com", OmnibarInputKind.WebAddress)]
        [InlineData("smb://nas/photos", OmnibarInputKind.NetworkAddress)]
        [InlineData("~", OmnibarInputKind.Path)]
        [InlineData("..", OmnibarInputKind.Path)]
        [InlineData("docs/2024", OmnibarInputKind.Path)]
        [InlineData("holiday photos", OmnibarInputKind.Text)]
        public void Input_IsClassified(string text, OmnibarInputKind kind)
        {
            Assert.Equal(kind, OmnibarInput.Parse(text, Path.GetTempPath(), Home).Kind);
        }

        [Fact]
        public void Input_CommandAndSearchDropTheirPrefix()
        {
            Assert.Equal("new folder", OmnibarInput.Parse(">  new folder ", null, Home).Query);
            Assert.Equal("tax 2024", OmnibarInput.Parse("? tax 2024", null, Home).Query);
        }

        [Fact]
        public void Input_ExpandsHome_AndSplitsForCompletion()
        {
            OmnibarInput input = OmnibarInput.Parse("~/Doc", "/", Home);
            Assert.Equal(Path.Combine(Home, "Doc"), input.FullPath);
            Assert.Equal(Home, input.Folder);
            Assert.Equal("Doc", input.Partial);

            OmnibarInput folder = OmnibarInput.Parse("~" + Path.DirectorySeparatorChar, "/", Home);
            Assert.Equal(Home, folder.Folder);
            Assert.Equal(string.Empty, folder.Partial);
            Assert.True(folder.EndsWithSeparator);
        }

        [Fact]
        public void Input_RelativePathsStartAtTheCurrentFolder()
        {
            string current = Path.Combine(Path.GetTempPath(), "a", "b");
            OmnibarInput up = OmnibarInput.Parse("..", current, Home);
            Assert.Equal(Path.Combine(Path.GetTempPath(), "a"), up.FullPath);
            Assert.Equal(string.Empty, up.Partial);

            OmnibarInput child = OmnibarInput.Parse("./c" + Path.DirectorySeparatorChar + "d", current, Home);
            Assert.Equal(Path.Combine(current, "c", "d"), child.FullPath);
            Assert.Equal("d", child.Partial);
        }

        [Fact]
        public void Input_UnquotesAndReadsFileUris()
        {
            string path = Path.Combine(Path.GetTempPath(), "My Files");
            Assert.Equal(path, OmnibarInput.Parse("\"" + path + "\"", null, Home).FullPath);
            Assert.Equal(path, OmnibarInput.Parse(new Uri(path).AbsoluteUri, null, Home).FullPath);
        }

        [Fact]
        public void Input_ExpandsEnvironmentVariables()
        {
            Environment.SetEnvironmentVariable("BE_OMNI_TEST", Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
            string typed = OperatingSystem.IsWindows() ? @"%BE_OMNI_TEST%\x" : "$BE_OMNI_TEST/x";
            Assert.Equal(Path.Combine(Path.GetTempPath(), "x"), OmnibarInput.Parse(typed, null, Home).FullPath);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(Path.Combine(Path.GetTempPath(), "y"), OmnibarInput.Parse("${BE_OMNI_TEST}/y", null, Home).FullPath);
            }
        }

        // ------------------------------------------------------------------ breadcrumbs

        [Fact]
        public void Crumbs_HomeIsOneSegment()
        {
            string path = Path.Combine(Home, "Documents", "Taxes");
            var crumbs = BreadcrumbBuilder.Build(path, Home, Array.Empty<CrumbVolume>());
            Assert.Equal(new[] { "Home", "Documents", "Taxes" }, crumbs.Select(c => c.Name));
            Assert.Equal(CrumbKind.Home, crumbs[0].Kind);
            Assert.Equal(path, crumbs[2].Path);
        }

        [Fact]
        public void Crumbs_VolumesAreNamedByLabel_AndArchivesMarked()
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            var volumes = new[] { new CrumbVolume("/media/me/USB", "My Stick", false) };
            var crumbs = BreadcrumbBuilder.Build("/media/me/USB/backup/photos.zip/2024", "/home/me", volumes);
            Assert.Equal(new[] { "My Stick", "backup", "photos.zip", "2024" }, crumbs.Select(c => c.Name));
            Assert.Equal(CrumbKind.Volume, crumbs[0].Kind);
            Assert.Equal(CrumbKind.Archive, crumbs[2].Kind);

            var root = BreadcrumbBuilder.Build("/etc/ssh", "/home/me", volumes);
            Assert.Equal(CrumbKind.Root, root[0].Kind);
            Assert.Equal(new[] { "etc", "ssh" }, root.Skip(1).Select(c => c.Name));
        }

        [Fact]
        public void Crumbs_EmptyForNoPath()
        {
            Assert.Empty(BreadcrumbBuilder.Build(null, Home, null));
        }

        private static string Home => Path.Combine(Path.GetTempPath(), "be-home");
    }
}
