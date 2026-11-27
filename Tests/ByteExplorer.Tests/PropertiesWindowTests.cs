// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using ByteExplorer.ViewModels;
using ByteExplorer.Views.Dialogs;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>The Properties window: facts per tab, background counting and cancellation, Apply, hashes, keyboard.</summary>
    public sealed class PropertiesWindowTests
    {
        [AvaloniaFact]
        public async Task SingleFile_General_ShowsSizeDatesAndType()
        {
            string folder = NewFolder();
            string file = Path.Combine(folder, "notes.txt");
            File.WriteAllBytes(file, new byte[5000]);
            var window = new PropertiesWindow(new[] { file });
            window.Show();
            try
            {
                await window.SizeTask;
                await window.FactsTask;
                await Pump(3);

                Assert.Equal("notes.txt Properties", window.Title);
                Assert.Equal("notes.txt", Find<TextBox>(window, "NameBox").Text);
                Assert.Contains("TXT File", Text(window, "TypeValue"));
                Assert.Contains("(.txt)", Text(window, "TypeValue"));
                Assert.Equal(folder, Text(window, "LocationValue"));
                Assert.Equal($"{4.88.ToString("N2", CultureInfo.CurrentCulture)} KB ({5000.ToString("N0", CultureInfo.CurrentCulture)} bytes)", Text(window, "SizeValue"));
                if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
                {
                    SelectableTextBlock onDisk = Find<SelectableTextBlock>(window, "SizeOnDiskValue");
                    Assert.True(onDisk.IsVisible);
                    Assert.DoesNotContain("…", onDisk.Text);
                }

                Assert.Equal(File.GetLastWriteTime(file).ToString("F", CultureInfo.CurrentCulture), Text(window, "ModifiedValue"));
                Assert.Null(FindOrNull<SelectableTextBlock>(window, "ContainsValue"));
                Assert.Equal(new[] { "General", "Details", "Security", "Hashes" }, TabHeaders(window));
                Assert.False(Find<Button>(window, "ApplyButton").IsEnabled);

                await window.EnsureSecurityLoaded();
                await Pump(2);
                if (!OperatingSystem.IsWindows())
                {
                    Assert.Contains("(0", Text(window, "ModeValue"));
                }
            }
            finally
            {
                window.Close();
                Directory.Delete(folder, true);
            }
        }

        [AvaloniaFact]
        public async Task Folder_CountsContentsInTheBackground()
        {
            string folder = NewFolder();
            Directory.CreateDirectory(Path.Combine(folder, "a", "b"));
            File.WriteAllBytes(Path.Combine(folder, "one.bin"), new byte[100]);
            File.WriteAllBytes(Path.Combine(folder, "a", "two.bin"), new byte[200]);
            File.WriteAllBytes(Path.Combine(folder, "a", "b", "three.bin"), new byte[300]);
            var window = new PropertiesWindow(new[] { folder });
            window.Show();
            try
            {
                await window.SizeTask;
                await Pump(3);
                Assert.Equal("3 Files, 2 Folders", Text(window, "ContainsValue"));
                Assert.Equal("600 bytes", Text(window, "SizeValue"));
                Assert.Equal("File folder", Text(window, "TypeValue"));
                Assert.Equal(new[] { "General", "Details", "Security" }, TabHeaders(window));
            }
            finally
            {
                window.Close();
                Directory.Delete(folder, true);
            }
        }

        [AvaloniaFact]
        public async Task Folder_CountIsCancelledWhenTheWindowCloses()
        {
            string folder = NewFolder();
            for (int i = 0; i < 400; i++)
            {
                string sub = Path.Combine(folder, "d" + i);
                Directory.CreateDirectory(sub);
                File.WriteAllText(Path.Combine(sub, "f.txt"), "x");
            }

            var window = new PropertiesWindow(new[] { folder });
            window.Show();
            Task count = window.SizeTask;
            window.Close(); // no pumping in between: the count is still running or its updates still queued
            try
            {
                Task finished = await Task.WhenAny(count, Task.Delay(10000));
                Assert.Same(count, finished);
                Assert.True(count.IsCompletedSuccessfully);
                await Pump(3);

                // Nothing reaches a closed window's controls.
                Assert.Equal("Calculating…", Text(window, "ContainsValue"));
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [AvaloniaFact]
        public async Task MultipleItems_SummarizeCountsSizeTypeAndFolder()
        {
            string folder = NewFolder();
            File.WriteAllBytes(Path.Combine(folder, "a.txt"), new byte[10]);
            File.WriteAllBytes(Path.Combine(folder, "b.log"), new byte[20]);
            Directory.CreateDirectory(Path.Combine(folder, "sub"));
            File.WriteAllBytes(Path.Combine(folder, "sub", "c.txt"), new byte[30]);
            var paths = new[] { Path.Combine(folder, "a.txt"), Path.Combine(folder, "b.log"), Path.Combine(folder, "sub") };
            var window = new PropertiesWindow(paths);
            window.Show();
            var sameType = new PropertiesWindow(new[] { Path.Combine(folder, "a.txt"), Path.Combine(folder, "sub", "c.txt") });
            sameType.Show();
            try
            {
                await window.SizeTask;
                await sameType.SizeTask;
                await Pump(3);
                Assert.Equal("3 items Properties", window.Title);
                Assert.Equal("2 Files, 1 Folder", Find<TextBox>(window, "NameBox").Text);
                Assert.Equal("Multiple types", Text(window, "TypeValue"));
                Assert.Equal("All in " + folder, Text(window, "LocationValue"));
                Assert.Equal("60 bytes", Text(window, "SizeValue"));
                Assert.Equal("3 Files, 1 Folder", Text(window, "ContainsValue"));
                Assert.Equal(new[] { "General" }, TabHeaders(window));
                Assert.True(Find<CheckBox>(window, "ReadOnlyBox").IsEnabled);

                Assert.Equal("All of type TXT File", Text(sameType, "TypeValue"));
                Assert.Equal("Various folders", Text(sameType, "LocationValue"));
            }
            finally
            {
                window.Close();
                sameType.Close();
                Directory.Delete(folder, true);
            }
        }

        [AvaloniaFact]
        public async Task NameBox_RenamesOnApply_AndRejectsBadNames()
        {
            string folder = NewFolder();
            string file = Path.Combine(folder, "before.txt");
            File.WriteAllText(file, "hello");
            var window = new PropertiesWindow(new[] { file });
            window.Show();
            try
            {
                await window.SizeTask;
                var name = Find<TextBox>(window, "NameBox");
                var apply = Find<Button>(window, "ApplyButton");

                name.Text = "bad/name.txt";
                Assert.True(apply.IsEnabled);
                Assert.False(await window.ApplyAsync());
                Assert.True(Find<TextBlock>(window, "StatusText").IsVisible);
                Assert.True(File.Exists(file));

                name.Text = "after.txt";
                Assert.True(apply.IsEnabled);
                Assert.True(await window.ApplyAsync());
                await Pump(2);
                Assert.False(File.Exists(file));
                Assert.True(File.Exists(Path.Combine(folder, "after.txt")));
                Assert.Equal("after.txt Properties", window.Title);
                Assert.False(apply.IsEnabled);
                Assert.False(Find<TextBlock>(window, "StatusText").IsVisible);
            }
            finally
            {
                window.Close();
                Directory.Delete(folder, true);
            }
        }

        [AvaloniaFact]
        public async Task NameBox_UsesTheOwnersFileListRename_WhichCanBeUndone()
        {
            string folder = NewFolder();
            string file = Path.Combine(folder, "report.txt");
            File.WriteAllText(file, "x");
            var main = new ModernMainWindow { Width = 1100, Height = 750 };
            main.Show();
            PropertiesWindow window = null;
            try
            {
                await Pump(20);
                MainViewModel vm = main.ViewModel;
                await vm.FileListViewModel.LoadFolderAsync(folder);
                window = new PropertiesWindow(new[] { file });
                window.Show(main);
                await window.SizeTask;
                Find<TextBox>(window, "NameBox").Text = "summary.txt";
                Assert.True(await window.ApplyAsync());
                Assert.True(File.Exists(Path.Combine(folder, "summary.txt")));
                Assert.True(vm.FileListViewModel.Operations.CanUndo);
                Assert.Contains("summary.txt", vm.FileListViewModel.Operations.UndoText);
            }
            finally
            {
                window?.Close();
                main.Close();
                await Pump(3);
                Directory.Delete(folder, true);
            }
        }

        [AvaloniaFact]
        public async Task ReadOnlyBox_IsAppliedToTheFile()
        {
            string folder = NewFolder();
            string file = Path.Combine(folder, "locked.txt");
            File.WriteAllText(file, "x");
            var window = new PropertiesWindow(new[] { file });
            window.Show();
            try
            {
                await window.SizeTask;
                var readOnly = Find<CheckBox>(window, "ReadOnlyBox");
                Assert.True(readOnly.IsEnabled);
                Assert.False(readOnly.IsChecked);
                readOnly.IsChecked = true;
                Assert.True(Find<Button>(window, "ApplyButton").IsEnabled);
                Assert.True(await window.ApplyAsync());
                Assert.True(new FileInfo(file).IsReadOnly);
                if (!OperatingSystem.IsWindows())
                {
                    Assert.Equal((UnixFileMode)0, File.GetUnixFileMode(file) & UnixFileMode.UserWrite);
                    Assert.False(Find<CheckBox>(window, "HiddenBox").IsEnabled); // a leading dot there, shown only
                }

                Assert.False(Find<Button>(window, "ApplyButton").IsEnabled);

                readOnly.IsChecked = false;
                Assert.True(await window.ApplyAsync());
                Assert.False(new FileInfo(file).IsReadOnly);
            }
            finally
            {
                window.Close();
                File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(folder, true);
            }
        }

        [AvaloniaFact]
        public async Task DetailsTab_ShowsImageDimensions()
        {
            string folder = NewFolder();
            string png = Path.Combine(folder, "picture.png");
            using (var bitmap = new WriteableBitmap(new PixelSize(37, 21), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul))
            {
                bitmap.Save(png);
            }

            var window = new PropertiesWindow(new[] { png });
            window.Show();
            try
            {
                window.Tabs.SelectedIndex = 1; // Details: loads on first show
                Assert.NotNull(window.DetailsTask);
                await window.DetailsTask;
                await Pump(2);
                Assert.Equal("37 × 21", Text(window, "Detail_Dimensions"));
                Assert.Equal(".png", Text(window, "Detail_Extension"));
                Assert.Equal(png, Text(window, "Detail_Full_path"));
            }
            finally
            {
                window.Close();
                Directory.Delete(folder, true);
            }
        }

        [AvaloniaFact]
        public async Task HashesTab_ComputesKnownChecksums_AndCompares()
        {
            string folder = NewFolder();
            string file = Path.Combine(folder, "abc.txt");
            File.WriteAllText(file, "abc");
            var window = new PropertiesWindow(new[] { file });
            window.Show();
            try
            {
                Find<Button>(window, "HashButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await window.HashTask;
                await Pump(2);
                Assert.Equal("900150983cd24fb0d6963f7d28e17f72", Text(window, "Md5Value"));
                Assert.Equal("a9993e364706816aba3e25717850c26c9cd0d89d", Text(window, "Sha1Value"));
                Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Text(window, "Sha256Value"));

                Find<TextBox>(window, "CompareBox").Text = "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD";
                Assert.Equal("Matches the SHA-256 checksum", Find<TextBlock>(window, "CompareResult").Text);
                Find<TextBox>(window, "CompareBox").Text = "0000";
                Assert.Equal("Does not match any checksum", Find<TextBlock>(window, "CompareResult").Text);
            }
            finally
            {
                window.Close();
                Directory.Delete(folder, true);
            }
        }

        [AvaloniaFact]
        public async Task Keyboard_CtrlTabCyclesTabs_EscapeCloses()
        {
            string folder = NewFolder();
            string file = Path.Combine(folder, "k.txt");
            File.WriteAllText(file, "k");
            var window = new PropertiesWindow(new[] { file });
            bool closed = false;
            window.Closed += (_, _) => closed = true;
            window.Show();
            try
            {
                await Pump(3);
                Assert.Equal(0, window.Tabs.SelectedIndex);
                window.KeyPress(Key.Tab, RawInputModifiers.Control, PhysicalKey.Tab, null);
                Assert.Equal(1, window.Tabs.SelectedIndex);
                window.KeyPress(Key.Tab, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.Tab, null);
                window.KeyPress(Key.Tab, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.Tab, null);
                Assert.Equal(3, window.Tabs.SelectedIndex);
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                await Pump(2);
                Assert.True(closed);
            }
            finally
            {
                if (!closed)
                {
                    window.Close();
                }

                Directory.Delete(folder, true);
            }
        }

        [AvaloniaFact]
        public async Task Drive_ShowsFileSystemAndSpace()
        {
            string root = OperatingSystem.IsWindows() ? Path.GetPathRoot(Path.GetTempPath()) : "/";
            var window = new PropertiesWindow(new[] { root });
            window.Show();
            try
            {
                await window.FactsTask;
                await Pump(2);
                Assert.Equal(new[] { "General", "Security" }, TabHeaders(window));
                Assert.NotEqual("Reading…", Text(window, "FileSystemValue"));
                Assert.NotEqual("Reading…", Text(window, "CapacityValue"));
            }
            finally
            {
                window.Close();
            }
        }

        /// <summary>Renders every tab in both themes when BYTEEXPLORER_SCREENSHOT_DIR is set (and checks they render).</summary>
        [AvaloniaFact]
        public async Task Screenshots_EveryTabInBothThemes()
        {
            string folder = NewFolder();
            string png = Path.Combine(folder, "holiday photo.png");
            using (var bitmap = new WriteableBitmap(new PixelSize(640, 480), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul))
            {
                bitmap.Save(png);
            }

            Directory.CreateDirectory(Path.Combine(folder, "Projects"));
            File.WriteAllText(Path.Combine(folder, "Projects", "plan.md"), new string('x', 3000));
            File.WriteAllText(Path.Combine(folder, "notes.txt"), "abc");
            string directory = Environment.GetEnvironmentVariable("BYTEEXPLORER_SCREENSHOT_DIR");
            ThemeVariant saved = Application.Current.RequestedThemeVariant;
            var windows = new List<PropertiesWindow>();
            try
            {
                foreach (string theme in new[] { "Light", "Dark" })
                {
                    Application.Current.RequestedThemeVariant = theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
                    var file = new PropertiesWindow(new[] { png });
                    windows.Add(file);
                    file.Show();
                    await file.SizeTask;
                    await file.FactsTask;
                    string[] names = TabHeaders(file).ToArray();
                    for (int i = 0; i < names.Length; i++)
                    {
                        file.Tabs.SelectedIndex = i;
                        if (names[i] == "Details")
                        {
                            await file.EnsureDetailsLoaded();
                        }
                        else if (names[i] == "Security")
                        {
                            await file.EnsureSecurityLoaded();
                        }
                        else if (names[i] == "Hashes")
                        {
                            Find<Button>(file, "HashButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            await file.HashTask;
                        }

                        Capture(file, directory, $"properties-{names[i].ToLowerInvariant()}-{theme}.png");
                    }

                    var folderWindow = new PropertiesWindow(new[] { Path.Combine(folder, "Projects") });
                    windows.Add(folderWindow);
                    folderWindow.Show();
                    await folderWindow.SizeTask;
                    Capture(folderWindow, directory, $"properties-folder-{theme}.png");

                    var multi = new PropertiesWindow(new[] { png, Path.Combine(folder, "notes.txt"), Path.Combine(folder, "Projects") });
                    windows.Add(multi);
                    multi.Show();
                    await multi.SizeTask;
                    Capture(multi, directory, $"properties-multiple-{theme}.png");

                    var drive = new PropertiesWindow(new[] { OperatingSystem.IsWindows() ? Path.GetPathRoot(folder) : "/" });
                    windows.Add(drive);
                    drive.Show();
                    await drive.FactsTask;
                    Capture(drive, directory, $"properties-drive-{theme}.png");
                }
            }
            finally
            {
                foreach (PropertiesWindow window in windows)
                {
                    window.Close();
                }

                Application.Current.RequestedThemeVariant = saved;
                await Pump(2);
                Directory.Delete(folder, true);
            }
        }

        private static void Capture(Window window, string directory, string name)
        {
            for (int i = 0; i < 3; i++)
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
            }

            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
                frame.Save(Path.Combine(directory, name));
            }
        }

        private static string NewFolder()
        {
            string folder = Path.Combine(Path.GetTempPath(), "be-props-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static IEnumerable<string> TabHeaders(PropertiesWindow window) =>
            window.Tabs.Items.OfType<TabItem>().Select(t => (string)t.Header).ToArray();

        private static T FindOrNull<T>(Window window, string name) where T : Control =>
            window.GetLogicalDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);

        private static T Find<T>(Window window, string name) where T : Control =>
            FindOrNull<T>(window, name) ?? throw new Xunit.Sdk.XunitException($"No {typeof(T).Name} named {name}");

        private static string Text(Window window, string name) => Find<TextBlock>(window, name).Text;

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
