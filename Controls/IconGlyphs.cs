// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Avalonia.Media;
using ByteExplorer.Icons;

namespace ByteExplorer.Controls
{
    /// <summary>The icon font and per-symbol characters for owner-drawn rows (<see cref="FileItemView"/>).</summary>
    public static class IconGlyphs
    {
        /// <summary>The bundled icon font.</summary>
        public static Typeface Typeface => SymbolIcon.IconTypeface;

        /// <summary>The character(s) to draw for a symbol.</summary>
        public static string Get(Symbol symbol, IconVariant variant) => SymbolGlyphs.Get(symbol, variant);
    }
}
