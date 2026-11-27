// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using ByteExplorer.Infrastructure.DefaultApp;
using ByteExplorer.Infrastructure.Launch;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ByteExplorer.ViewModels;
using ByteExplorer.ViewModels.Settings;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// ByteExplorer as the default file manager: the command line it is started with, handing a request to the running
    /// copy, and the Windows (on a registry in memory) and Linux (with a fake xdg-mime) registrations, undone exactly.
    /// </summary>
    [Collection(GlobalStateCollection.Name)]
    public sealed class DefaultFileManagerTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "be-default-" + Guid.NewGuid().ToString("N"));

        public DefaultFileManagerTests()
        {
            Directory.CreateDirectory(Path.Combine(_root, "Photos"));
            File.WriteAllText(Path.Combine(_root, "Photos", "cat.jpg"), "x");
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

        // ------------------------------------------------------------------ command line

        [Fact]
        public void CommandLine_TakesFoldersFilesUrisAndExplorersSwitches()
        {
            string photos = Path.Combine(_root, "Photos");
            string cat = Path.Combine(photos, "cat.jpg");

            Assert.False(LaunchRequest.Parse(Array.Empty<string>(), _root).ShouldForward);
            Assert.False(LaunchRequest.Parse(new[] { "--some-switch" }, _root).ShouldForward);

            LaunchRequest folder = LaunchRequest.Parse(new[] { LaunchRequest.FromShellSwitch, photos }, _root);
            Assert.True(folder.FromShell);
            Assert.Equal(photos, folder.Folder);
            Assert.Null(folder.Select);
            Assert.True(folder.ShouldForward);

            Assert.Equal(photos, LaunchRequest.Parse(new[] { "Photos" }, _root).Folder); // relative to the starting folder
            Assert.Equal(photos, LaunchRequest.Parse(new[] { "\"" + photos + "\"" }, _root).Folder);

            LaunchRequest file = LaunchRequest.Parse(new[] { cat }, _root);
            Assert.Equal(photos, file.Folder);
            Assert.Equal(cat, file.Select);

            LaunchRequest select = LaunchRequest.Parse(new[] { "/select," + cat }, _root);
            Assert.Equal(photos, select.Folder);
            Assert.Equal(cat, select.Select);
            Assert.Equal(cat, LaunchRequest.Parse(new[] { "/select", cat }, _root).Select);

            LaunchRequest uri = LaunchRequest.Parse(new[] { LaunchRequest.FromShellSwitch, new Uri(photos).AbsoluteUri }, _root);
            Assert.Equal(photos, uri.Folder);

            LaunchRequest winE = LaunchRequest.Parse(new[] { LaunchRequest.FromShellSwitch }, _root);
            Assert.True(winE.FromShell);
            Assert.False(winE.HasFolder);
            Assert.True(winE.ShouldForward);

            // Shell namespaces and other schemes are not folders ByteExplorer can show.
            Assert.False(LaunchRequest.Parse(new[] { "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}" }, _root).HasFolder);
            Assert.False(LaunchRequest.Parse(new[] { "shell:RecycleBinFolder" }, _root).HasFolder);
            Assert.False(LaunchRequest.Parse(new[] { "https://example.com/" }, _root).HasFolder);
            Assert.False(LaunchRequest.Parse(new[] { "/e," }, _root).HasFolder);
        }

        // ------------------------------------------------------------------ the window

        [AvaloniaFact]
        public async Task TheWindow_OpensTheFolderItWasStartedWith_AndLaterRequestsInNewTabs()
        {
            string photos = Path.Combine(_root, "Photos");
            string cat = Path.Combine(photos, "cat.jpg");
            App.StartupRequest = LaunchRequest.Parse(new[] { LaunchRequest.FromShellSwitch, photos }, _root);
            var window = new ModernMainWindow { Width = 1200, Height = 800 };
            window.Show();
            MainViewModel main = window.ViewModel;
            try
            {
                await WaitFor(() => window.FirstPlaceShown.IsCompleted);
                await WaitFor(() => main.FileListViewModel.CurrentPath == photos);
                Assert.Same(LaunchRequest.Empty, App.StartupRequest); // used once
                int tabs = main.Tabs.Tabs.Count;

                // Another start hands over "/select,cat.jpg": a new tab, the file selected.
                window.HandleLaunchRequest(LaunchRequest.Parse(new[] { "/select," + cat }, _root));
                await WaitFor(() => main.Tabs.Tabs.Count == tabs + 1 && (main.FileListViewModel.SelectedItem as FileListItemViewModel)?.FullPath == cat);
                Assert.Equal(photos, main.FileListViewModel.CurrentPath);

                // Win+E: a new tab at the startup place.
                window.HandleLaunchRequest(LaunchRequest.Parse(new[] { LaunchRequest.FromShellSwitch }, _root));
                await WaitFor(() => main.Tabs.Tabs.Count == tabs + 2);

                // The setting is on the Folder options page; where the system can't have it, it says why.
                window.OpenSettings();
                await Pump(10);
                SettingsViewModel vm = window.SettingsWindowForTests.ViewModel;
                ToggleSetting toggle = vm.Pages.Single(p => p.Key == "folderoptions").Groups.First().Items.OfType<ToggleSetting>().Single();
                Assert.Equal("Make ByteExplorer the default file explorer", toggle.Title);
                Assert.Equal(DefaultFileManager.IsSupported, toggle.IsAvailable);
                if (!DefaultFileManager.IsSupported)
                {
                    Assert.False(toggle.Value);
                    Assert.Equal(DefaultFileManager.UnsupportedReason, toggle.Description);
                }
            }
            finally
            {
                App.StartupRequest = LaunchRequest.Empty;
                window.SettingsWindowForTests?.Close();
                window.CloseWithoutAsking();
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

        // ------------------------------------------------------------------ handing over to the running copy

        [Fact]
        public void Requests_RoundTrip_AndOnlyFullPathsAreTaken()
        {
            var request = new LaunchRequest { FromShell = true, Folder = _root, Select = Path.Combine(_root, "Photos") };
            LaunchRequest back = SingleInstance.Deserialize(SingleInstance.Serialize(request));
            Assert.True(back.FromShell);
            Assert.Equal(request.Folder, back.Folder);
            Assert.Equal(request.Select, back.Select);

            Assert.Null(SingleInstance.Deserialize("not json"));
            Assert.Null(SingleInstance.Deserialize("{\"Folder\":\"relative\\\\path\"}").Folder);
            Assert.Null(SingleInstance.Deserialize("{\"Folder\":\"" + "x\\u0000y" + "\"}").Folder);
        }

        [Fact]
        public void ALaterStart_HandsItsFolderToTheRunningCopy()
        {
            Assert.True(SingleInstance.IsPrimary || SingleInstance.TryBecomePrimary(), "no other test copy holds the channel");
            var received = new List<LaunchRequest>();
            using var arrived = new ManualResetEventSlim();
            SingleInstance.StartListening(r =>
            {
                lock (received)
                {
                    received.Add(r);
                }

                arrived.Set();
            });
            try
            {
                string photos = Path.Combine(_root, "Photos");
                bool forwarded = false;
                for (int attempt = 0; attempt < 20 && !forwarded; attempt++)
                {
                    forwarded = SingleInstance.TryForward(LaunchRequest.Parse(new[] { LaunchRequest.FromShellSwitch, photos }, _root), TimeSpan.FromSeconds(2));
                }

                Assert.True(forwarded);
                Assert.True(arrived.Wait(TimeSpan.FromSeconds(5)));
                LaunchRequest request = received.Single();
                Assert.Equal(photos, request.Folder);
                Assert.True(request.FromShell);
            }
            finally
            {
                SingleInstance.StopListening();
            }
        }

        // ------------------------------------------------------------------ Windows

        private const string FolderCommand = "\"C:\\Apps\\ByteExplorer\\ByteExplorer.exe\" --from-shell \"%1\"";
        private const string NewWindowCommand = "\"C:\\Apps\\ByteExplorer\\ByteExplorer.exe\" --from-shell";

        [Fact]
        public void Windows_Registration_OpensFoldersDrivesAndWinE_AndIsUndoneExactly()
        {
            var registry = new MemoryRegistry();
            // Another file manager had folders before; Explorer's Win+E is untouched (no HKCU key).
            registry.SetValue(@"Software\Classes\Directory\shell", "", "openinfiles");
            registry.SetValue(@"Software\Classes\Directory\shell\openinfiles\command", "", "\"files.exe\" \"%1\"");
            var before = registry.Snapshot();
            var registration = new WindowsFileManagerRegistration(registry);
            Assert.False(registration.IsRegistered);

            registration.Register(FolderCommand, NewWindowCommand, "C:\\Apps\\ByteExplorer\\ByteExplorer.exe,0");
            Assert.True(registration.IsRegistered);
            Assert.Equal(FolderCommand, registration.RegisteredCommand);
            Assert.Equal("ByteExplorer", registry.GetValue(@"Software\Classes\Directory\shell", ""));
            Assert.Equal("ByteExplorer", registry.GetValue(@"Software\Classes\Drive\shell", ""));
            Assert.Equal(FolderCommand, registry.GetValue(@"Software\Classes\Drive\shell\ByteExplorer\command", ""));
            Assert.Equal(NewWindowCommand, registry.GetValue(WindowsFileManagerRegistration.WinEVerbKey + @"\command", ""));
            Assert.Equal("", registry.GetValue(WindowsFileManagerRegistration.WinEVerbKey + @"\command", "DelegateExecute"));
            Assert.Null(registry.GetValue(@"Software\Classes\Folder\shell", "")); // virtual folders stay with Explorer
            Assert.Equal(@"C:\Apps\ByteExplorer\ByteExplorer.exe", WindowsFileManagerRegistration.ProgramOf(registration.RegisteredCommand));

            // The undo file names what to put back.
            string script = registration.BuildRestoreScript();
            Assert.StartsWith("Windows Registry Editor Version 5.00", script);
            Assert.Contains("[-HKEY_CURRENT_USER\\Software\\Classes\\Directory\\shell\\ByteExplorer]", script);
            Assert.Contains("@=\"openinfiles\"", script);
            Assert.Contains("[HKEY_CURRENT_USER\\Software\\Classes\\Drive\\shell]\r\n@=-", script);
            Assert.Contains("[-HKEY_CURRENT_USER\\" + WindowsFileManagerRegistration.WinEClsidKey + "]", script);

            // Moved to another folder: registered again, the original backup is kept.
            registration.Register(FolderCommand.Replace("C:\\Apps", "D:\\Tools"), NewWindowCommand.Replace("C:\\Apps", "D:\\Tools"), null);
            Assert.Contains("D:\\Tools", registration.RegisteredCommand);
            Assert.Equal("openinfiles", registry.GetValue(WindowsFileManagerRegistration.BackupKey, "Directory.Default"));

            registration.Unregister();
            Assert.False(registration.IsRegistered);
            Assert.Equal(before, registry.Snapshot()); // exactly as before, nothing of ours left
        }

        [Fact]
        public void Windows_Unregister_RestoresAnExistingWinECommand_AndLeavesAnotherAppsChange()
        {
            var registry = new MemoryRegistry();
            registry.SetValue(WindowsFileManagerRegistration.WinEVerbKey + @"\command", "", "\"other.exe\"");
            registry.SetValue(WindowsFileManagerRegistration.WinEVerbKey + @"\command", "DelegateExecute", "{11dbb47c-a525-400b-9e80-a54615a090c0}");
            var before = registry.Snapshot();
            var registration = new WindowsFileManagerRegistration(registry);
            registration.Register(FolderCommand, NewWindowCommand, null);
            Assert.Contains("\"DelegateExecute\"=\"{11dbb47c-a525-400b-9e80-a54615a090c0}\"", registration.BuildRestoreScript());
            registration.Unregister();
            Assert.Equal(before, registry.Snapshot());

            // Another app took folders over after ByteExplorer: turning ByteExplorer off leaves that app's choice alone.
            registration.Register(FolderCommand, NewWindowCommand, null);
            registry.SetValue(@"Software\Classes\Directory\shell", "", "openinother");
            registration.Unregister();
            Assert.Equal("openinother", registry.GetValue(@"Software\Classes\Directory\shell", ""));
            Assert.False(registry.KeyExists(@"Software\Classes\Directory\shell\ByteExplorer"));
            Assert.Throws<ArgumentException>(() => registration.Register("\"x.exe\"", null, null)); // the folder must be passed
        }

        [Fact]
        public void Windows_Commands_QuoteTheProgramAndTheFolder()
        {
            Assert.Equal(FolderCommand, WindowsFileManagerRegistration.BuildCommand(new[] { @"C:\Apps\ByteExplorer\ByteExplorer.exe" }, "--from-shell", "%1"));
            Assert.Equal("\"C:\\dotnet\\dotnet.exe\" \"C:\\b\\ByteExplorer.dll\" --from-shell",
                WindowsFileManagerRegistration.BuildCommand(new[] { @"C:\dotnet\dotnet.exe", @"C:\b\ByteExplorer.dll" }, "--from-shell"));
            Assert.Equal("app", WindowsFileManagerRegistration.ProgramOf("app --x"));
            Assert.Null(WindowsFileManagerRegistration.ProgramOf("  "));
        }

        // ------------------------------------------------------------------ Linux

        [Fact]
        public void Linux_Registration_SetsTheFolderHandler_AndGivesItBack()
        {
            string applications = Path.Combine(_root, "share", "applications");
            string config = Path.Combine(_root, "config");
            Directory.CreateDirectory(config);
            string handler = "org.gnome.Nautilus.desktop";
            var calls = new List<string>();
            (int, string) Run(string program, string[] args)
            {
                calls.Add(program + " " + string.Join(" ", args));
                if (program == "xdg-mime" && args[0] == "query")
                {
                    return (0, handler + "\n");
                }

                if (program == "xdg-mime" && args[0] == "default")
                {
                    handler = args[1];
                    File.WriteAllText(Path.Combine(config, "mimeapps.list"), "[Default Applications]\ninode/directory=" + handler + ";\ntext/plain=gedit.desktop;\n");
                    return (0, string.Empty);
                }

                return (0, string.Empty);
            }

            var registration = new LinuxFileManagerRegistration(Run, applications, config, Path.Combine(_root, "data", "default-file-manager.json"));
            Assert.False(registration.IsRegistered);
            registration.Register(new[] { "/opt/Byte Explorer/ByteExplorer" }, "/opt/icon.png");
            Assert.True(registration.IsRegistered);
            Assert.Equal(LinuxFileManagerRegistration.DesktopFileName, handler);
            string entry = File.ReadAllText(registration.DesktopFilePath);
            Assert.Contains("Exec=\"/opt/Byte Explorer/ByteExplorer\" --from-shell %U\n", entry);
            Assert.Contains("MimeType=inode/directory;", entry);
            Assert.Equal("\"/opt/Byte Explorer/ByteExplorer\" --from-shell %U", registration.RegisteredExec);

            registration.Unregister();
            Assert.Equal("org.gnome.Nautilus.desktop", handler); // the file manager from before
            Assert.False(File.Exists(registration.DesktopFilePath));
            Assert.False(registration.IsRegistered);

            // No handler before: ByteExplorer's line is taken out of mimeapps.list, the rest kept.
            handler = string.Empty;
            registration.Register(new[] { "/usr/bin/byteexplorer" }, null);
            registration.Unregister();
            string mimeApps = File.ReadAllText(Path.Combine(config, "mimeapps.list"));
            Assert.DoesNotContain(LinuxFileManagerRegistration.DesktopFileName, mimeApps);
            Assert.Contains("text/plain=gedit.desktop;", mimeApps);
        }

        [Fact]
        public void Linux_ExecArguments_FollowTheDesktopEntryQuotingRules()
        {
            Assert.Equal("/usr/bin/byteexplorer", LinuxFileManagerRegistration.QuoteExecArgument("/usr/bin/byteexplorer"));
            Assert.Equal("\"/opt/a b/x\"", LinuxFileManagerRegistration.QuoteExecArgument("/opt/a b/x"));
            Assert.Equal("\"/opt/\\$HOME\\\"q\\`/x\"", LinuxFileManagerRegistration.QuoteExecArgument("/opt/$HOME\"q`/x"));
            Assert.Equal("/opt/100%%/x", LinuxFileManagerRegistration.QuoteExecArgument("/opt/100%/x"));
            // A backslash is escaped for Exec, then again as the file's string escape.
            Assert.Contains("Exec=\"/opt/a\\\\\\\\b\" --from-shell %U", LinuxFileManagerRegistration.BuildDesktopEntry(new[] { "/opt/a\\b" }, null));
        }

        [Fact]
        public void Windows_HasTraces_SeesALeftoverVerbAfterAnotherAppTookOver()
        {
            var registry = new MemoryRegistry();
            var registration = new WindowsFileManagerRegistration(registry);
            Assert.False(registration.HasTraces);

            registration.Register(FolderCommand, NewWindowCommand, null);
            Assert.True(registration.HasTraces);

            // Another file manager becomes the default: ByteExplorer is no longer it, but its verb is still in the menu.
            registry.SetValue(@"Software\Classes\Directory\shell", "", "openinfiles");
            Assert.False(registration.IsRegistered);
            Assert.True(registration.HasTraces);

            registration.Unregister();
            Assert.False(registration.HasTraces);
            Assert.Equal("openinfiles", registry.GetValue(@"Software\Classes\Directory\shell", "")); // left to the other app
        }

        /// <summary>HKEY_CURRENT_USER in memory: keys (case-insensitive, as the registry's) with string values.</summary>
        private sealed class MemoryRegistry : IRegistryStore
        {
            private readonly Dictionary<string, Dictionary<string, string>> _keys = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            public string GetValue(string key, string name) =>
                _keys.TryGetValue(key, out var values) && values.TryGetValue(name ?? string.Empty, out string value) ? value : null;

            public bool KeyExists(string key) => _keys.ContainsKey(key);

            public bool IsEmpty(string key) =>
                !_keys.TryGetValue(key, out var values) || (values.Count == 0 && !_keys.Keys.Any(k => k.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase)));

            public void SetValue(string key, string name, string value)
            {
                for (int i = key.IndexOf('\\'); i > 0; i = key.IndexOf('\\', i + 1))
                {
                    _keys.TryAdd(key.Substring(0, i), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                }

                if (!_keys.TryGetValue(key, out var values))
                {
                    _keys[key] = values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                values[name ?? string.Empty] = value;
            }

            public void DeleteValue(string key, string name)
            {
                if (_keys.TryGetValue(key, out var values))
                {
                    values.Remove(name ?? string.Empty);
                }
            }

            public void DeleteKeyTree(string key)
            {
                foreach (string k in _keys.Keys.Where(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase) || k.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    _keys.Remove(k);
                }
            }

            /// <summary>Every key with values, as text (empty parent keys aside: those the real registry also keeps).</summary>
            public string Snapshot() =>
                string.Join("\n", _keys.Where(k => k.Value.Count > 0).OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(k => k.Key + ": " + string.Join(", ", k.Value.OrderBy(v => v.Key).Select(v => v.Key + "=" + v.Value))));
        }
    }
}
