// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using ByteExplorer.Core;
using ByteExplorer.Core.Platform;
using ByteExplorer.Icons;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using Serilog;

namespace ByteExplorer.Views.Dialogs
{
    /// <summary>
    /// The app's own Properties window, the same on every OS (the WPF build opened the Windows shell dialog, which has no
    /// counterpart elsewhere), laid out like Explorer's: tabs, and OK / Cancel / Apply with Explorer's meaning (Apply is
    /// enabled only while something is changed; OK applies and closes; Cancel and Esc close without applying).
    /// <list type="bullet">
    /// <item><b>General</b>: icon and an editable name (renamed on Apply through the file list's rename, so it can be
    /// undone), type, default application, location with a copy button, size and size on disk, contents of folders, the
    /// three times, and the Read-only / Hidden attributes (editable; on Linux and macOS "hidden" is a leading dot, shown
    /// read-only). For several items: the counts, the common type and folder, the combined sizes, tri-state attributes.
    /// For a drive or mount point: type, file system, used / free space with a ring chart.</item>
    /// <item><b>Details</b>: Explorer's grouped property list from <see cref="FileMetadataReader"/> (image size, date
    /// taken, camera, media length and tags, archive contents) plus the file system facts.</item>
    /// <item><b>Security</b>: owner and permissions (Unix mode bits as a table, or the Windows ACL), read-only.</item>
    /// <item><b>Hashes</b>: MD5 / SHA-1 / SHA-256 of a file on request, with copy buttons and a compare box.</item>
    /// </list>
    /// Everything that touches the disk beyond a single stat runs off the UI thread and posts back through
    /// <see cref="UiThread.Post(Action)"/>; all of it stops when the window closes.
    /// </summary>
    public sealed partial class PropertiesWindow : Window
    {
        private enum ItemKind
        {
            File,
            Folder,
            Drive,
            Multiple,
            Missing
        }

        private const double LabelColumnWidth = 116;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly IReadOnlyList<string> _paths;
        private readonly ItemKind _kind;
        private string _path;
        private TabControl _tabs;
        private Button _okButton;
        private Button _applyButton;
        private TextBlock _statusText;
        private bool _closed;
        private bool _applying;

        public PropertiesWindow()
            : this(Array.Empty<string>())
        {
        }

        public PropertiesWindow(IReadOnlyList<string> paths)
        {
            _paths = paths ?? Array.Empty<string>();
            _path = _paths.Count == 1 ? _paths[0] : null;
            _kind = Classify(_paths);

            DialogChrome.Apply(this, TitleFor(_path, _paths.Count), 500);
            SizeToContent = SizeToContent.Manual;
            Height = 610;
            MinWidth = 420;
            MinHeight = 420;
            CanResize = true;
            ShowInTaskbar = true;
            Content = Build();

            Opened += (_, _) => StartBackgroundWork();
            Closed += (_, _) =>
            {
                _closed = true;
                _cts.Cancel();
                _cts.Dispose();
            };
            AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        }

        /// <summary>
        /// Renames an item for the name box: (full path, is folder, new name). Defaults to the owner window's active file
        /// list (validation, undo record, refresh); without one (an ownerless window, tests) the item is moved directly.
        /// </summary>
        internal Func<string, bool, string, Task> Renamer { get; set; }

        /// <summary>The size count; completes when the totals are final (or the window closed).</summary>
        internal Task SizeTask { get; private set; } = Task.CompletedTask;

        /// <summary>The slower General facts (default application, friendly type, drive space).</summary>
        internal Task FactsTask { get; private set; } = Task.CompletedTask;

        /// <summary>The Details tab's content; started the first time the tab is shown.</summary>
        internal Task DetailsTask { get; private set; }

        /// <summary>The last hash calculation.</summary>
        internal Task HashTask { get; private set; } = Task.CompletedTask;

        internal TabControl Tabs => _tabs;

        private CancellationToken Token => _cts.Token;

        private static string TitleFor(string single, int count) =>
            single != null ? PathUtil.GetName(single) + " Properties" : $"{count:N0} items Properties";

        private static ItemKind Classify(IReadOnlyList<string> paths)
        {
            if (paths.Count != 1)
            {
                return paths.Count == 0 ? ItemKind.Missing : ItemKind.Multiple;
            }

            string path = paths[0];
            if (IsDriveRoot(path))
            {
                return ItemKind.Drive;
            }

            if (Directory.Exists(path))
            {
                return ItemKind.Folder;
            }

            if (File.Exists(path))
            {
                return ItemKind.File;
            }

            try
            {
                return new FileInfo(path).LinkTarget != null ? ItemKind.File : ItemKind.Missing; // a broken link
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                return ItemKind.Missing;
            }
        }

        /// <summary>A drive letter's root on Windows; "/" or another mount point on Unix (the mount table is cached).</summary>
        internal static bool IsDriveRoot(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            try
            {
                if (OperatingSystem.IsWindows())
                {
                    string full = Path.GetFullPath(path);
                    string root = Path.GetPathRoot(full);
                    return !string.IsNullOrEmpty(root) && string.Equals(PathUtil.TrimTrailingSeparator(full), PathUtil.TrimTrailingSeparator(root), StringComparison.OrdinalIgnoreCase);
                }

                string normalized = PathUtil.TrimTrailingSeparator(PathUtil.Normalize(path));
                if (normalized.Length == 0 || normalized == "/")
                {
                    return true;
                }

                return Directory.Exists(normalized) && string.Equals(MountPoints.Of(normalized), normalized, StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is NotSupportedException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        // ------------------------------------------------------------------ layout

        private Control Build()
        {
            _tabs = new TabControl { Margin = new Thickness(12, 8, 12, 0), Padding = new Thickness(0) };
            AddTab("General", BuildGeneral());
            if (_kind == ItemKind.File || _kind == ItemKind.Folder)
            {
                AddTab("Details", BuildDetailsPlaceholder());
            }

            if (_kind == ItemKind.File || _kind == ItemKind.Folder || _kind == ItemKind.Drive)
            {
                AddTab("Security", BuildSecurity());
            }

            if (_kind == ItemKind.File)
            {
                AddTab("Hashes", BuildHashes());
            }

            _tabs.SelectionChanged += (_, _) =>
            {
                if (ReferenceEquals((_tabs.SelectedItem as TabItem)?.Tag, "Details"))
                {
                    EnsureDetailsLoaded();
                }
            };

            _okButton = DialogChrome.Button("OK", isDefault: true, isCancel: false, accent: true);
            _okButton.Name = "OkButton";
            _okButton.Click += async (_, _) => await OkAsync();
            Button cancel = DialogChrome.Button("Cancel", isDefault: false, isCancel: true, accent: false);
            cancel.Name = "CancelButton";
            cancel.Click += (_, _) => Close();
            _applyButton = DialogChrome.Button("Apply", isDefault: false, isCancel: false, accent: false);
            _applyButton.Name = "ApplyButton";
            _applyButton.IsEnabled = false;
            _applyButton.Click += async (_, _) => await ApplyAsync();

            _statusText = DialogChrome.Text(string.Empty, 12, FontWeight.Normal, "DangerBrush");
            _statusText.Name = "StatusText";
            _statusText.IsVisible = false;
            _statusText.Margin = new Thickness(0, 0, 0, 8);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(_okButton);
            buttons.Children.Add(cancel);
            buttons.Children.Add(_applyButton);

            var footer = new StackPanel { Margin = new Thickness(16, 12, 16, 16) };
            footer.Children.Add(_statusText);
            footer.Children.Add(buttons);

            var root = new DockPanel();
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);
            root.Children.Add(_tabs);
            return root;
        }

        private void AddTab(string header, Control content)
        {
            var scroller = new ScrollViewer
            {
                Content = content,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            };
            var frame = new Border
            {
                Child = scroller,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 4, 0, 0)
            };
            frame[!Border.BorderBrushProperty] = frame.GetResourceObservable("BorderLight").ToBinding();

            var item = new TabItem
            {
                Header = header,
                Tag = header,
                Name = header + "Tab",
                Content = frame,
                FontSize = 13,
                MinHeight = 34,
                Padding = new Thickness(12, 4)
            };
            item[!TabItem.ForegroundProperty] = item.GetResourceObservable("TextPrimary").ToBinding();
            _tabs.Items.Add(item);
        }

        /// <summary>A two-column property list (caption, value) with separators and group headers.</summary>
        private sealed class PropertyList
        {
            private int _row;

            public PropertyList()
            {
                Grid = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions($"{LabelColumnWidth.ToString(CultureInfo.InvariantCulture)},*"),
                    Margin = new Thickness(18, 14, 18, 14)
                };
            }

            public Grid Grid { get; }

            public int Count => _row;

            /// <summary>Adds a row; returns the caption so the caller can hide the row with the value.</summary>
            public TextBlock Row(string caption, Control value)
            {
                Grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                TextBlock label = DialogChrome.Text(caption, 12, FontWeight.Normal, "TextSecondary");
                label.Margin = new Thickness(0, 0, 12, 9);
                label.VerticalAlignment = VerticalAlignment.Top;
                Grid.SetRow(label, _row);
                Grid.SetRow(value, _row);
                Grid.SetColumn(value, 1);
                if (value.Margin == default)
                {
                    value.Margin = new Thickness(0, 0, 0, 9);
                }

                Grid.Children.Add(label);
                Grid.Children.Add(value);
                _row++;
                return label;
            }

            /// <summary>A control across both columns (a header, a chart, a table).</summary>
            public void Wide(Control control)
            {
                Grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                Grid.SetRow(control, _row);
                Grid.SetColumnSpan(control, 2);
                Grid.Children.Add(control);
                _row++;
            }

            public void Separator()
            {
                if (_row == 0)
                {
                    return;
                }

                var line = new Border { Height = 1, Margin = new Thickness(0, 3, 0, 12) };
                line[!Border.BackgroundProperty] = line.GetResourceObservable("BorderLight").ToBinding();
                Wide(line);
            }

            public void Header(string text)
            {
                TextBlock header = DialogChrome.Text(text, 12, FontWeight.SemiBold, "TextPrimary");
                header.Margin = new Thickness(0, _row == 0 ? 0 : 8, 0, 8);
                Wide(header);
            }
        }

        /// <summary>A selectable, wrapping value in the theme's text colour.</summary>
        private static SelectableTextBlock Value(string text, string name = null)
        {
            var block = new SelectableTextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Name = name };
            block[!TextBlock.ForegroundProperty] = block.GetResourceObservable("TextPrimary").ToBinding();
            block[!SelectableTextBlock.SelectionBrushProperty] = block.GetResourceObservable("SelectedBg").ToBinding();
            return block;
        }

        /// <summary>A small icon button that copies text (the location, a hash).</summary>
        private static Button CopyButton(Func<string> text, string tip)
        {
            var button = new Button
            {
                Classes = { "cmd" },
                Width = 26,
                Height = 24,
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Top,
                Content = new SymbolIcon { Symbol = Symbol.Copy, FontSize = 14 }
            };
            ((SymbolIcon)button.Content)[!SymbolIcon.ForegroundProperty] = button.GetResourceObservable("TextSecondary").ToBinding();
            ToolTip.SetTip(button, tip);
            button.Click += (_, _) =>
            {
                string value = text();
                if (!string.IsNullOrEmpty(value))
                {
                    ClipboardFileService.SetText(value);
                }
            };
            return button;
        }

        /// <summary>A value with a copy button to its right.</summary>
        private static Control WithCopy(SelectableTextBlock value, string tip)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 9) };
            value.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(value);
            Button copy = CopyButton(() => value.Text, tip);
            copy.Margin = new Thickness(6, -4, 0, -4);
            Grid.SetColumn(copy, 1);
            grid.Children.Add(copy);
            return grid;
        }

        // ------------------------------------------------------------------ keyboard

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Tab && (e.KeyModifiers & KeyModifiers.Control) != 0 && _tabs.ItemCount > 0)
            {
                int step = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? -1 : 1;
                _tabs.SelectedIndex = (_tabs.SelectedIndex + step + _tabs.ItemCount) % _tabs.ItemCount;
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                // Cancel is IsCancel, but a focused text box or the tab strip could keep Esc from reaching it.
                Close();
                e.Handled = true;
            }
        }

        // ------------------------------------------------------------------ background work

        private void StartBackgroundWork()
        {
            if (_kind == ItemKind.Drive)
            {
                FactsTask = LoadDriveAsync();
                return;
            }

            if (_kind != ItemKind.Missing)
            {
                SizeTask = CountAsync();
            }

            if (_kind == ItemKind.File || _kind == ItemKind.Folder)
            {
                FactsTask = LoadFileFactsAsync();
            }
        }

        /// <summary>Starts loading the Details tab if it has not been (tests call it without switching tabs).</summary>
        internal Task EnsureDetailsLoaded()
        {
            if (DetailsTask == null && (_kind == ItemKind.File || _kind == ItemKind.Folder))
            {
                DetailsTask = LoadDetailsAsync();
            }

            return DetailsTask ?? Task.CompletedTask;
        }

        // ------------------------------------------------------------------ OK / Apply

        private async Task OkAsync()
        {
            if (_applying)
            {
                return;
            }

            if (!HasChanges() || await ApplyAsync())
            {
                Close();
            }
        }

        private bool HasChanges() => IsNameChanged() || AttributesChanged();

        private void UpdateApplyEnabled()
        {
            if (_applyButton != null)
            {
                _applyButton.IsEnabled = !_applying && HasChanges();
            }
        }

        private void ShowStatus(string message)
        {
            _statusText.Text = message ?? string.Empty;
            _statusText.IsVisible = !string.IsNullOrEmpty(message);
        }

        /// <summary>
        /// Applies the changed attributes, then the new name (attributes first: they are set on the path as it is now).
        /// Returns false, with the reason shown above the buttons, when anything failed.
        /// </summary>
        internal async Task<bool> ApplyAsync()
        {
            if (_applying || !HasChanges())
            {
                return true;
            }

            _applying = true;
            UpdateApplyEnabled();
            ShowStatus(null);
            var problems = new List<string>();
            try
            {
                if (AttributesChanged())
                {
                    await ApplyAttributesAsync(problems);
                }

                if (problems.Count == 0 && IsNameChanged())
                {
                    string problem = await RenameAsync();
                    if (problem != null)
                    {
                        problems.Add(problem);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "Applying properties failed");
                problems.Add(ex.Message);
            }
            finally
            {
                _applying = false;
            }

            if (_closed)
            {
                return problems.Count == 0;
            }

            ShowStatus(problems.Count == 0 ? null : string.Join("\n", problems));
            UpdateApplyEnabled();
            return problems.Count == 0;
        }

        // ------------------------------------------------------------------ formatting

        internal static string FormatSize(long bytes)
        {
            string[] units = { "bytes", "KB", "MB", "GB", "TB", "PB" };
            double size = bytes;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }

            return unit == 0 ? $"{bytes.ToString("N0", CultureInfo.CurrentCulture)} bytes" : $"{size.ToString(size >= 100 ? "N0" : "N2", CultureInfo.CurrentCulture)} {units[unit]}";
        }

        /// <summary>Explorer's form: "1.50 MB (1,572,864 bytes)"; small sizes once: "12 bytes".</summary>
        internal static string FormatSizeWithBytes(long bytes) =>
            bytes < 1024 ? FormatSize(bytes) : $"{FormatSize(bytes)} ({bytes.ToString("N0", CultureInfo.CurrentCulture)} bytes)";

        private static string FormatTime(Func<DateTime> read)
        {
            try
            {
                DateTime value = read();
                return value.Year < 1971 ? "Unknown" : value.ToString("F", CultureInfo.CurrentCulture);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentOutOfRangeException || ex is PlatformNotSupportedException)
            {
                return "Unknown";
            }
        }

        private static string Plural(long count, string one, string many) =>
            count.ToString("N0", CultureInfo.CurrentCulture) + " " + (count == 1 ? one : many);

        /// <summary>The file list's type name ("PDF File"), with the extension as Explorer adds it.</summary>
        private static string FallbackTypeName(string path)
        {
            string extension = Path.GetExtension(path);
            return string.IsNullOrEmpty(extension) || extension == "." ? "File" : $"{extension.TrimStart('.').ToUpperInvariant()} File";
        }

        private static string DescribeType(string path, string friendly)
        {
            string extension = Path.GetExtension(path);
            string name = friendly ?? FallbackTypeName(path);
            return string.IsNullOrEmpty(extension) || extension == "." ? name : $"{name} ({extension})";
        }

        /// <summary>The owner window's active file list renames with undo; otherwise null (rename directly).</summary>
        private Func<string, bool, string, Task> ResolveRenamer()
        {
            if (Renamer != null)
            {
                return Renamer;
            }

            if (Owner?.DataContext is MainViewModel main && main.FileListViewModel is FileListViewModel list)
            {
                return list.RenamePathAsync;
            }

            return null;
        }
    }
}
