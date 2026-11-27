// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Text;

namespace ByteExplorer.Infrastructure.DefaultApp
{
    /// <summary>The registry, as the Windows registration uses it: HKEY_CURRENT_USER only, string values only (tests use a dictionary).</summary>
    public interface IRegistryStore
    {
        /// <summary>A string value (<paramref name="name"/> empty: the key's default value); null when the key or value is missing.</summary>
        string GetValue(string key, string name);

        bool KeyExists(string key);

        /// <summary>Whether the key has neither values nor subkeys (a missing key is empty).</summary>
        bool IsEmpty(string key);

        /// <summary>Sets a string value, creating the key (and its parents) when missing.</summary>
        void SetValue(string key, string name, string value);

        void DeleteValue(string key, string name);

        /// <summary>Deletes the key with everything under it; nothing when missing.</summary>
        void DeleteKeyTree(string key);
    }

    /// <summary>
    /// Makes ByteExplorer what Windows opens folders with, for the current user only (HKEY_CURRENT_USER\Software\Classes,
    /// no administrator rights), the way other Explorer replacements do it:
    /// <list type="bullet">
    /// <item>a "ByteExplorer" verb on <c>Directory</c> (every file-system folder) and <c>Drive</c> (C:\, a USB stick),
    /// made their default verb, so a double-click on a folder on the desktop, "Open" in a program, or a folder opened by
    /// ShellExecute starts ByteExplorer;</item>
    /// <item>Win+E (the "File Explorer" folder <c>{52205fd8-5dfb-447d-801a-d0b52f2e83e1}</c>'s "opennewwindow" verb, with
    /// its DelegateExecute emptied so the command line is used).</item>
    /// </list>
    /// <c>Folder</c> is left alone on purpose: it also covers Control Panel, the Recycle Bin, This PC and other virtual
    /// folders ByteExplorer can't show; they keep opening in File Explorer. Programs that run explorer.exe themselves
    /// (with a path) also still get Explorer: no per-user setting changes that.
    /// <para>
    /// What was there before (another file manager's verb, Explorer's own setting) is saved under
    /// HKEY_CURRENT_USER\Software\ByteExplorer\DefaultFileManager on the first registration and put back by
    /// <see cref="Unregister"/>; a .reg file that does the same (<see cref="BuildRestoreScript"/>) is kept outside the
    /// app's folder, for the case that ByteExplorer is deleted while still the default.
    /// </para>
    /// </summary>
    public sealed class WindowsFileManagerRegistration
    {
        public const string VerbName = "ByteExplorer";
        internal const string Classes = @"Software\Classes";
        internal const string BackupKey = @"Software\ByteExplorer\DefaultFileManager";
        internal const string FileExplorerClsid = "{52205fd8-5dfb-447d-801a-d0b52f2e83e1}";
        internal const string WinEClsidKey = Classes + @"\CLSID\" + FileExplorerClsid;
        internal const string WinEVerbKey = WinEClsidKey + @"\shell\opennewwindow";
        private static readonly string[] Targets = { "Directory", "Drive" };

        private readonly IRegistryStore _registry;

        public WindowsFileManagerRegistration(IRegistryStore registry)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        private static string ShellKey(string target) => Classes + @"\" + target + @"\shell";

        private static string VerbKey(string target) => ShellKey(target) + @"\" + VerbName;

        /// <summary>ByteExplorer opens folders now (its verb is the folders' default and has a command).</summary>
        public bool IsRegistered =>
            string.Equals(_registry.GetValue(ShellKey("Directory"), string.Empty), VerbName, StringComparison.OrdinalIgnoreCase) &&
            _registry.GetValue(VerbKey("Directory") + @"\command", string.Empty) != null;

        /// <summary>
        /// Something <see cref="Register"/> wrote is still there (the verb, the backup of the previous setting), even when
        /// another program has become the default since: the uninstaller removes it then too, so no "Open in ByteExplorer"
        /// pointing at a deleted program is left in the folders' menu.
        /// </summary>
        public bool HasTraces
        {
            get
            {
                foreach (string target in Targets)
                {
                    if (_registry.KeyExists(VerbKey(target)))
                    {
                        return true;
                    }
                }

                return _registry.KeyExists(BackupKey);
            }
        }

        /// <summary>The command folders open with now (null when not registered).</summary>
        public string RegisteredCommand => IsRegistered ? _registry.GetValue(VerbKey("Directory") + @"\command", string.Empty) : null;

        /// <summary>
        /// Registers (or re-registers, e.g. after the app moved) with <paramref name="folderCommand"/> for folders and
        /// drives (it contains "%1") and <paramref name="newWindowCommand"/> for Win+E.
        /// </summary>
        public void Register(string folderCommand, string newWindowCommand, string iconPath)
        {
            if (string.IsNullOrEmpty(folderCommand) || !folderCommand.Contains("%1", StringComparison.Ordinal))
            {
                throw new ArgumentException("The folder command must take the folder as %1.", nameof(folderCommand));
            }

            BackUpOnce();
            foreach (string target in Targets)
            {
                string verb = VerbKey(target);
                _registry.SetValue(verb, string.Empty, "Open in ByteExplorer");
                _registry.SetValue(verb, "MUIVerb", "Open in ByteExplorer");
                if (!string.IsNullOrEmpty(iconPath))
                {
                    _registry.SetValue(verb, "Icon", iconPath);
                }

                _registry.SetValue(verb + @"\command", string.Empty, folderCommand);
                _registry.SetValue(ShellKey(target), string.Empty, VerbName);
            }

            if (!string.IsNullOrEmpty(newWindowCommand))
            {
                _registry.SetValue(WinEVerbKey + @"\command", string.Empty, newWindowCommand);
                _registry.SetValue(WinEVerbKey + @"\command", "DelegateExecute", string.Empty);
            }
        }

        /// <summary>Puts back what was there before <see cref="Register"/>. Parts another app has changed since are left to it.</summary>
        public void Unregister()
        {
            foreach (string target in Targets)
            {
                _registry.DeleteKeyTree(VerbKey(target));
                string shell = ShellKey(target);
                if (string.Equals(_registry.GetValue(shell, string.Empty), VerbName, StringComparison.OrdinalIgnoreCase))
                {
                    string previous = _registry.GetValue(BackupKey, target + ".Default");
                    if (_registry.GetValue(BackupKey, target + ".HadDefault") == "1" && previous != null)
                    {
                        _registry.SetValue(shell, string.Empty, previous);
                    }
                    else
                    {
                        _registry.DeleteValue(shell, string.Empty);
                    }
                }

                DeleteIfEmpty(shell);
            }

            string winECommand = _registry.GetValue(WinEVerbKey + @"\command", string.Empty);
            if (winECommand != null && winECommand.Contains(LaunchSwitch, StringComparison.OrdinalIgnoreCase))
            {
                if (_registry.GetValue(BackupKey, "WinE.Existed") == "1")
                {
                    RestoreValue(WinEVerbKey + @"\command", string.Empty, "WinE.Command");
                    RestoreValue(WinEVerbKey + @"\command", "DelegateExecute", "WinE.DelegateExecute");
                }
                else
                {
                    _registry.DeleteKeyTree(WinEVerbKey);
                    DeleteIfEmpty(WinEClsidKey + @"\shell");
                    DeleteIfEmpty(WinEClsidKey);
                }
            }

            _registry.DeleteKeyTree(BackupKey);
            DeleteIfEmpty(@"Software\ByteExplorer");
        }

        private const string LaunchSwitch = Launch.LaunchRequest.FromShellSwitch;

        /// <summary>
        /// A .reg file (Windows Registry Editor 5.00) that undoes the registration, as <see cref="Unregister"/> would:
        /// double-clicked, it gives folders back to File Explorer even with ByteExplorer gone. From the saved backup.
        /// </summary>
        public string BuildRestoreScript()
        {
            var reg = new StringBuilder();
            reg.Append("Windows Registry Editor Version 5.00\r\n\r\n");
            reg.Append("; Gives folders, drives and Win+E back to what opened them before ByteExplorer was made the default.\r\n\r\n");
            foreach (string target in Targets)
            {
                reg.Append("[-HKEY_CURRENT_USER\\").Append(VerbKey(target)).Append("]\r\n\r\n");
                reg.Append("[HKEY_CURRENT_USER\\").Append(ShellKey(target)).Append("]\r\n");
                string previous = _registry.GetValue(BackupKey, target + ".Default");
                reg.Append(_registry.GetValue(BackupKey, target + ".HadDefault") == "1" && previous != null
                    ? "@=\"" + Escape(previous) + "\"\r\n\r\n"
                    : "@=-\r\n\r\n");
            }

            if (_registry.GetValue(BackupKey, "WinE.Existed") == "1")
            {
                reg.Append("[HKEY_CURRENT_USER\\").Append(WinEVerbKey).Append("\\command]\r\n");
                AppendRestoredValue(reg, "@", "WinE.Command");
                AppendRestoredValue(reg, "\"DelegateExecute\"", "WinE.DelegateExecute");
                reg.Append("\r\n");
            }
            else
            {
                reg.Append("[-HKEY_CURRENT_USER\\").Append(WinEClsidKey).Append("]\r\n\r\n");
            }

            reg.Append("[-HKEY_CURRENT_USER\\").Append(BackupKey).Append("]\r\n");
            return reg.ToString();
        }

        private void AppendRestoredValue(StringBuilder reg, string name, string backupName)
        {
            string value = _registry.GetValue(BackupKey, backupName);
            reg.Append(name).Append(value == null ? "=-" : "=\"" + Escape(value) + "\"").Append("\r\n");
        }

        private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

        /// <summary>Saves what Register replaces, once: a re-registration (the app moved) must not save its own values.</summary>
        private void BackUpOnce()
        {
            if (_registry.KeyExists(BackupKey))
            {
                return;
            }

            foreach (string target in Targets)
            {
                string previous = _registry.GetValue(ShellKey(target), string.Empty);
                bool hadDefault = previous != null && !string.Equals(previous, VerbName, StringComparison.OrdinalIgnoreCase);
                _registry.SetValue(BackupKey, target + ".HadDefault", hadDefault ? "1" : "0");
                _registry.SetValue(BackupKey, target + ".Default", hadDefault ? previous : string.Empty);
            }

            bool winEExisted = _registry.KeyExists(WinEVerbKey);
            _registry.SetValue(BackupKey, "WinE.Existed", winEExisted ? "1" : "0");
            if (winEExisted)
            {
                BackUpValue(WinEVerbKey + @"\command", string.Empty, "WinE.Command");
                BackUpValue(WinEVerbKey + @"\command", "DelegateExecute", "WinE.DelegateExecute");
            }
        }

        private void BackUpValue(string key, string name, string backupName)
        {
            string value = _registry.GetValue(key, name);
            if (value != null)
            {
                _registry.SetValue(BackupKey, backupName, value);
            }
        }

        private void RestoreValue(string key, string name, string backupName)
        {
            string value = _registry.GetValue(BackupKey, backupName);
            if (value != null)
            {
                _registry.SetValue(key, name, value);
            }
            else
            {
                _registry.DeleteValue(key, name);
            }
        }

        private void DeleteIfEmpty(string key)
        {
            if (_registry.KeyExists(key) && _registry.IsEmpty(key))
            {
                _registry.DeleteKeyTree(key);
            }
        }

        /// <summary>The program a registered command starts (its first, possibly quoted, token).</summary>
        public static string ProgramOf(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return null;
            }

            string text = command.TrimStart();
            if (text.StartsWith('"'))
            {
                int end = text.IndexOf('"', 1);
                return end > 1 ? text.Substring(1, end - 1) : null;
            }

            int space = text.IndexOf(' ');
            return space < 0 ? text : text.Substring(0, space);
        }

        /// <summary>A command line: each part quoted (the system passes the folder as one quoted "%1").</summary>
        public static string BuildCommand(IReadOnlyList<string> program, params string[] arguments)
        {
            var parts = new List<string>();
            foreach (string part in program)
            {
                parts.Add("\"" + part + "\"");
            }

            foreach (string argument in arguments)
            {
                parts.Add(argument == "%1" ? "\"%1\"" : argument);
            }

            return string.Join(" ", parts);
        }
    }
}
