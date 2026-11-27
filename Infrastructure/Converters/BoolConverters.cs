// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace ByteExplorer.Infrastructure.Converters
{
    /// <summary>
    /// Value converters for the Avalonia views. WPF's Visibility enum has no Avalonia counterpart (controls have a plain
    /// IsVisible bool, and a hidden control never takes layout space, which is what Collapsed meant), so the WPF
    /// *ToVisibility converters became these bool converters; the XAML binds them to IsVisible.
    /// </summary>
    public sealed class EnumEqualsConverter : IValueConverter
    {
        /// <summary>True if the value's name equals the parameter (case-insensitive); "!Name" inverts it.</summary>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null || parameter == null)
            {
                return false;
            }

            string expected = parameter.ToString();
            bool invert = expected.StartsWith("!", StringComparison.Ordinal);
            if (invert)
            {
                expected = expected.Substring(1);
            }

            bool equal = false;
            foreach (string option in expected.Split('|'))
            {
                if (string.Equals(value.ToString(), option, StringComparison.OrdinalIgnoreCase))
                {
                    equal = true;
                    break;
                }
            }

            return invert ? !equal : equal;
        }

        /// <summary>
        /// Two-way use (a radio button bound to an enum): checking the button sets the enum to the parameter.
        /// </summary>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b && b && parameter != null && targetType != null)
            {
                Type enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;
                if (enumType.IsEnum && Enum.TryParse(enumType, parameter.ToString(), ignoreCase: true, out object result))
                {
                    return result;
                }
            }

            return BindingOperations.DoNothing;
        }
    }

    /// <summary>True when the value is null; parameter "Invert" gives true when it is not null.</summary>
    public sealed class IsNullConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool isNull = value == null;
            return string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase) ? !isNull : isNull;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>True when the string is non-empty; parameter "Invert" gives true when it is empty.</summary>
    public sealed class HasTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool hasValue = !string.IsNullOrEmpty(value as string);
            return string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase) ? !hasValue : hasValue;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>
    /// The file list's "show this view" rule (was FileListVisibilityConverter): true when there is no error text
    /// (values[0]) and the view mode (values[1]) equals the parameter.
    /// </summary>
    public sealed class FileListModeVisibleConverter : IMultiValueConverter
    {
        public object Convert(IList<object> values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Count < 2)
            {
                return false;
            }

            bool hasError = !string.IsNullOrEmpty(values[0] as string);
            bool modeMatches = values[1] != null && string.Equals(values[1].ToString(), parameter as string, StringComparison.OrdinalIgnoreCase);
            return !hasError && modeMatches;
        }
    }

    /// <summary>A "#RRGGBB" / "#AARRGGBB" string to a cached brush; Gray for anything unparsable.</summary>
    public sealed class HexToBrushConverter : IValueConverter
    {
        private static readonly Dictionary<string, IBrush> Cache = new Dictionary<string, IBrush>(StringComparer.OrdinalIgnoreCase);
        private static readonly object CacheLock = new object();

        public static IBrush BrushFor(string hex)
        {
            if (string.IsNullOrEmpty(hex))
            {
                return Brushes.Gray;
            }

            lock (CacheLock)
            {
                if (!Cache.TryGetValue(hex, out IBrush brush))
                {
                    brush = Color.TryParse(hex, out Color color) ? new ImmutableSolidColorBrush(color) : (IBrush)Brushes.Gray;
                    Cache[hex] = brush;
                }

                return brush;
            }
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => BrushFor(value as string);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>Static instances for {x:Static} use in XAML, so views need no per-file converter resources.</summary>
    public static class Conv
    {
        public static readonly EnumEqualsConverter EnumEquals = new EnumEqualsConverter();
        public static readonly IsNullConverter IsNull = new IsNullConverter();
        public static readonly HasTextConverter HasText = new HasTextConverter();
        public static readonly FileListModeVisibleConverter FileListModeVisible = new FileListModeVisibleConverter();
        public static readonly HexToBrushConverter HexToBrush = new HexToBrushConverter();
    }
}
