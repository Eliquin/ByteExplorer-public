// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using ByteExplorer.Core.Models;

namespace ByteExplorer.Infrastructure
{
    /// <summary>
    /// The tab settings as the views read them (a menu deciding which item gets the "Middle-click" hint has no settings
    /// service at hand): the window's view model attaches its settings once.
    /// </summary>
    public static class TabOptions
    {
        private static readonly TabSettings Defaults = new TabSettings();
        private static Func<TabSettings> s_source;

        public static TabSettings Current => s_source?.Invoke() ?? Defaults;

        /// <summary>A middle click on a folder opens it in the other pane (otherwise in a new tab).</summary>
        public static bool MiddleClickOpensOtherPane => Current.MiddleClickOpens == TabSettings.MiddleClickOtherPane;

        public static void Attach(Func<TabSettings> source) => s_source = source;
    }
}
