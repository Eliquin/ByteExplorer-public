// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ByteExplorer.Controls;
using ByteExplorer.Core.Services.FileOperations;
using ByteExplorer.Core.Tags;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using ByteExplorer.ViewModels.Sidebar;
using ByteExplorer.ViewModels.Tags;
using ByteExplorer.Views.Menus;
using ByteExplorer.Views.Sidebar;
using ByteExplorer.Views.Tags;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>Tags in the window: the Details column and tool tips, the details pane, the sidebar, tag searches, the context menu and the editor.</summary>
    public sealed class TagsUiTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-tagsui-" + Guid.NewGuid().ToString("N"));

        public TagsUiTests()
        {
            Directory.CreateDirectory(Path.Combine(_root, "Projects"));
            foreach (string name in new[] { "invoice-2024.pdf", "budget.xlsx", "notes.txt", "photo.png" })
            {
                File.WriteAllText(Path.Combine(_root, name), new string('x', 1500));
            }
        }

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

        private string P(string name) => Path.Combine(_root, name);

        [AvaloniaFact]
        public async Task Tags_ShowEverywhere_AndAreEditedFromTheMenuAndTheEditor()
        {
            var window = new ModernMainWindow { Width = 1400, Height = 860 };
            window.Show();
            MainViewModel main = window.ViewModel;
            TagStore store = main.Tags;
            var created = new List<string>();
            bool detailsWereVisible = false;
            try
            {
                await Pump(30);
                Assert.Same(store, TagStore.Default);
                foreach (string leftover in new[] { "UiTest", "UiTestUrgent", "Work Later", "Archive" })
                {
                    if (store.FindByFullName(leftover) is TagDefinition old)
                    {
                        store.DeleteTag(old.Id); // from an interrupted run (the store is the test folder's tags.json)
                    }
                }

                detailsWereVisible = main.IsDetailsPaneVisible;
                main.IsDetailsPaneVisible = true;
                FileListViewModel pane = main.FileListViewModel;
                pane.SelectedPreset = FileListViewPreset.Details;
                await pane.LoadFolderAsync(_root);
                await Pump(10);

                TagDefinition alpha = store.CreateTag("UiTest/Client Alpha", "Blue", description: "The Alpha account");
                TagDefinition urgent = store.CreateTag("UiTestUrgent", "Red");
                TagDefinition review = store.CreateTag("UiTest/Review", "Green");
                created.Add(store.FindByFullName("UiTest").Id);
                created.Add(urgent.Id);
                store.Assign(new[] { P("invoice-2024.pdf"), P("budget.xlsx"), P("Projects") }, alpha.Id);
                store.Assign(new[] { P("invoice-2024.pdf") }, urgent.Id);
                store.Assign(new[] { P("notes.txt") }, review.Id);
                store.SetComment(new[] { P("invoice-2024.pdf") }, "Send to the accountant");
                await Pump(10);

                // Details column and tool tip.
                FileListItemViewModel invoice = pane.Items.Cast<FileListItemViewModel>().Single(i => i.Name == "invoice-2024.pdf");
                string tip = LazyTip.TagTipText(invoice.FullPath);
                Assert.Contains("UiTest › Client Alpha (Blue) — The Alpha account", tip);
                Assert.Contains("UiTestUrgent (Red)", tip);
                Assert.Contains("Comment: Send to the accountant", tip);
                var detailsList = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "DetailsList" && l.IsEffectivelyVisible);
                FileItemView row = detailsList.GetVisualDescendants().OfType<FileItemView>().Single(v => ReferenceEquals(v.DataContext, invoice));
                Assert.Contains("Tags:", ToolTip.GetTip(row)?.ToString());
                Assert.Equal(DetailsColumnLayout.Default.Count - 1, DetailsColumnLayout.Default.IndexOf(DetailsColumnKind.Tags));

                // Details pane rows.
                pane.SelectedItem = invoice;
                await Pump(5);
                Assert.Contains(pane.Preview.Details, r => r.Label == "Tags" && r.Value.Contains("UiTest › Client Alpha"));
                Assert.Contains(pane.Preview.Details, r => r.Label == "Comment" && r.Value == "Send to the accountant");

                // Sidebar: filled automatically, counts include sub-tags.
                TagsSidebarViewModel sidebar = main.TagSidebar;
                await Pump(5);
                TagRowViewModel parentRow = sidebar.Rows.Single(r => r.FullName == "UiTest");
                Assert.Equal(4, parentRow.Count);
                Assert.True(parentRow.HasChildren);
                Assert.Contains(sidebar.Rows, r => r.FullName == "UiTest/Client Alpha" && r.Depth == 1);
                Save(window, "tags-details.png");

                // Opening a tag shows its items in the results view (a tag: search).
                sidebar.Open(sidebar.Rows.Single(r => r.FullName == "UiTest/Client Alpha"));
                Assert.Equal("tag:\"UiTest/Client Alpha\"", main.Search.Query);
                await WaitFor(() => main.Search.Results?.Count == 3);
                Assert.True(main.IsSearchActive);
                Assert.True(sidebar.Rows.Single(r => r.FullName == "UiTest/Client Alpha").IsActive);
                Save(window, "tags-search.png");
                main.ClearSearch();

                // Context menu: the Tags submenu.
                ContextMenu menu = FileContextMenu.ForItems(window, pane, new[] { P("notes.txt") }, true);
                MenuItem tags = menu.ItemsSource.OfType<MenuItem>().Single(m => (m.Header as string) == "Tags");
                List<MenuItem> tagItems = ((IEnumerable<Control>)tags.ItemsSource).OfType<MenuItem>().ToList();
                Assert.Contains(tagItems, m => (m.Header as string) == "New tag…");
                Assert.Contains(tagItems, m => (m.Header as string)?.StartsWith("Edit tags and comment", StringComparison.Ordinal) == true);
                MenuItem reviewItem = tagItems.Single(m => (m.Header as string)?.Trim() == "Review");
                Assert.True(reviewItem.IsChecked);
                MenuItem urgentItem = tagItems.Single(m => (m.Header as string)?.Trim() == "UiTestUrgent");
                urgentItem.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
                Assert.Contains(store.GetTags(P("notes.txt")), t => t.Id == urgent.Id);

                // The editor: a mixed tag (some of the items) stays mixed unless clicked; Save applies, Cancel does not.
                TagEditorWindow editor = TagEditorWindow.ShowFor(window, store, new[] { P("invoice-2024.pdf"), P("photo.png") });
                await Pump(10);
                TagEditorViewModel vm = editor.ViewModel;
                Assert.Equal("Tags for 2 items", vm.Title);
                TagEditorRow alphaRow = vm.Rows.Single(r => r.Id == alpha.Id);
                Assert.Null(alphaRow.State);
                vm.Filter = "Work Later/Archive";
                Assert.Equal("Create \"Work Later/Archive\"", vm.CreateText);
                vm.CreateCommand.Execute(null);
                TagDefinition archive = store.FindByFullName("Work Later/Archive");
                Assert.True(archive != null, $"error: {vm.Error}; filter '{vm.Filter}'; tags {string.Join(",", store.Tags.Select(store.FullName))}");
                created.Add(store.FindByFullName("Work Later").Id);
                Assert.True(vm.Rows.Single(r => r.Id == archive.Id).State);
                vm.Comment = "Checked";
                vm.SelectById(archive.Id);
                await Pump(5);
                Assert.Equal(store.FindByFullName("Work Later").Id, vm.SelectedParent.Id); // the Parent box shows it, and does not move it
                var parentBox = editor.FindControl<ComboBox>("ParentBox");
                Assert.True(ReferenceEquals(vm.SelectedParent, parentBox.SelectedItem),
                    $"box: {parentBox.SelectedItem} (index {parentBox.SelectedIndex}); same list {ReferenceEquals(parentBox.ItemsSource, vm.ParentChoices)}; vm {vm.SelectedParent}");
                Assert.Equal("Work Later/Archive", store.FullName(store.Find(archive.Id)));
                vm.SelectedParent = vm.ParentChoices.Single(c => c.Id == alpha.ParentId); // a person's pick moves it
                Assert.Equal("UiTest/Archive", store.FullName(store.Find(archive.Id)));
                vm.SelectedParent = vm.ParentChoices.Single(c => c.Name == "Work Later");
                Assert.Equal("Work Later/Archive", store.FullName(store.Find(archive.Id)));
                Assert.DoesNotContain(vm.ParentChoices, c => c.Id == archive.Id); // never under itself
                vm.SetColorCommand.Execute("Purple");
                Assert.Equal("Purple", store.Find(archive.Id).Color); // definitions change at once
                Save(editor, "tags-editor.png");
                Assert.DoesNotContain(store.GetTags(P("photo.png")), t => t.Id == archive.Id); // items not yet
                vm.Save();
                editor.Close();
                Assert.Contains(store.GetTags(P("photo.png")), t => t.Id == archive.Id);
                Assert.DoesNotContain(store.GetTags(P("photo.png")), t => t.Id == alpha.Id); // mixed: left as it was
                Assert.Contains(store.GetTags(P("invoice-2024.pdf")), t => t.Id == alpha.Id);
                Assert.Equal("Checked", store.GetComment(P("photo.png")));

                // A rename done in the app moves the tags; undoing it moves them back.
                string renamed = P("invoice-final.pdf");
                File.Move(P("invoice-2024.pdf"), renamed);
                var record = new FileOperationRecord("Rename", new UndoAction[] { new MovedAction(P("invoice-2024.pdf"), renamed) }, new[] { _root });
                main.Operations.Record(record);
                Assert.Contains(store.GetTags(renamed), t => t.Id == alpha.Id);
                Assert.Empty(store.GetTags(P("invoice-2024.pdf")));
                await main.Operations.UndoLastAsync();
                await Pump(10);
                Assert.Contains(store.GetTags(P("invoice-2024.pdf")), t => t.Id == alpha.Id);
            }
            finally
            {
                foreach (string id in created)
                {
                    store.DeleteTag(id);
                }

                store.ClearTags(Directory.EnumerateFileSystemEntries(_root));
                store.SetComment(Directory.EnumerateFileSystemEntries(_root), string.Empty);
                await store.FlushAsync();
                main.IsDetailsPaneVisible = detailsWereVisible;
                window.Close();
            }
        }

        private static void Save(Window window, string name)
        {
            string directory = Environment.GetEnvironmentVariable("BYTEEXPLORER_SCREENSHOT_DIR");
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                window.CaptureRenderedFrame()?.Save(Path.Combine(directory, name));
            }
        }

        private static async Task WaitFor(Func<bool> condition, int rounds = 250)
        {
            for (int i = 0; i < rounds && !condition(); i++)
            {
                await Pump(1);
            }

            Assert.True(condition());
        }

        private static async Task Pump(int rounds)
        {
            for (int i = 0; i < rounds; i++)
            {
                await Task.Delay(20);
                Dispatcher.UIThread.RunJobs();
            }
        }
    }
}
