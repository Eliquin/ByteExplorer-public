// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Windows.Input;
using Avalonia.Media;
using ByteExplorer.Core.Models;
using ByteExplorer.Icons;
using ByteExplorer.Infrastructure;

namespace ByteExplorer.ViewModels.Settings
{
    /// <summary>
    /// A color, or the theme's (empty): a row of swatches, a hex box ("#RRGGBB" or "#AARRGGBB") and "Default". Typing
    /// applies the color as soon as it is a valid one; a half-typed value is kept in the box without changing anything.
    /// </summary>
    public sealed class ColorSetting : SettingItem
    {
        private readonly Func<string> _get;
        private readonly Action<string> _set;
        private string _editText;

        public ColorSetting(string title, string description, Symbol icon, Func<string> get, Action<string> set)
            : base(title, description, icon)
        {
            _get = get;
            _set = set;
            _editText = get() ?? string.Empty;
            PickCommand = new RelayCommand(p => Hex = p as string);
            ClearCommand = new RelayCommand(_ => Hex = string.Empty, _ => !IsDefault);
        }

        /// <summary>The swatches offered: neutrals for backgrounds, then the theme accents and a few strong colors.</summary>
        public static IReadOnlyList<ColorSwatch> Swatches { get; } = new[]
        {
            new ColorSwatch("#FFFFFF", "White"),
            new ColorSwatch("#F3F3F3", "Light gray"),
            new ColorSwatch("#D6D6D6", "Silver"),
            new ColorSwatch("#8A8A8A", "Gray"),
            new ColorSwatch("#3A3A3A", "Charcoal"),
            new ColorSwatch("#202020", "Dark gray"),
            new ColorSwatch("#000000", "Black"),
            new ColorSwatch("#0078D4", "Blue"),
            new ColorSwatch("#1E3A5F", "Navy"),
            new ColorSwatch("#00B7C3", "Teal"),
            new ColorSwatch("#107C10", "Green"),
            new ColorSwatch("#FFB900", "Yellow"),
            new ColorSwatch("#CA5010", "Orange"),
            new ColorSwatch("#E81123", "Red"),
            new ColorSwatch("#E3008C", "Pink"),
            new ColorSwatch("#8764B8", "Purple"),
        };

        /// <summary>The saved color ("#RRGGBB"/"#AARRGGBB"), empty for the theme's.</summary>
        public string Hex
        {
            get => _get() ?? string.Empty;
            set
            {
                string normalized = AreaAppearance.NormalizeColor(value);
                _editText = normalized;
                RaisePropertyChanged(nameof(EditText));
                if (string.Equals(normalized, Hex, StringComparison.Ordinal))
                {
                    return;
                }

                _set(normalized);
                RaiseValueChanged();
                OnChanged();
            }
        }

        /// <summary>The hex box: a valid color applies at once, an empty box means the theme's color.</summary>
        public string EditText
        {
            get => _editText;
            set
            {
                _editText = value ?? string.Empty;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(IsEditTextValid));
                if (_editText.Trim().Length == 0)
                {
                    Hex = string.Empty;
                }
                else if (AreaAppearance.NormalizeColor(_editText).Length > 0)
                {
                    string normalized = AreaAppearance.NormalizeColor(_editText);
                    if (!string.Equals(normalized, Hex, StringComparison.Ordinal))
                    {
                        _set(normalized);
                        RaiseValueChanged();
                        OnChanged();
                    }
                }
            }
        }

        /// <summary>The box holds a color or nothing (false while half typed: the box shows it).</summary>
        public bool IsEditTextValid => _editText.Trim().Length == 0 || AreaAppearance.NormalizeColor(_editText).Length > 0;

        public bool IsDefault => Hex.Length == 0;

        /// <summary>The chosen color, or nothing (the preview then shows "Default").</summary>
        public IBrush PreviewBrush => Color.TryParse(Hex, out Color color) ? new SolidColorBrush(color) : null;

        public string StateText => IsDefault ? "Default" : Hex;

        public ICommand PickCommand { get; }

        public ICommand ClearCommand { get; }

        public override void Refresh()
        {
            _editText = Hex;
            RaisePropertyChanged(nameof(EditText));
            RaiseValueChanged();
        }

        private void RaiseValueChanged()
        {
            RaisePropertyChanged(nameof(Hex));
            RaisePropertyChanged(nameof(IsDefault));
            RaisePropertyChanged(nameof(PreviewBrush));
            RaisePropertyChanged(nameof(StateText));
            RaisePropertyChanged(nameof(IsEditTextValid));
            (ClearCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>One swatch of a <see cref="ColorSetting"/>.</summary>
    public sealed class ColorSwatch
    {
        public ColorSwatch(string hex, string name)
        {
            Hex = hex;
            Name = name;
            Brush = new SolidColorBrush(Color.Parse(hex));
        }

        public string Hex { get; }

        public string Name { get; }

        public IBrush Brush { get; }

        public string ToolTip => Name + " (" + Hex + ")";
    }
}
