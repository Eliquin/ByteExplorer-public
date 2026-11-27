// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Generic;

namespace ByteExplorer.Infrastructure
{
    /// <summary>
    /// A (value, display name) pair for binding FileListViewPreset to a
    /// ComboBox via SelectedValuePath, without needing a converter.
    /// </summary>
    public sealed class FileListViewPresetOption
    {
        public FileListViewPresetOption(FileListViewPreset value, string displayName)
        {
            Value = value;
            DisplayName = displayName;
        }

        public FileListViewPreset Value { get; }
        public string DisplayName { get; }

        public static IReadOnlyList<FileListViewPresetOption> BuildAll()
        {
            var list = new List<FileListViewPresetOption>();
            foreach (FileListViewPreset preset in FileListViewPresets.All)
            {
                list.Add(new FileListViewPresetOption(preset, FileListViewPresets.DisplayName(preset)));
            }
            return list;
        }
    }
}
