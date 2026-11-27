// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace ByteExplorer.Views
{
    /// <summary>
    /// Marks a control with a selection of its own that the file keys act on while it has the keyboard: the navigation
    /// tree and Quick access. The window's key bindings (Delete, Shift+Delete, F2, Enter, Alt+Enter, Ctrl+C/X/V,
    /// Ctrl+Shift+C) step aside while the focus is inside one, so the key reaches it. Window key bindings run before the
    /// focused control sees the key: without this, Delete with a tree folder focused deleted the files selected in the
    /// list, which the person could not even see was the target.
    /// </summary>
    public interface IFileKeyScope
    {
    }
}
