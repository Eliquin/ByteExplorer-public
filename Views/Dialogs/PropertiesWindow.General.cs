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
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using ByteExplorer.Core;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Native;
using ByteExplorer.Core.Platform;
using ByteExplorer.Icons;
using ByteExplorer.Infrastructure;
using ByteExplorer.Infrastructure.Converters;
using Serilog;
using Path = System.IO.Path;

namespace ByteExplorer.Views.Dialogs
{
    /// <summary>The General tab: identity, sizes, times and attributes, or a drive's space.</summary>
    public sealed partial class PropertiesWindow
    {
        private const int UpdateIntervalMs = 150;

        /// <summary>Attributes SetFileAttributes does not take (they are set by other means, or not at all).</summary>
        private const FileAttributes UnsettableAttributes = FileAttributes.Directory | FileAttributes.Compressed | FileAttributes.Encrypted |
                                                            FileAttributes.ReparsePoint | FileAttributes.SparseFile | FileAttributes.Device;

        private SymbolIcon _icon;
        private TextBox _nameBox;
        private string _originalName;
        private bool _nameSelectedOnce;
        private SelectableTextBlock _typeText;
        private SelectableTextBlock _opensWithText;
        private TextBlock _opensWithCaption;
        private SelectableTextBlock _locationText;
        private SelectableTextBlock _sizeText;
        private SelectableTextBlock _sizeOnDiskText;
        private TextBlock _sizeOnDiskCaption;
        private SelectableTextBlock _containsText;
        private CheckBox _readOnlyBox;
        private CheckBox _hiddenBox;
        private TextBlock _otherAttributesText;
        private bool? _originalReadOnly;
        private bool? _originalHidden;
        private bool _hiddenEditable;

        // Drive
        private SelectableTextBlock _driveTypeText;
        private SelectableTextBlock _fileSystemText;
        private SelectableTextBlock _usedText;
        private SelectableTextBlock _freeText;
        private SelectableTextBlock _capacityText;
        private Arc _usedArc;
        private TextBlock _ringCaption;

        private Control BuildGeneral()
        {
            var list = new PropertyList();
            switch (_kind)
            {
                case ItemKind.Drive:
                    BuildDriveGeneral(list);
                    break;
                case ItemKind.Multiple:
                    BuildMultipleGeneral(list);
                    break;
                default:
                    BuildSingleGeneral(list);
                    break;
            }

            return list.Grid;
        }

        private Control Header(Symbol symbol, IBrush brush, string name, bool editable)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"{LabelColumnWidth.ToString(CultureInfo.InvariantCulture)},*"), Margin = new Thickness(0, 0, 0, 12) };
            _icon = new SymbolIcon
            {
                Symbol = symbol,
                IconVariant = IconVariant.Filled,
                FontSize = 40,
                Foreground = brush,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            grid.Children.Add(_icon);

            _nameBox = new TextBox
            {
                Name = "NameBox",
                Text = name,
                IsReadOnly = !editable,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 13
            };
            _originalName = name;
            if (editable)
            {
                // The property, not TextChanged: that event is raised later through the dispatcher.
                _nameBox.PropertyChanged += (_, e) =>
                {
                    if (e.Property == TextBox.TextProperty)
                    {
                        ShowStatus(null);
                        UpdateApplyEnabled();
                    }
                };
                _nameBox.GotFocus += (_, _) => SelectBaseNameOnce();
            }

            Grid.SetColumn(_nameBox, 1);
            grid.Children.Add(_nameBox);
            return grid;
        }

        /// <summary>As Explorer: the first time the name box gets focus, the name without its extension is selected.</summary>
        private void SelectBaseNameOnce()
        {
            if (_nameSelectedOnce || _nameBox.Text == null)
            {
                return;
            }

            _nameSelectedOnce = true;
            string text = _nameBox.Text;
            int dot = _kind == ItemKind.File ? text.LastIndexOf('.') : -1;
            _nameBox.SelectionStart = 0;
            _nameBox.SelectionEnd = dot > 0 ? dot : text.Length;
        }

        private void BuildSingleGeneral(PropertyList list)
        {
            string path = _path ?? string.Empty;
            bool isFolder = _kind == ItemKind.Folder;
            bool missing = _kind == ItemKind.Missing;
            list.Wide(Header(FileIconGlyphs.For(path, isFolder), HexToBrushConverter.BrushFor(FileIconGlyphs.ColorHexFor(path, isFolder)),
                PathUtil.GetName(path), editable: !missing && PathUtil.GetParent(path) != null));

            if (missing)
            {
                list.Row("Location", Value(PathUtil.GetParent(path) ?? path));
                list.Row("Status", Value("This item no longer exists, or cannot be read."));
                return;
            }

            FileSystemInfo info = isFolder ? new DirectoryInfo(path) : new FileInfo(path);
            _typeText = Value(isFolder ? "File folder" : DescribeType(path, null), "TypeValue");
            list.Row(isFolder ? "Type" : "Type of file", _typeText);
            if (!isFolder)
            {
                _opensWithText = Value(string.Empty, "OpensWithValue");
                _opensWithCaption = list.Row("Opens with", _opensWithText);
                _opensWithCaption.IsVisible = _opensWithText.IsVisible = false;
            }

            string target = SafeLinkTarget(info);
            if (target != null)
            {
                bool broken = !File.Exists(path) && !Directory.Exists(path);
                list.Row("Target", Value(broken ? target + "  (missing)" : target, "TargetValue"));
            }

            list.Separator();
            _locationText = Value(PathUtil.GetParent(path) ?? path, "LocationValue");
            list.Row("Location", WithCopy(_locationText, "Copy location"));
            _sizeText = Value("Calculating…", "SizeValue");
            list.Row("Size", _sizeText);
            _sizeOnDiskText = Value("Calculating…", "SizeOnDiskValue");
            _sizeOnDiskCaption = list.Row("Size on disk", _sizeOnDiskText);
            if (!FileSystemFacts.CanMeasureSizeOnDisk)
            {
                _sizeOnDiskCaption.IsVisible = _sizeOnDiskText.IsVisible = false;
            }

            if (isFolder)
            {
                _containsText = Value("Calculating…", "ContainsValue");
                list.Row("Contains", _containsText);
            }

            list.Separator();
            list.Row("Created", Value(FormatTime(() => info.CreationTime), "CreatedValue"));
            list.Row("Modified", Value(FormatTime(() => info.LastWriteTime), "ModifiedValue"));
            list.Row("Accessed", Value(FormatTime(() => info.LastAccessTime), "AccessedValue"));

            list.Separator();
            FileAttributes? attributes = SafeAttributes(path);
            bool hiddenNow = OperatingSystem.IsWindows()
                ? attributes.HasValue && (attributes.Value & FileAttributes.Hidden) != 0
                : PathUtil.GetName(path).StartsWith('.');
            BuildAttributes(list,
                attributes.HasValue ? (attributes.Value & FileAttributes.ReadOnly) != 0 : false,
                hiddenNow,
                canEditReadOnly: attributes.HasValue && (!isFolder || !OperatingSystem.IsWindows()),
                canEditHidden: attributes.HasValue && OperatingSystem.IsWindows());
            if (OperatingSystem.IsWindows() && attributes.HasValue)
            {
                SetOtherAttributes(attributes.Value);
            }
        }

        private void BuildMultipleGeneral(PropertyList list)
        {
            list.Wide(Header(Symbol.DocumentCopy, HexToBrushConverter.BrushFor("#8B8B8B"), $"{_paths.Count:N0} items", editable: false));
            _typeText = Value("Calculating…", "TypeValue");
            list.Row("Type", _typeText);
            string parent = CommonParent(_paths);
            _locationText = Value(parent != null ? "All in " + parent : "Various folders", "LocationValue");
            list.Row("Location", parent != null ? WithCopy(_locationText, "Copy location") : _locationText);
            list.Separator();
            _sizeText = Value("Calculating…", "SizeValue");
            list.Row("Size", _sizeText);
            _sizeOnDiskText = Value("Calculating…", "SizeOnDiskValue");
            _sizeOnDiskCaption = list.Row("Size on disk", _sizeOnDiskText);
            if (!FileSystemFacts.CanMeasureSizeOnDisk)
            {
                _sizeOnDiskCaption.IsVisible = _sizeOnDiskText.IsVisible = false;
            }

            _containsText = Value("Calculating…", "ContainsValue");
            list.Row("Contains", _containsText);
            list.Separator();

            // Enabled once the selection's attributes are read (CountAsync's first pass).
            BuildAttributes(list, false, false, canEditReadOnly: false, canEditHidden: false);
        }

        private void BuildAttributes(PropertyList list, bool readOnly, bool hidden, bool canEditReadOnly, bool canEditHidden)
        {
            _readOnlyBox = new CheckBox { Name = "ReadOnlyBox", Content = "Read-only", IsChecked = readOnly, IsEnabled = canEditReadOnly, FontSize = 12, MinHeight = 24 };
            _hiddenBox = new CheckBox { Name = "HiddenBox", Content = "Hidden", IsChecked = hidden, IsEnabled = canEditHidden, FontSize = 12, MinHeight = 24 };
            _originalReadOnly = readOnly;
            _originalHidden = hidden;
            _hiddenEditable = canEditHidden;
            foreach (CheckBox box in new[] { _readOnlyBox, _hiddenBox })
            {
                box[!CheckBox.ForegroundProperty] = box.GetResourceObservable("TextPrimary").ToBinding();
                box.IsCheckedChanged += (sender, _) =>
                {
                    var changed = (CheckBox)sender;
                    if (changed.IsThreeState && changed.IsChecked.HasValue)
                    {
                        changed.IsThreeState = false; // a mixed selection becomes all-or-nothing once clicked, as in Explorer
                    }

                    ShowStatus(null);
                    UpdateApplyEnabled();
                };
            }

            if (_kind == ItemKind.Folder && OperatingSystem.IsWindows())
            {
                ToolTip.SetTip(_readOnlyBox, "Windows ignores the read-only attribute on folders.");
            }

            if (!OperatingSystem.IsWindows())
            {
                ToolTip.SetTip(_hiddenBox, "Names that start with a dot are hidden on this system. Rename the item to change it.");
            }

            var boxes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
            boxes.Children.Add(_readOnlyBox);
            boxes.Children.Add(_hiddenBox);
            _otherAttributesText = DialogChrome.Text(string.Empty, 11, FontWeight.Normal, "TextMuted");
            _otherAttributesText.Name = "OtherAttributesValue";
            _otherAttributesText.IsVisible = false;
            var stack = new StackPanel { Spacing = 2, Margin = new Thickness(0, -3, 0, 6) };
            stack.Children.Add(boxes);
            stack.Children.Add(_otherAttributesText);
            TextBlock caption = list.Row("Attributes", stack);
            caption.Margin = new Thickness(0, 1, 12, 9);
        }

        /// <summary>Windows: the attributes the dialog cannot change, listed under the check boxes.</summary>
        private void SetOtherAttributes(FileAttributes attributes)
        {
            var parts = new List<string>();
            if ((attributes & FileAttributes.Archive) != 0) parts.Add("Archive");
            if ((attributes & FileAttributes.System) != 0) parts.Add("System");
            if ((attributes & FileAttributes.Compressed) != 0) parts.Add("Compressed");
            if ((attributes & FileAttributes.Encrypted) != 0) parts.Add("Encrypted");
            if ((attributes & FileAttributes.Offline) != 0) parts.Add("Offline");
            if ((attributes & FileAttributes.SparseFile) != 0) parts.Add("Sparse");
            if ((attributes & FileAttributes.NotContentIndexed) != 0) parts.Add("Not indexed");
            _otherAttributesText.Text = parts.Count == 0 ? string.Empty : "Also: " + string.Join(", ", parts);
            _otherAttributesText.IsVisible = parts.Count > 0;
        }

        private void BuildDriveGeneral(PropertyList list)
        {
            string root = _path;
            list.Wide(Header(Symbol.HardDrive, HexToBrushConverter.BrushFor("#6B7B8C"), DriveTitle(root, null, DriveType.Unknown), editable: false));
            _driveTypeText = Value("Reading…", "DriveTypeValue");
            list.Row("Type", _driveTypeText);
            _fileSystemText = Value("Reading…", "FileSystemValue");
            list.Row("File system", _fileSystemText);
            if (!OperatingSystem.IsWindows())
            {
                _locationText = Value(root, "LocationValue");
                list.Row("Mount point", WithCopy(_locationText, "Copy mount point"));
            }

            list.Separator();
            _usedText = Value("Reading…", "UsedValue");
            list.Row("Used space", Legend(_usedText, "SelectedAccent"));
            _freeText = Value("Reading…", "FreeValue");
            list.Row("Free space", Legend(_freeText, "BorderLight"));
            list.Separator();
            _capacityText = Value("Reading…", "CapacityValue");
            list.Row("Capacity", _capacityText);

            const double ring = 132;
            const double thickness = 22;
            var track = new Ellipse { Width = ring, Height = ring, StrokeThickness = thickness };
            track[!Shape.StrokeProperty] = track.GetResourceObservable("BorderLight").ToBinding();
            _usedArc = new Arc { Width = ring, Height = ring, StrokeThickness = thickness, StartAngle = -90, SweepAngle = 0, Name = "UsedArc" };
            _usedArc[!Shape.StrokeProperty] = _usedArc.GetResourceObservable("SelectedAccent").ToBinding();
            var chart = new Grid { Width = ring, Height = ring, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 8) };
            chart.Children.Add(track);
            chart.Children.Add(_usedArc);
            list.Wide(chart);
            _ringCaption = DialogChrome.Text(DriveCaption(root), 12, FontWeight.SemiBold, "TextPrimary");
            _ringCaption.HorizontalAlignment = HorizontalAlignment.Center;
            list.Wide(_ringCaption);
        }

        /// <summary>A value with a coloured square in front, as Explorer's used / free legend.</summary>
        private static Control Legend(SelectableTextBlock value, string brushKey)
        {
            var swatch = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 2, 8, 0), VerticalAlignment = VerticalAlignment.Top };
            swatch[!Border.BackgroundProperty] = swatch.GetResourceObservable(brushKey).ToBinding();
            var panel = new DockPanel { Margin = new Thickness(0, 0, 0, 9) };
            DockPanel.SetDock(swatch, Dock.Left);
            panel.Children.Add(swatch);
            panel.Children.Add(value);
            return panel;
        }

        private static string DriveCaption(string root) =>
            OperatingSystem.IsWindows() ? "Drive " + PathUtil.TrimTrailingSeparator(root) : root;

        private static string DriveTitle(string root, string label, DriveType type)
        {
            if (OperatingSystem.IsWindows())
            {
                string letter = PathUtil.TrimTrailingSeparator(root);
                string name = string.IsNullOrWhiteSpace(label) ? (type == DriveType.Fixed ? "Local Disk" : type == DriveType.Unknown ? "Drive" : DriveTypeName(type)) : label;
                return $"{name} ({letter})";
            }

            return root == "/" ? (OperatingSystem.IsMacOS() ? "Macintosh HD" : "File System") : PathUtil.GetName(PathUtil.TrimTrailingSeparator(root));
        }

        private static string DriveTypeName(DriveType type) => type switch
        {
            DriveType.Fixed => "Local Disk",
            DriveType.Removable => "Removable Disk",
            DriveType.Network => "Network Drive",
            DriveType.CDRom => "CD Drive",
            DriveType.Ram => "RAM Disk",
            DriveType.NoRootDirectory => "Not mounted",
            _ => "Disk"
        };

        // ------------------------------------------------------------------ background: drive

        private async Task LoadDriveAsync()
        {
            CancellationToken token = Token;
            string root = _path;
            try
            {
                var facts = await Task.Run(() =>
                {
                    var drive = new DriveInfo(root);
                    DriveType type = drive.DriveType;
                    if (!drive.IsReady)
                    {
                        return (Ready: false, Type: type, Format: (string)null, Label: (string)null, Total: 0L, Free: 0L, Available: 0L);
                    }

                    string label = OperatingSystem.IsWindows() ? drive.VolumeLabel : null;
                    return (Ready: true, Type: type, Format: drive.DriveFormat, Label: label, Total: drive.TotalSize, Free: drive.TotalFreeSpace, Available: drive.AvailableFreeSpace);
                }, token);

                if (token.IsCancellationRequested || _closed)
                {
                    return;
                }

                _nameBox.Text = DriveTitle(root, facts.Label, facts.Type);
                _driveTypeText.Text = DriveTypeName(facts.Type);
                if (!facts.Ready)
                {
                    _fileSystemText.Text = _usedText.Text = _freeText.Text = _capacityText.Text = "Not available (the drive is not ready)";
                    return;
                }

                _fileSystemText.Text = string.IsNullOrEmpty(facts.Format) ? "Unknown" : facts.Format;
                long used = Math.Max(0, facts.Total - facts.Free);
                _usedText.Text = FormatSizeWithBytes(used);
                _freeText.Text = FormatSizeWithBytes(facts.Free) + (facts.Available < facts.Free ? $" ({FormatSize(facts.Available)} available to you)" : string.Empty);
                _capacityText.Text = FormatSizeWithBytes(facts.Total);
                double fraction = facts.Total > 0 ? (double)used / facts.Total : 0;
                _usedArc.SweepAngle = Math.Clamp(fraction, 0, 1) * 360;
                _ringCaption.Text = $"{DriveCaption(root)}  ·  {(fraction * 100).ToString("0", CultureInfo.CurrentCulture)}% used";
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                Log.Logger.Debug(ex, "Drive facts unavailable for {Root}", root);
                if (!_closed)
                {
                    _driveTypeText.Text = _fileSystemText.Text = _usedText.Text = _freeText.Text = _capacityText.Text = "Unknown";
                }
            }
        }

        // ------------------------------------------------------------------ background: type and default app

        private async Task LoadFileFactsAsync()
        {
            CancellationToken token = Token;
            string path = _path;
            bool isFolder = _kind == ItemKind.Folder;
            try
            {
                (string friendly, string app) = await Task.Run(() =>
                    isFolder ? (null, null) : (FileSystemFacts.FriendlyTypeName(path), FileSystemFacts.DefaultApplicationName(path)), token);
                if (token.IsCancellationRequested || _closed || !string.Equals(path, _path, StringComparison.Ordinal))
                {
                    return;
                }

                if (friendly != null)
                {
                    _typeText.Text = DescribeType(path, friendly);
                }

                if (_opensWithText != null && !string.IsNullOrWhiteSpace(app))
                {
                    _opensWithText.Text = app;
                    _opensWithText.IsVisible = _opensWithCaption.IsVisible = true;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Logger.Debug(ex, "File facts unavailable for {Path}", path);
            }
        }

        // ------------------------------------------------------------------ background: sizes

        /// <summary>
        /// Totals every item: files count directly, folders are walked (iteratively, with an explicit stack) with the same
        /// enumerator as the file list, never following symbolic links (no cycles, no counting a target twice), skipping
        /// folders that cannot be read. Live-updated at most every 150 ms; cancelled when the window closes. Size on disk is
        /// each file's allocation (Windows: rounded up to the cluster, which ignores NTFS compression inside folders for
        /// speed; Linux: statx blocks); for a multi-selection the first pass also reads what the header, type and attribute
        /// boxes need.
        /// </summary>
        private async Task CountAsync()
        {
            CancellationToken token = Token;
            IReadOnlyList<string> paths = _kind == ItemKind.Multiple ? _paths : new[] { _path };
            bool multiple = _kind == ItemKind.Multiple;
            long bytes = 0, onDisk = 0, files = 0, folders = 0, skipped = 0;
            bool onDiskKnown = FileSystemFacts.CanMeasureSizeOnDisk;
            long lastUpdate = 0;

            void Publish(bool final)
            {
                long now = Environment.TickCount64;
                if (!final && now - lastUpdate < UpdateIntervalMs)
                {
                    return;
                }

                lastUpdate = now;
                long b = bytes, d = onDisk, f = files, n = folders, s = skipped;
                bool known = onDiskKnown;
                UiThread.Post(() =>
                {
                    if (token.IsCancellationRequested || _closed)
                    {
                        return;
                    }

                    string more = final ? string.Empty : "…";
                    _sizeText.Text = FormatSizeWithBytes(b) + more;
                    if (_sizeOnDiskText != null)
                    {
                        _sizeOnDiskText.Text = FormatSizeWithBytes(d) + more;
                        _sizeOnDiskText.IsVisible = _sizeOnDiskCaption.IsVisible = known;
                    }

                    if (_containsText != null)
                    {
                        _containsText.Text = $"{Plural(f, "File", "Files")}, {Plural(n, "Folder", "Folders")}" +
                                             (s > 0 ? $" ({s:N0} could not be read)" : string.Empty) + more;
                    }
                });
            }

            try
            {
                await Task.Run(() =>
                {
                    long cluster = OperatingSystem.IsWindows() && paths.Count > 0 ? FileSystemFacts.ClusterSize(paths[0]) : 0;
                    if (OperatingSystem.IsWindows() && cluster <= 0)
                    {
                        onDiskKnown = false;
                    }

                    byte[] statBuffer = FileSystemFacts.CreateStatBuffer();
                    var selection = new SelectionSummary();
                    var pending = new Stack<string>();
                    foreach (string path in paths)
                    {
                        token.ThrowIfCancellationRequested();
                        FileAttributes attributes;
                        try
                        {
                            attributes = File.GetAttributes(path);
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
                        {
                            skipped++;
                            continue;
                        }

                        bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                        if (multiple)
                        {
                            selection.Add(path, isDirectory, attributes);
                        }

                        if (isDirectory)
                        {
                            if (multiple)
                            {
                                folders++;
                            }

                            if ((attributes & FileAttributes.ReparsePoint) == 0)
                            {
                                pending.Push(path);
                            }

                            continue;
                        }

                        files++;
                        try
                        {
                            long length = (attributes & FileAttributes.ReparsePoint) != 0 && !File.Exists(path) ? 0 : new FileInfo(path).Length;
                            bytes += length;
                            long? allocated = onDiskKnown ? FileSystemFacts.FileSizeOnDisk(path, length, cluster, statBuffer) : null;
                            if (allocated.HasValue)
                            {
                                onDisk += allocated.Value;
                            }
                            else if (onDiskKnown && !FileSystemFacts.CanMeasureSizeOnDisk)
                            {
                                onDiskKnown = false;
                            }
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                        {
                            skipped++;
                        }
                    }

                    if (multiple)
                    {
                        PostSelection(selection, token);
                    }

                    bool statEach = onDiskKnown && !OperatingSystem.IsWindows();
                    while (pending.Count > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        string folder = pending.Pop();
                        try
                        {
                            foreach (DirectoryIndexEntry entry in NativeFileEnumerator.Enumerate(folder, token))
                            {
                                if (entry.IsDirectory)
                                {
                                    folders++;
                                    if ((entry.Attributes & FileAttributes.ReparsePoint) == 0)
                                    {
                                        pending.Push(Path.Combine(folder, entry.Name));
                                    }
                                }
                                else
                                {
                                    files++;
                                    bytes += entry.SizeBytes;
                                    if (!onDiskKnown)
                                    {
                                        continue;
                                    }

                                    if (!statEach)
                                    {
                                        onDisk += FileSystemFacts.RoundUpToCluster(entry.SizeBytes, cluster);
                                    }
                                    else if (FileSystemFacts.TryGetUnixStat(Path.Combine(folder, entry.Name), statBuffer, out UnixStat stat))
                                    {
                                        onDisk += stat.AllocatedBytes;
                                    }
                                }
                            }
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                        {
                            skipped++;
                        }

                        Publish(final: false);
                    }
                }, token);

                Publish(final: true);
            }
            catch (OperationCanceledException)
            {
                // Window closed.
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "Counting sizes for the Properties window failed");
            }
        }

        /// <summary>What a multi-selection is made of: counts, the common type, and the attributes' common states.</summary>
        private sealed class SelectionSummary
        {
            public long Files;
            public long Folders;
            public string CommonExtension;
            public bool MixedTypes;
            public int ReadOnlyCount;
            public int HiddenCount;
            public int ReadOnlyCandidates;

            public void Add(string path, bool isDirectory, FileAttributes attributes)
            {
                string kind = isDirectory ? "\0folder" : Path.GetExtension(path).ToLowerInvariant();
                if (Files + Folders == 0)
                {
                    CommonExtension = kind;
                }
                else if (!string.Equals(CommonExtension, kind, StringComparison.Ordinal))
                {
                    MixedTypes = true;
                }

                if (isDirectory)
                {
                    Folders++;
                }
                else
                {
                    Files++;
                }

                // Explorer's read-only box speaks for the files of a selection (folders ignore it on Windows).
                if (!isDirectory || !OperatingSystem.IsWindows())
                {
                    ReadOnlyCandidates++;
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                    {
                        ReadOnlyCount++;
                    }
                }

                bool hidden = OperatingSystem.IsWindows() ? (attributes & FileAttributes.Hidden) != 0 : PathUtil.GetName(path).StartsWith('.');
                if (hidden)
                {
                    HiddenCount++;
                }
            }
        }

        private void PostSelection(SelectionSummary selection, CancellationToken token)
        {
            UiThread.Post(() =>
            {
                if (token.IsCancellationRequested || _closed)
                {
                    return;
                }

                long total = selection.Files + selection.Folders;
                _nameBox.Text = selection.Folders == 0 ? Plural(selection.Files, "File", "Files")
                    : selection.Files == 0 ? Plural(selection.Folders, "Folder", "Folders")
                    : $"{Plural(selection.Files, "File", "Files")}, {Plural(selection.Folders, "Folder", "Folders")}";
                _typeText.Text = total == 0 ? "Unknown"
                    : selection.MixedTypes ? "Multiple types"
                    : selection.CommonExtension == "\0folder" ? "All of type File folder"
                    : "All of type " + FallbackTypeName("x" + selection.CommonExtension);

                bool? readOnly = selection.ReadOnlyCandidates == 0 ? false
                    : selection.ReadOnlyCount == 0 ? false
                    : selection.ReadOnlyCount == selection.ReadOnlyCandidates ? true : null;
                bool? hidden = total == 0 ? false : selection.HiddenCount == 0 ? false : selection.HiddenCount == total ? true : null;
                SetAttributeBoxes(readOnly, hidden, canEditReadOnly: selection.ReadOnlyCandidates > 0, canEditHidden: OperatingSystem.IsWindows() && total > 0);
            });
        }

        /// <summary>Sets the boxes and their "unchanged" baseline; null shows the mixed state of a selection.</summary>
        private void SetAttributeBoxes(bool? readOnly, bool? hidden, bool canEditReadOnly, bool canEditHidden)
        {
            _originalReadOnly = readOnly;
            _originalHidden = hidden;
            _hiddenEditable = canEditHidden;
            _readOnlyBox.IsThreeState = !readOnly.HasValue;
            _readOnlyBox.IsChecked = readOnly;
            _readOnlyBox.IsEnabled = canEditReadOnly;
            _hiddenBox.IsThreeState = !hidden.HasValue;
            _hiddenBox.IsChecked = hidden;
            _hiddenBox.IsEnabled = canEditHidden;
            UpdateApplyEnabled();
        }

        // ------------------------------------------------------------------ apply: attributes and name

        private bool IsNameChanged() =>
            _nameBox != null && !_nameBox.IsReadOnly && _originalName != null &&
            !string.Equals((_nameBox.Text ?? string.Empty).Trim(), _originalName, StringComparison.Ordinal);

        private bool AttributesChanged() =>
            _readOnlyBox != null &&
            ((_readOnlyBox.IsEnabled && _readOnlyBox.IsChecked.HasValue && _readOnlyBox.IsChecked != _originalReadOnly) ||
             (_hiddenEditable && _hiddenBox.IsChecked.HasValue && _hiddenBox.IsChecked != _originalHidden));

        private async Task ApplyAttributesAsync(List<string> problems)
        {
            bool? readOnly = _readOnlyBox.IsEnabled && _readOnlyBox.IsChecked != _originalReadOnly ? _readOnlyBox.IsChecked : null;
            bool? hidden = _hiddenEditable && _hiddenBox.IsChecked != _originalHidden ? _hiddenBox.IsChecked : null;
            IReadOnlyList<string> targets = _kind == ItemKind.Multiple ? _paths : new[] { _path };
            bool windows = OperatingSystem.IsWindows();

            List<string> failures = await Task.Run(() =>
            {
                var failed = new List<string>();
                foreach (string path in targets)
                {
                    try
                    {
                        FileAttributes current = File.GetAttributes(path);
                        bool isDirectory = (current & FileAttributes.Directory) != 0;
                        FileAttributes wanted = current;
                        if (readOnly.HasValue && (!isDirectory || !windows))
                        {
                            wanted = readOnly.Value ? wanted | FileAttributes.ReadOnly : wanted & ~FileAttributes.ReadOnly;
                        }

                        if (hidden.HasValue && windows)
                        {
                            wanted = hidden.Value ? wanted | FileAttributes.Hidden : wanted & ~FileAttributes.Hidden;
                        }

                        if (wanted != current)
                        {
                            wanted &= ~UnsettableAttributes;
                            File.SetAttributes(path, wanted == 0 ? FileAttributes.Normal : wanted);
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
                    {
                        failed.Add($"Couldn't change the attributes of \"{PathUtil.GetName(path)}\": {ex.Message}");
                    }
                }

                return failed;
            });

            if (failures.Count > 0)
            {
                problems.Add(failures.Count == 1 ? failures[0] : $"{failures[0]}\n(and {failures.Count - 1:N0} more)");
            }

            // The baseline is what the items have now (on a failure, partly the old state).
            if (_kind == ItemKind.Multiple)
            {
                var selection = await Task.Run(() =>
                {
                    var summary = new SelectionSummary();
                    foreach (string path in targets)
                    {
                        FileAttributes? attributes = SafeAttributes(path);
                        if (attributes.HasValue)
                        {
                            summary.Add(path, (attributes.Value & FileAttributes.Directory) != 0, attributes.Value);
                        }
                    }

                    return summary;
                });
                long total = selection.Files + selection.Folders;
                SetAttributeBoxes(
                    selection.ReadOnlyCount == 0 ? false : selection.ReadOnlyCount == selection.ReadOnlyCandidates ? true : null,
                    selection.HiddenCount == 0 ? false : selection.HiddenCount == total ? true : null,
                    _readOnlyBox.IsEnabled, _hiddenEditable);
            }
            else
            {
                FileAttributes? now = SafeAttributes(_path);
                if (now.HasValue)
                {
                    _originalReadOnly = (now.Value & FileAttributes.ReadOnly) != 0;
                    _readOnlyBox.IsChecked = _originalReadOnly;
                    if (windows)
                    {
                        _originalHidden = (now.Value & FileAttributes.Hidden) != 0;
                        _hiddenBox.IsChecked = _originalHidden;
                        SetOtherAttributes(now.Value);
                    }
                }
            }
        }

        /// <summary>Renames the item to the name box's text; returns the reason when it did not happen.</summary>
        private async Task<string> RenameAsync()
        {
            string newName = (_nameBox.Text ?? string.Empty).Trim();
            string problem = FileNameRules.Validate(newName);
            if (problem != null)
            {
                FocusName();
                return problem;
            }

            string oldPath = PathUtil.TrimTrailingSeparator(_path);
            string parent = Path.GetDirectoryName(oldPath);
            if (string.IsNullOrEmpty(parent))
            {
                return "This item cannot be renamed.";
            }

            string destination = Path.Combine(parent, newName);
            bool isFolder = _kind == ItemKind.Folder;
            bool caseOnly = string.Equals(newName, _originalName, StringComparison.OrdinalIgnoreCase);
            if (!caseOnly && Exists(destination))
            {
                FocusName();
                return "A file or folder with that name already exists.";
            }

            try
            {
                Func<string, bool, string, Task> renamer = ResolveRenamer();
                if (renamer != null)
                {
                    await renamer(oldPath, isFolder, newName);
                }
                else
                {
                    await Task.Run(() =>
                    {
                        if (isFolder)
                        {
                            Directory.Move(oldPath, destination);
                        }
                        else
                        {
                            File.Move(oldPath, destination, overwrite: false);
                        }
                    });
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return $"Couldn't rename \"{_originalName}\": {ex.Message}";
            }

            bool renamed = Exists(destination) && (caseOnly || !Exists(oldPath));
            if (!renamed)
            {
                return $"Couldn't rename \"{_originalName}\".";
            }

            _path = destination;
            _originalName = newName;
            _nameBox.Text = newName;
            Title = TitleFor(_path, 1);
            if (!isFolder)
            {
                _typeText.Text = DescribeType(_path, null);
                _icon.Symbol = FileIconGlyphs.For(_path, false);
                _icon.Foreground = HexToBrushConverter.BrushFor(FileIconGlyphs.ColorHexFor(_path, false));
                FactsTask = LoadFileFactsAsync();
            }

            ResetDetailsAfterRename();
            return null;
        }

        private void FocusName()
        {
            if (_tabs.ItemCount > 0)
            {
                _tabs.SelectedIndex = 0;
            }

            _nameBox.Focus();
        }

        private static bool Exists(string path)
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                return true;
            }

            try
            {
                return new FileInfo(path).LinkTarget != null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                return false;
            }
        }

        // ------------------------------------------------------------------ helpers

        private static FileAttributes? SafeAttributes(string path)
        {
            try
            {
                return File.GetAttributes(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                return null;
            }
        }

        private static string SafeLinkTarget(FileSystemInfo info)
        {
            try
            {
                return info.LinkTarget;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static string CommonParent(IReadOnlyList<string> paths)
        {
            string parent = PathUtil.GetParent(paths[0]);
            for (int i = 1; i < paths.Count; i++)
            {
                if (!PathUtil.AreSame(PathUtil.GetParent(paths[i]), parent))
                {
                    return null;
                }
            }

            return parent;
        }
    }
}
