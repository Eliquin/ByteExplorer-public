// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Core.Tags;
using ByteExplorer.Infrastructure;
using ByteSearcher;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>The tag store: tags and sub-tags, assignments and comments, following moves, persistence, tag searches.</summary>
    public sealed class TagStoreTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-tags-" + Guid.NewGuid().ToString("N"));

        public TagStoreTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException)
            {
            }
        }

        private string File(string name, string content = "x")
        {
            string path = Path.Combine(_root, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            System.IO.File.WriteAllText(path, content);
            return path;
        }

        [Fact]
        public void Tags_SubTagsFromSlashes_UniqueSiblings_RenameMoveDelete()
        {
            var store = new TagStore(null);
            var events = new List<TagsChangedEventArgs>();
            store.Changed += (_, e) => events.Add(e);

            TagDefinition project = store.CreateTag("Work/Project A", "Blue");
            TagDefinition work = store.FindByFullName("work"); // created on the way, case-insensitive lookup
            Assert.NotNull(work);
            Assert.Equal(work.Id, project.ParentId);
            Assert.Equal("Blue", project.Color);
            Assert.Equal("Work/Project A", store.FullName(project));
            Assert.Equal(1, store.Depth(project));
            Assert.Same(project, store.CreateTag("work/project a")); // the existing tag, not a twin
            Assert.Equal(new[] { "Work", "Project A" }, store.Tags.Select(t => t.Name));
            Assert.True(events.All(e => e.DefinitionsChanged));

            TagDefinition urgent = store.CreateTag("Urgent", "Red");
            Assert.Equal(new[] { "Urgent", "Work", "Project A" }, store.Tags.Select(t => t.Name)); // siblings by name, children follow

            store.RenameTag(urgent.Id, "Today");
            Assert.Equal("Today", store.Find(urgent.Id).Name);
            Assert.Throws<InvalidOperationException>(() => store.RenameTag(urgent.Id, "work")); // a sibling has that name
            Assert.Throws<ArgumentException>(() => store.RenameTag(urgent.Id, "a/b"));
            Assert.Throws<InvalidOperationException>(() => store.SetParent(work.Id, project.Id)); // under its own sub-tag
            store.SetParent(urgent.Id, work.Id);
            Assert.Equal("Work/Today", store.FullName(store.Find(urgent.Id)));
            store.SetColor(urgent.Id, "green");
            Assert.Equal("Green", store.Find(urgent.Id).Color);
            Assert.Throws<ArgumentException>(() => store.SetColor(urgent.Id, "Chartreuse"));

            string file = File("a.txt");
            store.Assign(new[] { file }, project.Id);
            store.Assign(new[] { file }, urgent.Id);
            store.DeleteTag(work.Id); // with its sub-tags, taken off the items
            Assert.Empty(store.Tags);
            Assert.Empty(store.GetTags(file));
            Assert.Equal(0, store.ItemCount);
        }

        [Fact]
        public void Items_AssignUnassignSetClear_Comments_Counts()
        {
            var store = new TagStore(null);
            TagDefinition work = store.CreateTag("Work");
            TagDefinition project = store.CreateTag("Project", parentId: work.Id);
            TagDefinition urgent = store.CreateTag("Urgent");
            string a = File("a.txt");
            string b = File("b.txt");

            store.Assign(new[] { a, b }, project.Id);
            store.Assign(new[] { a }, urgent.Id);
            store.Assign(new[] { a }, urgent.Id); // once
            Assert.Equal(new[] { "Urgent", "Project" }, store.GetTags(a).Select(t => t.Name).OrderBy(n => n == "Project").ToArray());
            Assert.Equal(2, store.GetTags(a).Count);
            Assert.Equal(2, store.CountItems(work.Id)); // through its sub-tag
            Assert.Equal(0, store.CountItems(work.Id, includeSubTags: false));
            Assert.Equal(new[] { a }, store.ItemsWithTag(urgent.Id));

            store.SetComment(new[] { a }, "  Sent to the client  ");
            Assert.Equal("Sent to the client", store.GetComment(a));
            store.Unassign(new[] { a }, urgent.Id);
            store.ClearTags(new[] { a });
            Assert.Empty(store.GetTags(a));
            Assert.Equal("Sent to the client", store.GetComment(a)); // a comment alone keeps the item
            store.SetComment(new[] { a }, "");
            Assert.Null(store.GetItem(a));

            store.SetTags(new[] { b }, new[] { urgent.Id, work.Id });
            Assert.Equal(new[] { urgent.Id, work.Id }, store.GetItem(b).TagIds);
            Assert.Throws<ArgumentException>(() => store.Assign(new[] { b }, "no-such-tag"));
        }

        [Fact]
        public async Task Moves_FollowItems_DeletedItemsComeBack_PruneKeepsUnreachable()
        {
            var store = new TagStore(null);
            TagDefinition tag = store.CreateTag("Keep");
            string folder = Path.Combine(_root, "Folder");
            string inside = File("Folder/inner/deep.txt");
            store.Assign(new[] { folder, inside }, tag.Id);

            // A folder renamed in the app: it and everything tagged inside move.
            string renamed = Path.Combine(_root, "Renamed");
            Directory.Move(folder, renamed);
            store.MovePath(folder, renamed);
            string movedInside = Path.Combine(renamed, "inner", "deep.txt");
            Assert.Single(store.GetTags(renamed));
            Assert.Single(store.GetTags(movedInside));
            Assert.Empty(store.GetTags(inside));

            // Deleted: set aside when its folder is refreshed, back when it returns (an undone delete).
            string trashed = movedInside + ".bak";
            System.IO.File.Move(movedInside, trashed);
            store.ReconcileFolders(new[] { Path.GetDirectoryName(movedInside) });
            Assert.Empty(store.GetTags(movedInside));
            System.IO.File.Move(trashed, movedInside);
            store.ReconcileFolders(new[] { Path.GetDirectoryName(movedInside) });
            Assert.Single(store.GetTags(movedInside));

            // Prune: a missing item in a folder that exists goes; one whose folder is gone (a drive not connected) stays.
            string gone = File("gone.txt");
            string offline = Path.Combine(_root, "not-connected-" + Guid.NewGuid().ToString("N"), "file.txt");
            store.Assign(new[] { gone, offline }, tag.Id);
            System.IO.File.Delete(gone);
            int removed = await store.PruneMissingAsync();
            Assert.Equal(1, removed);
            Assert.Empty(store.GetTags(gone));
            Assert.Single(store.GetTags(offline));
        }

        [Fact]
        public async Task Persistence_RoundTrips_PortablePaths_AndSurvivesACorruptFile()
        {
            string file = Path.Combine(_root, "tags.json");
            var store = new TagStore(file);
            TagDefinition tag = store.CreateTag("Work/Invoices", "Orange", description: "Bills to pay");
            string inHome = Path.Combine(SpecialFolders.Home, "be-tag-test-" + Guid.NewGuid().ToString("N") + ".txt");
            string elsewhere = File("doc.txt");
            store.Assign(new[] { inHome, elsewhere }, tag.Id);
            store.SetComment(new[] { elsewhere }, "Due in May");
            await store.FlushAsync();

            string json = System.IO.File.ReadAllText(file);
            Assert.Contains("::home/", json); // follows the app to another computer
            Assert.Contains("Due in May", json);

            var reloaded = new TagStore(file);
            await reloaded.LoadAsync();
            Assert.Equal("Work/Invoices", reloaded.FullName(reloaded.Find(tag.Id)));
            Assert.Equal("Bills to pay", reloaded.Find(tag.Id).Description);
            Assert.Equal("Orange", reloaded.Find(tag.Id).Color);
            Assert.Single(reloaded.GetTags(inHome));
            Assert.Equal("Due in May", reloaded.GetComment(elsewhere));

            System.IO.File.WriteAllText(file, "{ this is not json");
            var broken = new TagStore(file);
            await broken.LoadAsync();
            Assert.Empty(broken.Tags);
            Assert.True(System.IO.File.Exists(file + ".corrupt"));
        }

        [Fact]
        public void TagSearch_ParsesQueries_AndFindsTaggedItems()
        {
            Assert.True(TagSearch.TryParse("tag:Work report", out IReadOnlyList<string> names, out string rest));
            Assert.Equal(new[] { "Work" }, names);
            Assert.Equal("report", rest);
            Assert.True(TagSearch.TryParse("tag:\"Work/Project A\" tag:Urgent", out names, out rest));
            Assert.Equal(new[] { "Work/Project A", "Urgent" }, names);
            Assert.Equal(string.Empty, rest);
            Assert.False(TagSearch.TryParse("report tagline", out _, out _));
            Assert.Equal("tag:\"Work/Project A\"", TagSearch.QueryFor("Work/Project A"));

            var store = new TagStore(null);
            TagDefinition project = store.CreateTag("Work/Project A");
            TagDefinition urgent = store.CreateTag("Urgent");
            string report = File("report-2024.pdf");
            string notes = File("notes.txt");
            string folder = Path.Combine(_root, "Shared");
            Directory.CreateDirectory(folder);
            store.Assign(new[] { report, notes, folder }, project.Id);
            store.Assign(new[] { report }, urgent.Id);

            SearchResults all = TagSearch.Run(store, "tag:Work", new SearchOptions(), CancellationToken.None, out string unknown);
            Assert.Null(unknown);
            Assert.Equal(3, all.Count); // sub-tags included
            Assert.True(all.GetHit(0).IsDirectory); // folders first

            SearchResults both = TagSearch.Run(store, "tag:\"Project A\" tag:urgent", new SearchOptions(), CancellationToken.None, out _);
            Assert.Equal(new[] { "report-2024.pdf" }, Enumerable.Range(0, both.Count).Select(i => both.GetHit(i).Name));

            SearchResults named = TagSearch.Run(store, "tag:Work notes", new SearchOptions(), CancellationToken.None, out _);
            Assert.Equal("notes.txt", named.GetHit(0).Name);
            Assert.Equal(new TextRange(0, 5), named.GetHit(0).NameHighlights[0]);

            SearchResults filesOnly = TagSearch.Run(store, "tag:Work", new SearchOptions { FoldersOnly = false }, CancellationToken.None, out _);
            Assert.Equal(2, filesOnly.Count);
            SearchResults bySize = all.Sorted(SearchSort.Size, descending: true);
            Assert.Equal(3, bySize.Count);

            TagSearch.Run(store, "tag:Nothing", new SearchOptions(), CancellationToken.None, out unknown);
            Assert.Equal("Nothing", unknown);
        }
    }
}
