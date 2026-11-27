// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ByteExplorer.Core;
using ByteExplorer.Core.Services;
using ByteExplorer.ViewModels;
using ByteExplorer.ViewModels.FileOperations;
using ByteExplorer.Views.Dialogs;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// Bulk rename: the rules (each one, combined, their errors), the preview's problems (invalid names, duplicates, names
    /// taken in the folder), the renames on disk (chains, cycles, case-only, partial failures), one undo for the whole
    /// batch, and the Rename items dialog opened from the window with a selection.
    /// </summary>
    [Xunit.Collection(GlobalStateCollection.Name)]
    public sealed class BulkRenameTests : IDisposable
    {
        private static readonly DateTime May = new DateTime(2024, 5, 17, 9, 30, 0);
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-bulkrename-" + Guid.NewGuid().ToString("N"));

        public BulkRenameTests()
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(Path.GetTempPath(), "be-bulkrename-trash"));
            Directory.CreateDirectory(_root);
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

        // ------------------------------------------------------------------ rules

        [Fact]
        public void Split_KeepsDotFilesAndFoldersWhole()
        {
            Assert.Equal(("photo", ".jpg"), BulkRename.Split("photo.jpg", false));
            Assert.Equal(("archive.tar", ".gz"), BulkRename.Split("archive.tar.gz", false));
            Assert.Equal((".bashrc", ""), BulkRename.Split(".bashrc", false));
            Assert.Equal(("notes", ""), BulkRename.Split("notes", false));
            Assert.Equal(("v1.2", ""), BulkRename.Split("v1.2", true));
        }

        [Fact]
        public void FindAndReplace_PlainText_AllOrFirst_CaseAndExtension()
        {
            var options = new BulkRenameOptions { FindEnabled = true, Find = "a", Replace = "o" };
            Assert.Equal(new[] { "bonono.txt" }, Names(options, "banana.txt"));
            Assert.Equal(new[] { "Bonono.txt" }, Names(options, "BAnana.txt"));

            options.FirstOnly = true;
            Assert.Equal(new[] { "bonana.txt" }, Names(options, "banana.txt"));

            options.FirstOnly = false;
            options.MatchCase = true;
            Assert.Equal(new[] { "AAo.txt" }, Names(options, "AAa.txt"));

            // The extension is left alone unless asked for.
            options = new BulkRenameOptions { FindEnabled = true, Find = "txt", Replace = "md" };
            Assert.Equal(new[] { "md notes.txt" }, Names(options, "txt notes.txt"));
            options.IncludeExtension = true;
            Assert.Equal(new[] { "md notes.md" }, Names(options, "txt notes.txt"));
        }

        [Fact]
        public void FindAndReplace_RegularExpression_GroupsAndErrors()
        {
            var options = new BulkRenameOptions { FindEnabled = true, UseRegex = true, Find = @"IMG_(\d+)", Replace = "Photo $1" };
            Assert.Equal(new[] { "Photo 0042.jpg", "Photo 7.jpg" }, Names(options, "IMG_0042.jpg", "img_7.jpg"));

            options.MatchCase = true;
            Assert.Equal(new[] { "Photo 0042.jpg", "img_7.jpg" }, Names(options, "IMG_0042.jpg", "img_7.jpg"));

            options = new BulkRenameOptions { FindEnabled = true, UseRegex = true, Find = @"\d", Replace = "#", FirstOnly = true };
            Assert.Equal(new[] { "a#2.txt" }, Names(options, "a12.txt"));

            options.Find = "(unclosed";
            BulkRenamePlan plan = Plan(options, "a.txt", "b.txt");
            Assert.StartsWith("The regular expression isn't valid", plan.Error);
            Assert.False(plan.CanApply);
            Assert.All(plan.Rows, row => Assert.False(row.IsChanged));
        }

        [Fact]
        public void Numbering_ExplorerMode_PatternStartStepPaddingAndExtension()
        {
            // One name typed for several items, as in Explorer: "Name (1)", "Name (2)"…
            var options = new BulkRenameOptions { NumberingEnabled = true, Pattern = "Holiday" };
            Assert.Equal(new[] { "Holiday (1).jpg", "Holiday (2).png", "Holiday (3)" }, Names(options, "a.jpg", "b.png", "c"));

            options = new BulkRenameOptions { NumberingEnabled = true, Pattern = "Img_{n}", Start = 5, Step = 2, Digits = 3 };
            Assert.Equal(new[] { "Img_005.jpg", "Img_007.jpg", "Img_009.jpg" }, Names(options, "x.jpg", "y.jpg", "z.jpg"));

            options.Digits = 2;
            options.Start = 99;
            options.Step = 1;
            Assert.Equal(new[] { "Img_99.jpg", "Img_100.jpg" }, Names(options, "x.jpg", "y.jpg"));

            options = new BulkRenameOptions { NumberingEnabled = true, Pattern = "{name} - {n}", KeepExtension = false };
            Assert.Equal(new[] { "draft - 1", "final - 2" }, Names(options, "draft.txt", "final.txt"));
        }

        [Fact]
        public void AddText_RemoveCharacters_AndInsertDate_GoBeforeTheExtension()
        {
            var options = new BulkRenameOptions { AddTextEnabled = true, Prefix = "2024 ", Suffix = "_v2" };
            Assert.Equal(new[] { "2024 report_v2.pdf", "2024 Photos_v2" }, Names(options, Item("report.pdf"), Item("Photos", folder: true)));

            options = new BulkRenameOptions { RemoveEnabled = true, RemovePosition = 1, RemoveCount = 4 };
            Assert.Equal(new[] { "0042.jpg" }, Names(options, "IMG_0042.jpg"));
            options.RemovePosition = 3;
            options.RemoveCount = 2;
            Assert.Equal(new[] { "IM0042.jpg", "ab.txt" }, Names(options, "IMG_0042.jpg", "ab.txt")); // past the end: unchanged

            options = new BulkRenameOptions { DateEnabled = true };
            Assert.Equal(new[] { "2024-05-17 scan.pdf" }, Names(options, "scan.pdf"));
            options.DatePosition = BulkRenameDatePosition.AfterName;
            options.DateFormat = "yyyyMMdd";
            options.DateSeparator = "_";
            Assert.Equal(new[] { "scan_20240517.pdf" }, Names(options, "scan.pdf"));

            options.DateFormat = "%"; // not a date format
            Assert.StartsWith("The date format isn't valid", Plan(options, "scan.pdf").Error);
        }

        [Fact]
        public void ChangeCase_AndExtension()
        {
            var options = new BulkRenameOptions { CaseEnabled = true, Case = BulkRenameCase.Lower };
            Assert.Equal(new[] { "my holiday.JPG" }, Names(options, "My HOLIDAY.JPG"));
            options.Case = BulkRenameCase.Upper;
            Assert.Equal(new[] { "MY HOLIDAY.JPG" }, Names(options, "My holiday.JPG"));
            options.Case = BulkRenameCase.Title;
            Assert.Equal(new[] { "My Summer Holiday.jpg" }, Names(options, "my SUMMER holiday.jpg"));
            options.Case = BulkRenameCase.Sentence;
            Assert.Equal(new[] { "01 My summer holiday.jpg", "Report" }, Names(options, Item("01 MY Summer holiday.jpg"), Item("REPORT", folder: true)));

            options = new BulkRenameOptions { ExtensionEnabled = true, ExtensionMode = BulkRenameExtensionMode.ChangeTo, NewExtension = ".md" };
            Assert.Equal(new[] { "notes.md", "readme.md", "Docs.v2" }, Names(options, Item("notes.txt"), Item("readme"), Item("Docs.v2", folder: true)));
            options.NewExtension = "TXT";
            Assert.Equal(new[] { "notes.TXT" }, Names(options, "notes.txt"));
            options.ExtensionMode = BulkRenameExtensionMode.Lower;
            Assert.Equal(new[] { "Photo.jpeg" }, Names(options, "Photo.JPEG"));
            options.ExtensionMode = BulkRenameExtensionMode.Upper;
            Assert.Equal(new[] { "Photo.JPEG" }, Names(options, "Photo.jpeg"));
            options.ExtensionMode = BulkRenameExtensionMode.Remove;
            Assert.Equal(new[] { "archive.tar", ".bashrc" }, Names(options, "archive.tar.gz", ".bashrc"));
        }

        [Fact]
        public void Rules_CombineTopToBottom()
        {
            var options = new BulkRenameOptions
            {
                FindEnabled = true, Find = "_", Replace = " ",
                NumberingEnabled = true, Pattern = "{n} {name}", Digits = 2,
                AddTextEnabled = true, Suffix = " (copy)",
                CaseEnabled = true, Case = BulkRenameCase.Title,
                ExtensionEnabled = true, ExtensionMode = BulkRenameExtensionMode.Lower
            };
            Assert.Equal(new[] { "01 Beach Day (Copy).jpg", "02 Old Town (Copy).png" }, Names(options, "BEACH_DAY.JPG", "old_town.PNG"));
        }

        [Fact]
        public void Problems_InvalidNames_Duplicates_AndNamesTakenInTheFolder()
        {
            // An invalid name (a separator), an empty one, and a lost stem are refused with the reason.
            var options = new BulkRenameOptions { FindEnabled = true, Find = "x", Replace = "/" };
            BulkRenamePlan plan = Plan(options, "x1.txt", "keep.txt");
            Assert.NotNull(plan.Rows[0].Problem);
            Assert.Null(plan.Rows[1].Problem);
            Assert.False(plan.CanApply);

            options = new BulkRenameOptions { FindEnabled = true, Find = "photo", Replace = "" };
            plan = Plan(options, "photo.jpg", "photo");
            Assert.Equal("Nothing would be left before the extension.", plan.Rows[0].Problem);
            Assert.NotNull(plan.Rows[1].Problem); // empty
            Assert.Equal(2, plan.ProblemCount);

            // Two items getting the same name: both are marked.
            options = new BulkRenameOptions { FindEnabled = true, UseRegex = true, Find = @"\d", Replace = "" };
            plan = Plan(options, "a1.txt", "a2.txt", "b.txt");
            Assert.Equal("Another selected item would get the same name.", plan.Rows[0].Problem);
            Assert.Equal("Another selected item would get the same name.", plan.Rows[1].Problem);
            Assert.Null(plan.Rows[2].Problem);

            // An unchanged selected item keeps its name, so nothing else may take it.
            plan = Plan(options, "a1.txt", "a.txt");
            Assert.NotNull(plan.Rows[0].Problem);

            // A name taken by an item outside the selection.
            options = new BulkRenameOptions { AddTextEnabled = true, Prefix = "new-" };
            plan = BulkRename.Plan(new[] { Item("a.txt"), Item("b.txt") }, options, (folder, name) => name == "new-b.txt" || name == "a.txt" || name == "b.txt");
            Assert.Null(plan.Rows[0].Problem);
            Assert.Equal("An item with this name already exists in the folder.", plan.Rows[1].Problem);
            Assert.Equal(1, plan.ProblemCount);
            Assert.False(plan.CanApply);
        }

        [Fact]
        public void Problems_CyclesAndCaseOnlyRenames_AreFine()
        {
            // 2.txt -> 1.txt while 1.txt -> 2.txt (numbering in list order): the names are the batch's own, no clash.
            var options = new BulkRenameOptions { NumberingEnabled = true, Pattern = "{n}" };
            Func<string, string, bool> exists = (folder, name) => name == "1.txt" || name == "2.txt";
            BulkRenamePlan plan = BulkRename.Plan(new[] { Item("2.txt"), Item("1.txt") }, options, exists);
            Assert.Equal(new[] { "1.txt", "2.txt" }, plan.Rows.Select(r => r.NewName));
            Assert.True(plan.CanApply);
            Assert.Equal(2, plan.Moves.Count);

            // A change of case only on a case-insensitive file system: the item "exists" (it is itself), still fine.
            options = new BulkRenameOptions { CaseEnabled = true, Case = BulkRenameCase.Lower };
            plan = BulkRename.Plan(new[] { Item("REPORT.txt") }, options, (folder, name) => string.Equals(name, "report.txt", StringComparison.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
            Assert.True(plan.CanApply);

            // Nothing changes: nothing to apply.
            Assert.False(Plan(new BulkRenameOptions(), "a.txt").CanApply);
        }

        // ------------------------------------------------------------------ renames on disk

        [Fact]
        public void Runner_ChainsCyclesAndCaseOnly_ViaTemporaryNames()
        {
            string a = Write("a.txt", "A");
            string b = Write("b.txt", "B");
            string c = Write("c.txt", "C");
            string upper = Write("UPPER.txt", "U");

            // a <-> b (a cycle), c -> d, UPPER -> upper (case only; the temporary name is used when told case-insensitive).
            var moves = new[]
            {
                new BulkRenameMove(a, b, false),
                new BulkRenameMove(b, a, false),
                new BulkRenameMove(c, P("d.txt"), false),
                new BulkRenameMove(upper, P("upper.txt"), false)
            };
            BulkRenameResult result = BulkRenameRunner.Run(moves, caseInsensitive: true);

            Assert.Empty(result.Failures);
            Assert.Equal(4, result.Renamed.Count);
            Assert.Equal("B", File.ReadAllText(a));
            Assert.Equal("A", File.ReadAllText(b));
            Assert.Equal("C", File.ReadAllText(P("d.txt")));
            Assert.Contains("upper.txt", Directory.GetFiles(_root).Select(Path.GetFileName));
            Assert.DoesNotContain(Directory.GetFiles(_root), f => Path.GetFileName(f).StartsWith(".renaming", StringComparison.Ordinal));
            Assert.Equal(7, result.Steps.Count); // a, b and UPPER in two steps each, c in one
        }

        [Fact]
        public void Runner_PartialFailure_KeepsTheRest_AndPutsBackWhatCouldNotFinish()
        {
            string a = Write("a.txt", "A");
            string b = Write("b.txt", "B");
            Write("taken.txt", "T"); // created after the preview: never replaced
            string folder = Directory.CreateDirectory(P("folder")).FullName;

            // b -> taken.txt fails; a -> b then has nowhere to go and gets its old name back; folder -> renamed works.
            var moves = new[]
            {
                new BulkRenameMove(a, b, false),
                new BulkRenameMove(b, P("taken.txt"), false),
                new BulkRenameMove(folder, P("renamed"), true)
            };
            BulkRenameResult result = BulkRenameRunner.Run(moves, caseInsensitive: false);

            Assert.Equal(new[] { "b.txt", "a.txt" }, result.Failures.Select(f => f.Name));
            Assert.Single(result.Renamed);
            Assert.True(Directory.Exists(P("renamed")));
            Assert.Equal("A", File.ReadAllText(a));
            Assert.Equal("B", File.ReadAllText(b));
            Assert.Equal("T", File.ReadAllText(P("taken.txt")));
            Assert.Single(result.Steps); // a's trip out and back left nothing to undo
            Assert.Contains("couldn't be renamed", FileListViewModel.DescribeFailures(result));
        }

        [AvaloniaFact]
        public async Task Apply_IsOneOperation_UndoneWithOneUndo_AndSelectsTheRenamedItems()
        {
            string folder = Directory.CreateDirectory(P("work")).FullName;
            File.WriteAllText(Path.Combine(folder, "1.txt"), "one");
            File.WriteAllText(Path.Combine(folder, "2.txt"), "two");
            File.WriteAllText(Path.Combine(folder, "notes.md"), "notes");
            var window = new ModernMainWindow { Width = 1200, Height = 700 };
            window.Show();
            try
            {
                await Pump(30);
                FileListViewModel pane = window.ViewModel.FileListViewModel;
                FileOperationQueue operations = window.ViewModel.Operations;
                await pane.LoadFolderAsync(folder);
                await Pump(5);

                var moves = new[]
                {
                    new BulkRenameMove(Path.Combine(folder, "1.txt"), Path.Combine(folder, "2.txt"), false),
                    new BulkRenameMove(Path.Combine(folder, "2.txt"), Path.Combine(folder, "1.txt"), false),
                    new BulkRenameMove(Path.Combine(folder, "notes.md"), Path.Combine(folder, "Notes.MD"), false)
                };
                await pane.ApplyBulkRenameAsync(moves);
                await Pump(5);

                Assert.Equal("two", File.ReadAllText(Path.Combine(folder, "1.txt")));
                Assert.Equal("one", File.ReadAllText(Path.Combine(folder, "2.txt")));
                Assert.Contains("Notes.MD", Directory.GetFiles(folder).Select(Path.GetFileName));
                Assert.Equal("Undo Rename 3 items", operations.UndoText);
                Assert.Equal(new[] { "1.txt", "2.txt", "Notes.MD" }, pane.SelectedItems.Select(i => i.Name).OrderBy(n => n, StringComparer.Ordinal));

                await operations.UndoLastAsync();
                Assert.Equal("one", File.ReadAllText(Path.Combine(folder, "1.txt")));
                Assert.Equal("two", File.ReadAllText(Path.Combine(folder, "2.txt")));
                Assert.Contains("notes.md", Directory.GetFiles(folder).Select(Path.GetFileName));
                Assert.Equal(3, Directory.GetFileSystemEntries(folder).Length);
                Assert.False(operations.CanUndo && operations.UndoText.Contains("Rename", StringComparison.Ordinal));
            }
            finally
            {
                window.Close();
            }
        }

        [AvaloniaFact]
        public async Task Dialog_OpensForASelection_PreviewFollowsTheRules_AndOkRenames()
        {
            string folder = Directory.CreateDirectory(P("trip")).FullName;
            foreach (string name in new[] { "c.jpg", "a.jpg", "b.jpg" })
            {
                File.WriteAllText(Path.Combine(folder, name), name);
            }

            File.WriteAllText(Path.Combine(folder, "Holiday 2.jpg"), "outside the selection");
            var window = new ModernMainWindow { Width = 1200, Height = 700 };
            window.Show();
            try
            {
                await Pump(30);
                FileListViewModel pane = window.ViewModel.FileListViewModel;
                await pane.LoadFolderAsync(folder);
                await Pump(5);

                // Selected in another order than the list's (sorted by name): numbering follows the list.
                var selection = pane.Items.OfType<FileListItemViewModel>().Where(i => i.Name.Length == 5).OrderByDescending(i => i.Name).ToList();
                pane.UpdateSelection(selection);
                Assert.True(pane.RenameCommand.CanExecute(null));
                pane.RenameCommand.Execute(null);
                await WaitUntil(() => window.OwnedWindows.OfType<BulkRenameDialog>().Any());
                BulkRenameDialog dialog = window.OwnedWindows.OfType<BulkRenameDialog>().First();

                // Explorer's quick mode: the first item's name, numbered.
                Assert.Equal("a", dialog.PatternBox.Text);
                Assert.Equal(new[] { "a (1).jpg", "a (2).jpg", "a (3).jpg" }, dialog.CurrentPlan.Rows.Select(r => r.NewName));
                Assert.Equal(new[] { "a.jpg", "b.jpg", "c.jpg" }, dialog.CurrentPlan.Rows.Select(r => r.Item.Name));
                Assert.True(dialog.CanAccept);

                // "Holiday {n}" clashes with "Holiday 2.jpg", which isn't selected: shown, and Rename is disabled.
                dialog.PatternBox.Text = "Holiday {n}";
                Assert.Equal("Holiday 2.jpg", dialog.CurrentPlan.Rows[1].NewName);
                Assert.Equal("An item with this name already exists in the folder.", dialog.CurrentPlan.Rows[1].Problem);
                Assert.False(dialog.CanAccept);
                Assert.Equal(3, dialog.PreviewList.ItemCount);

                dialog.DigitsBox.Value = 2;
                Assert.Equal(new[] { "Holiday 01.jpg", "Holiday 02.jpg", "Holiday 03.jpg" }, dialog.CurrentPlan.Rows.Select(r => r.NewName));
                Assert.True(dialog.CanAccept);

                // A broken regular expression stops everything until it is fixed.
                dialog.FindRule.IsChecked = true;
                dialog.RegexBox.IsChecked = true;
                dialog.FindBox.Text = "(";
                Assert.NotNull(dialog.CurrentPlan.Error);
                Assert.False(dialog.CanAccept);
                dialog.FindBox.Text = "^(.)$";
                Assert.Null(dialog.CurrentPlan.Error);
                dialog.FindRule.IsChecked = false;
                dialog.PatternBox.Text = "Trip {n}";
                dialog.CaseRule.IsChecked = true;
                dialog.CaseChoice.SelectedIndex = (int)BulkRenameCase.Upper;
                Assert.Equal(new[] { "TRIP 01.jpg", "TRIP 02.jpg", "TRIP 03.jpg" }, dialog.CurrentPlan.Rows.Select(r => r.NewName));
                Assert.True(dialog.CanAccept);

                Save(dialog, "bulk-rename.png");
                dialog.AcceptForTest();
                await WaitUntil(() => File.Exists(Path.Combine(folder, "TRIP 03.jpg")));
                Assert.Equal("a.jpg", File.ReadAllText(Path.Combine(folder, "TRIP 01.jpg")));
                Assert.Equal("c.jpg", File.ReadAllText(Path.Combine(folder, "TRIP 03.jpg")));
                await WaitUntil(() => pane.SelectedItems.Count == 3 && pane.SelectedItems.All(i => i.Name.StartsWith("TRIP", StringComparison.Ordinal)));

                await window.ViewModel.Operations.UndoLastAsync();
                Assert.True(File.Exists(Path.Combine(folder, "a.jpg")));
                Assert.False(File.Exists(Path.Combine(folder, "TRIP 01.jpg")));
            }
            finally
            {
                window.Close();
            }
        }

        // ------------------------------------------------------------------ helpers

        private BulkRenameItem Item(string name, bool folder = false) => new BulkRenameItem(Path.Combine(_root, name), folder, May);

        private BulkRenamePlan Plan(BulkRenameOptions options, params string[] names) =>
            BulkRename.Plan(names.Select(n => Item(n)).ToList(), options, (folder, name) => false, StringComparer.Ordinal);

        private string[] Names(BulkRenameOptions options, params string[] names) => Plan(options, names).Rows.Select(r => r.NewName).ToArray();

        private static string[] Names(BulkRenameOptions options, params BulkRenameItem[] items) =>
            BulkRename.Plan(items, options, (folder, name) => false, StringComparer.Ordinal).Rows.Select(r => r.NewName).ToArray();

        private string P(string name) => Path.Combine(_root, name);

        private string Write(string name, string text)
        {
            string path = P(name);
            File.WriteAllText(path, text);
            return path;
        }

        /// <summary>A screenshot of the dialog when BYTEEXPLORER_SCREENSHOT_DIR is set (as the other dialog tests do).</summary>
        private static void Save(Avalonia.Controls.Window window, string name)
        {
            string directory = Environment.GetEnvironmentVariable("BYTEEXPLORER_SCREENSHOT_DIR");
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
                Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)?.Save(Path.Combine(directory, name));
            }
        }

        private static async Task WaitUntil(Func<bool> condition)
        {
            for (int i = 0; i < 300 && !condition(); i++)
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
