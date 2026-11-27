// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace ByteExplorer.Core.Models
{
    /// <summary>One column of the Details view as saved in the settings: which (its key) and how wide.</summary>
    public sealed class DetailsColumnEntry
    {
        /// <summary>"Name", "DateModified", "DateCreated", "Dimensions"… (Controls.DetailsColumnKind).</summary>
        public string Key { get; set; } = string.Empty;

        public double Width { get; set; }
    }
}
