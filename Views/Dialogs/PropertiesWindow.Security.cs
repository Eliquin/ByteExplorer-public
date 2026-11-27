// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ByteExplorer.Core.Platform;
using Serilog;

namespace ByteExplorer.Views.Dialogs
{
    /// <summary>
    /// The Security tab, read-only: Unix owner, group and mode bits as Explorer-like Allow tables; on Windows the owner and
    /// the ACL's entries (merged per identity and Allow/Deny, named as Explorer names the permissions).
    /// </summary>
    public sealed partial class PropertiesWindow
    {
        private ContentControl _securityHost;
        private Task _securityTask;

        internal Task SecurityTask => _securityTask ?? Task.CompletedTask;

        private Control BuildSecurity()
        {
            _securityHost = new ContentControl { Name = "SecurityHost" };
            TextBlock loading = DialogChrome.Text("Reading permissions…", 12, FontWeight.Normal, "TextMuted");
            loading.Margin = new Thickness(18, 16);
            _securityHost.Content = loading;
            _tabs.SelectionChanged += (_, _) =>
            {
                if (_securityTask == null && ReferenceEquals((_tabs.SelectedItem as TabItem)?.Tag, "Security"))
                {
                    _securityTask = LoadSecurityAsync();
                }
            };
            return _securityHost;
        }

        /// <summary>Starts reading the Security tab if it has not been (tests call it without switching tabs).</summary>
        internal Task EnsureSecurityLoaded()
        {
            if (_securityTask == null && _securityHost != null)
            {
                _securityTask = LoadSecurityAsync();
            }

            return SecurityTask;
        }

        private async Task LoadSecurityAsync()
        {
            CancellationToken token = Token;
            string path = _path;
            bool isFolder = _kind != ItemKind.File;
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    WindowsSecurityInfo security = await Task.Run(() => FileSystemFacts.ReadWindowsSecurity(path, isFolder), token);
                    if (!token.IsCancellationRequested && !_closed)
                    {
                        _securityHost.Content = BuildWindowsSecurity(path, security);
                    }

                    return;
                }

                (bool hasStat, UnixStat stat, UnixFileMode? mode) = await Task.Run(() =>
                {
                    bool ok = FileSystemFacts.TryGetUnixStat(path, FileSystemFacts.CreateStatBuffer(), out UnixStat s);
                    UnixFileMode? m = null;
                    try
                    {
                        m = OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(path);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
                    {
                    }

                    // Resolve the names here: getpwuid may consult the network (LDAP/NSS).
                    if (ok)
                    {
                        FileSystemFacts.UserName(s.Uid);
                        FileSystemFacts.GroupName(s.Gid);
                    }

                    return (ok, s, m);
                }, token);
                if (!token.IsCancellationRequested && !_closed)
                {
                    _securityHost.Content = BuildUnixSecurity(path, hasStat, stat, mode);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "Security for the Properties window failed for {Path}", path);
            }
        }

        private Control BuildUnixSecurity(string path, bool hasStat, UnixStat stat, UnixFileMode? mode)
        {
            var list = new PropertyList();
            list.Row("Object name", Value(path, "ObjectNameValue"));
            if (hasStat)
            {
                list.Row("Owner", Value($"{FileSystemFacts.UserName(stat.Uid)} (uid {stat.Uid.ToString(CultureInfo.InvariantCulture)})", "OwnerValue"));
                list.Row("Group", Value($"{FileSystemFacts.GroupName(stat.Gid)} (gid {stat.Gid.ToString(CultureInfo.InvariantCulture)})", "GroupValue"));
            }

            if (!mode.HasValue)
            {
                list.Separator();
                list.Row("Permissions", Value("The permissions could not be read."));
                return list.Grid;
            }

            UnixFileMode m = mode.Value;
            list.Separator();
            list.Header("Permissions");
            bool folder = _kind != ItemKind.File;
            string[] columns = folder ? new[] { "List", "Create & delete", "Enter" } : new[] { "Read", "Write", "Execute" };
            var table = new Grid { ColumnDefinitions = new ColumnDefinitions("1.3*,*,*,*"), Margin = new Thickness(0, 0, 0, 10) };
            table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (int c = 0; c < 3; c++)
            {
                TextBlock heading = DialogChrome.Text(columns[c], 11, FontWeight.SemiBold, "TextSecondary");
                heading.HorizontalAlignment = HorizontalAlignment.Center;
                heading.TextAlignment = TextAlignment.Center;
                heading.Margin = new Thickness(0, 0, 0, 6);
                Grid.SetColumn(heading, c + 1);
                table.Children.Add(heading);
            }

            void Line(int row, string who, UnixFileMode read, UnixFileMode write, UnixFileMode execute)
            {
                table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                TextBlock label = DialogChrome.Text(who, 12);
                label.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetRow(label, row);
                table.Children.Add(label);
                UnixFileMode[] flags = { read, write, execute };
                for (int c = 0; c < 3; c++)
                {
                    var box = new CheckBox
                    {
                        IsChecked = (m & flags[c]) != 0,
                        IsHitTestVisible = false,
                        Focusable = false,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        MinHeight = 26,
                        Name = $"Perm_{who.Split(' ')[0]}_{c}"
                    };
                    Grid.SetRow(box, row);
                    Grid.SetColumn(box, c + 1);
                    table.Children.Add(box);
                }
            }

            string ownerName = hasStat ? FileSystemFacts.UserName(stat.Uid) : null;
            string groupName = hasStat ? FileSystemFacts.GroupName(stat.Gid) : null;
            Line(1, ownerName != null ? $"Owner ({ownerName})" : "Owner", UnixFileMode.UserRead, UnixFileMode.UserWrite, UnixFileMode.UserExecute);
            Line(2, groupName != null ? $"Group ({groupName})" : "Group", UnixFileMode.GroupRead, UnixFileMode.GroupWrite, UnixFileMode.GroupExecute);
            Line(3, "Others", UnixFileMode.OtherRead, UnixFileMode.OtherWrite, UnixFileMode.OtherExecute);
            list.Wide(table);

            list.Row("Mode", Value(FormatMode(m), "ModeValue"));
            string special = SpecialBits(m, folder);
            if (special != null)
            {
                list.Row("Special", Value(special, "SpecialValue"));
            }

            TextBlock note = DialogChrome.Text("Change permissions with the system's tools (for example chmod and chown).", 11, FontWeight.Normal, "TextMuted");
            note.Margin = new Thickness(0, 4, 0, 0);
            list.Wide(note);
            return list.Grid;
        }

        private static string SpecialBits(UnixFileMode mode, bool folder)
        {
            var parts = new System.Collections.Generic.List<string>();
            if ((mode & UnixFileMode.SetUser) != 0) parts.Add("Set user ID");
            if ((mode & UnixFileMode.SetGroup) != 0) parts.Add(folder ? "Set group ID (new items inherit the group)" : "Set group ID");
            if ((mode & UnixFileMode.StickyBit) != 0) parts.Add(folder ? "Sticky (only owners can delete their items)" : "Sticky");
            return parts.Count == 0 ? null : string.Join(", ", parts);
        }

        /// <summary>"rwxr-xr-x (0755)", with setuid/setgid/sticky as ls shows them.</summary>
        internal static string FormatMode(UnixFileMode mode)
        {
            char Bit(UnixFileMode flag, char c) => (mode & flag) != 0 ? c : '-';
            char Exec(UnixFileMode exec, UnixFileMode special, char set, char setNoExec) =>
                (mode & special) != 0 ? ((mode & exec) != 0 ? set : setNoExec) : Bit(exec, 'x');

            string text = new string(new[]
            {
                Bit(UnixFileMode.UserRead, 'r'), Bit(UnixFileMode.UserWrite, 'w'), Exec(UnixFileMode.UserExecute, UnixFileMode.SetUser, 's', 'S'),
                Bit(UnixFileMode.GroupRead, 'r'), Bit(UnixFileMode.GroupWrite, 'w'), Exec(UnixFileMode.GroupExecute, UnixFileMode.SetGroup, 's', 'S'),
                Bit(UnixFileMode.OtherRead, 'r'), Bit(UnixFileMode.OtherWrite, 'w'), Exec(UnixFileMode.OtherExecute, UnixFileMode.StickyBit, 't', 'T')
            });
            return $"{text} ({Convert.ToString((int)mode & 0xFFF, 8).PadLeft(4, '0')})";
        }

        /// <summary>The mode for the Details tab (Unix); null when it cannot be read.</summary>
        private static string UnixPermissionsText(string path)
        {
            try
            {
                return OperatingSystem.IsWindows() ? null : FormatMode(File.GetUnixFileMode(path));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                return null;
            }
        }

        private Control BuildWindowsSecurity(string path, WindowsSecurityInfo security)
        {
            var list = new PropertyList();
            list.Row("Object name", Value(path, "ObjectNameValue"));
            if (security == null)
            {
                list.Row("Owner", Value("Unknown", "OwnerValue"));
                list.Separator();
                list.Row("Permissions", Value("You do not have permission to view this object's security, or it has none (for example on a FAT drive)."));
                return list.Grid;
            }

            list.Row("Owner", Value(security.Owner ?? "Unknown", "OwnerValue"));
            list.Separator();
            list.Header("Permissions");
            if (security.Rules.Count == 0)
            {
                list.Wide(Value("No permissions are set: nobody has access except the owner."));
                return list.Grid;
            }

            var table = new Grid { ColumnDefinitions = new ColumnDefinitions("1.4*,*,Auto"), Margin = new Thickness(0, 0, 0, 10) };
            table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            string[] headings = { "Group or user", "Permissions", "Type" };
            for (int c = 0; c < headings.Length; c++)
            {
                TextBlock heading = DialogChrome.Text(headings[c], 11, FontWeight.SemiBold, "TextSecondary");
                heading.Margin = new Thickness(0, 0, 10, 6);
                Grid.SetColumn(heading, c);
                table.Children.Add(heading);
            }

            int row = 1;
            foreach (AccessRuleInfo rule in security.Rules)
            {
                table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                SelectableTextBlock who = Value(rule.Identity);
                who.Margin = new Thickness(0, 0, 10, 8);
                SelectableTextBlock rights = Value(rule.Rights + (rule.Inherited ? " (inherited)" : string.Empty));
                rights.Margin = new Thickness(0, 0, 10, 8);
                TextBlock type = DialogChrome.Text(rule.Allow ? "Allow" : "Deny", 12, FontWeight.Normal, rule.Allow ? "TextPrimary" : "DangerBrush");
                type.Margin = new Thickness(0, 0, 0, 8);
                Grid.SetRow(who, row);
                Grid.SetRow(rights, row);
                Grid.SetRow(type, row);
                Grid.SetColumn(rights, 1);
                Grid.SetColumn(type, 2);
                table.Children.Add(who);
                table.Children.Add(rights);
                table.Children.Add(type);
                row++;
            }

            list.Wide(table);
            TextBlock note = DialogChrome.Text("To change permissions, use the Security tab of the Windows Properties dialog.", 11, FontWeight.Normal, "TextMuted");
            note.Margin = new Thickness(0, 4, 0, 0);
            list.Wide(note);
            return list.Grid;
        }
    }
}
