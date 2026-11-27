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
using ByteExplorer.Core;
using ByteExplorer.Core.Platform;
using ByteExplorer.Infrastructure;
using Serilog;

namespace ByteExplorer.Views.Dialogs
{
    /// <summary>
    /// The Details tab: Explorer's grouped property list. The content facts come from <see cref="FileMetadataReader"/>
    /// (the details pane's reader: image size, EXIF date and camera, media length and tags, archive contents), sorted into
    /// Explorer's groups; the File group adds what the file system knows.
    /// </summary>
    public sealed partial class PropertiesWindow
    {
        /// <summary>Explorer's group for each of the reader's labels; anything new lands in "Other".</summary>
        private static readonly Dictionary<string, string> DetailGroups = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Title"] = "Description",
            ["Date taken"] = "Origin",
            ["Dimensions"] = "Image",
            ["Camera"] = "Camera",
            ["Length"] = "Media",
            ["Artist"] = "Media",
            ["Album"] = "Media",
            ["Resolution"] = "Video",
            ["Archive"] = "Archive"
        };

        private static readonly string[] GroupOrder = { "Description", "Origin", "Image", "Camera", "Media", "Video", "Archive", "Other" };

        /// <summary>The reader's rows the General tab already shows.</summary>
        private static readonly HashSet<string> GeneralRows = new HashSet<string>(StringComparer.Ordinal) { "Location", "Created", "Contains" };

        private ContentControl _detailsHost;

        private Control BuildDetailsPlaceholder()
        {
            _detailsHost = new ContentControl { Name = "DetailsHost", Content = Loading() };
            return _detailsHost;
        }

        private static Control Loading()
        {
            TextBlock text = DialogChrome.Text("Reading details…", 12, Avalonia.Media.FontWeight.Normal, "TextMuted");
            text.Margin = new Thickness(18, 16);
            return text;
        }

        private void ResetDetailsAfterRename()
        {
            if (_detailsHost != null && DetailsTask != null)
            {
                DetailsTask = null;
                _detailsHost.Content = Loading();
                if (ReferenceEquals((_tabs.SelectedItem as TabItem)?.Tag, "Details"))
                {
                    EnsureDetailsLoaded();
                }
            }
        }

        private async Task LoadDetailsAsync()
        {
            CancellationToken token = Token;
            string path = _path;
            bool isFolder = _kind == ItemKind.Folder;
            try
            {
                IReadOnlyList<FileDetailRow> content = isFolder
                    ? Array.Empty<FileDetailRow>()
                    : await FileMetadataReader.ReadAsync(path, false, token);
                List<(string Label, string Value)> fileFacts = await Task.Run(() => ReadFileFacts(path, isFolder), token);
                if (token.IsCancellationRequested || _closed || !string.Equals(path, _path, StringComparison.Ordinal))
                {
                    return;
                }

                var list = new PropertyList();
                var groups = new Dictionary<string, List<FileDetailRow>>(StringComparer.Ordinal);
                foreach (FileDetailRow row in content)
                {
                    if (GeneralRows.Contains(row.Label))
                    {
                        continue;
                    }

                    string group = DetailGroups.TryGetValue(row.Label, out string known) ? known : "Other";
                    if (!groups.TryGetValue(group, out List<FileDetailRow> rows))
                    {
                        groups[group] = rows = new List<FileDetailRow>();
                    }

                    rows.Add(row);
                }

                foreach (string group in GroupOrder)
                {
                    if (groups.TryGetValue(group, out List<FileDetailRow> rows))
                    {
                        list.Header(group);
                        foreach (FileDetailRow row in rows)
                        {
                            list.Row(row.Label, Value(row.Value, "Detail_" + row.Label.Replace(' ', '_')));
                        }
                    }
                }

                list.Header("File");
                foreach ((string label, string value) in fileFacts)
                {
                    list.Row(label, Value(value, "Detail_" + label.Replace(' ', '_')));
                }

                _detailsHost.Content = list.Grid;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "Details for the Properties window failed for {Path}", path);
                if (!_closed)
                {
                    TextBlock text = DialogChrome.Text("The details could not be read.", 12, Avalonia.Media.FontWeight.Normal, "TextMuted");
                    text.Margin = new Thickness(18, 16);
                    _detailsHost.Content = text;
                }
            }
        }

        /// <summary>The file system's facts, Explorer's File group plus the Unix ones (runs off the UI thread).</summary>
        private static List<(string, string)> ReadFileFacts(string path, bool isFolder)
        {
            var facts = new List<(string, string)>();
            FileSystemInfo info = isFolder ? new DirectoryInfo(path) : new FileInfo(path);
            facts.Add(("Name", PathUtil.GetName(path)));
            facts.Add(("Item type", isFolder ? "File folder" : FileSystemFacts.FriendlyTypeName(path) ?? FallbackTypeName(path)));
            string mime = OpenWithApplications.MimeTypeFor(path);
            if (!string.IsNullOrEmpty(mime))
            {
                facts.Add(("MIME type", mime));
            }

            string extension = Path.GetExtension(path);
            if (!isFolder && !string.IsNullOrEmpty(extension) && extension != ".")
            {
                facts.Add(("Extension", extension));
            }

            facts.Add(("Folder path", PathUtil.GetParent(path) ?? path));
            facts.Add(("Full path", path));
            if (!isFolder)
            {
                try
                {
                    var file = (FileInfo)info;
                    if (file.Exists)
                    {
                        facts.Add(("Size", FormatSizeWithBytes(file.Length)));
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                }
            }

            facts.Add(("Date created", FormatTime(() => info.CreationTime)));
            facts.Add(("Date modified", FormatTime(() => info.LastWriteTime)));
            facts.Add(("Date accessed", FormatTime(() => info.LastAccessTime)));

            string attributes = DescribeAttributes(path);
            if (attributes != null)
            {
                facts.Add(("Attributes", attributes));
            }

            string target = SafeLinkTarget(info);
            if (target != null)
            {
                facts.Add(("Link target", target));
                facts.Add(("Link status", File.Exists(path) || Directory.Exists(path) ? "Target exists" : "Broken (the target is missing)"));
            }

            if (OperatingSystem.IsWindows())
            {
                WindowsSecurityInfo security = FileSystemFacts.ReadWindowsSecurity(path, isFolder);
                if (!string.IsNullOrEmpty(security?.Owner))
                {
                    facts.Add(("Owner", security.Owner));
                }

                facts.Add(("Computer", Environment.MachineName));
            }
            else
            {
                if (FileSystemFacts.TryGetUnixStat(path, FileSystemFacts.CreateStatBuffer(), out UnixStat stat))
                {
                    facts.Add(("Owner", FileSystemFacts.UserName(stat.Uid)));
                    facts.Add(("Group", FileSystemFacts.GroupName(stat.Gid)));
                    if (stat.HardLinks > 0)
                    {
                        facts.Add(("Hard links", stat.HardLinks.ToString("N0", CultureInfo.CurrentCulture)));
                    }

                    if (stat.Inode != 0)
                    {
                        facts.Add(("Inode", stat.Inode.ToString(CultureInfo.InvariantCulture)));
                    }
                }

                string permissions = UnixPermissionsText(path);
                if (permissions != null)
                {
                    facts.Add(("Permissions", permissions));
                }

                facts.Add(("Computer", Environment.MachineName));
            }

            return facts;
        }

        /// <summary>The Windows attributes ("Read-only, Archive"); on Unix the read-only / hidden state.</summary>
        private static string DescribeAttributes(string path)
        {
            FileAttributes? read = SafeAttributes(path);
            if (!read.HasValue)
            {
                return null;
            }

            FileAttributes attributes = read.Value;
            var parts = new List<string>();
            if ((attributes & FileAttributes.ReadOnly) != 0) parts.Add("Read-only");
            if ((attributes & FileAttributes.Hidden) != 0) parts.Add("Hidden");
            if (OperatingSystem.IsWindows())
            {
                if ((attributes & FileAttributes.System) != 0) parts.Add("System");
                if ((attributes & FileAttributes.Archive) != 0) parts.Add("Archive");
                if ((attributes & FileAttributes.Compressed) != 0) parts.Add("Compressed");
                if ((attributes & FileAttributes.Encrypted) != 0) parts.Add("Encrypted");
                if ((attributes & FileAttributes.Offline) != 0) parts.Add("Offline");
                if ((attributes & FileAttributes.SparseFile) != 0) parts.Add("Sparse");
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0) parts.Add(OperatingSystem.IsWindows() ? "Reparse point" : "Symbolic link");
            return parts.Count == 0 ? "None" : string.Join(", ", parts);
        }
    }
}
