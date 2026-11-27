// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using ByteExplorer.Icons;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>The bundled icon font loads and has a glyph for every symbol the app uses, in both variants.</summary>
    public sealed class IconGlyphsTests
    {
        [AvaloniaFact]
        public void EverySymbol_HasAGlyphInTheBundledFont()
        {
            Assert.True(FontManager.Current.TryGetGlyphTypeface(SymbolIcon.IconTypeface, out IGlyphTypeface font), "icon font not loaded");
            Assert.Equal("Seagull Fluent Icons", font.FamilyName);

            foreach (Symbol symbol in Enum.GetValues<Symbol>())
            {
                foreach (IconVariant variant in Enum.GetValues<IconVariant>())
                {
                    string glyph = SymbolGlyphs.Get(symbol, variant);
                    Assert.False(string.IsNullOrEmpty(glyph), $"{symbol}/{variant}: no codepoint");
                    Assert.True(font.TryGetGlyph((uint)char.ConvertToUtf32(glyph, 0), out ushort index) && index != 0, $"{symbol}/{variant}: not in the font");
                }
            }

            Assert.NotEqual(SymbolGlyphs.Get(Symbol.Folder, IconVariant.Filled), SymbolGlyphs.Get(Symbol.Folder, IconVariant.Regular));
        }
    }
}
