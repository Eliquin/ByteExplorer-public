// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.LogicalTree;
using Avalonia.Media;
using ByteExplorer.Core.Models;

namespace ByteExplorer.Views
{
    /// <summary>
    /// One customizable area of a main window layout (the address bar, toolbar, left panel, main view, details panel or
    /// status bar): the element that holds it, the theme resources its own background comes from, and the fixed-height
    /// grid row it sits in, if any.
    /// </summary>
    public sealed class AreaPart
    {
        public AreaPart(string area, Control host, params string[] backgroundKeys)
        {
            Area = area;
            Host = host ?? throw new ArgumentNullException(nameof(host));
            BackgroundKeys = backgroundKeys ?? Array.Empty<string>();
        }

        /// <summary>One of the <see cref="AreaAppearance"/> area keys.</summary>
        public string Area { get; }

        /// <summary>The area's outermost element (a Border, or a control with a Background).</summary>
        public Control Host { get; }

        /// <summary>
        /// The theme resources the area's surfaces are painted with: made transparent inside the area while it has a
        /// background color (or the main view an image), so the area shows one color painted once on <see cref="Host"/>.
        /// </summary>
        public IReadOnlyList<string> BackgroundKeys { get; }

        /// <summary>Optional: the fixed-height row holding the area, made taller or shorter with its text size.</summary>
        public RowDefinition Row { get; init; }
    }

    /// <summary>
    /// Applies an <see cref="AreaAppearance"/> to an <see cref="AreaPart"/> and takes it off again, leaving the layout as
    /// its XAML made it. Every change is reversible without remembering anything:
    /// <list type="bullet">
    /// <item>Text color: the theme's text resources (TextPrimary, TextSecondary, TextMuted, IconGray) are overridden in the
    /// host's own resources, so every text in the area that uses them follows; removing the entries restores the theme.</item>
    /// <item>Background: the color is set on the host at animation priority, above the XAML's DynamicResource binding,
    /// which comes back when the value's token is disposed; the area's own surface resources are made transparent below it.</item>
    /// <item>Text size: the host is wrapped once in a LayoutTransformControl that scales it (the XAML sets font sizes on
    /// hundreds of elements, so an inherited font size would change almost nothing; a scale keeps headings, captions and
    /// icons in proportion). Scale 1 leaves the wrapper with no transform.</item>
    /// </list>
    /// Popups opened from a customized area (menus, tooltips, the address bar's suggestions) look up resources through it;
    /// <see cref="ResetPopupResources"/> gives them the theme's values back, so a menu never gets an area's colors.
    /// </summary>
    public static class AreaStyler
    {
        /// <summary>The theme resources a text color overrides: primary, secondary and muted text, and icons.</summary>
        public static readonly IReadOnlyList<(string Key, double Opacity)> TextKeys = new[]
        {
            ("TextPrimary", 1.0),
            ("TextSecondary", 0.78),
            ("TextMuted", 0.58),
            ("IconGray", 0.85),
        };

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, IDisposable> BackgroundTokens =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Control, IDisposable>();

        private static readonly HashSet<string> OverriddenKeys = new HashSet<string>(StringComparer.Ordinal);
        private static bool s_popupHookInstalled;

        /// <summary>Applies the area's look (null = the theme's).</summary>
        public static void Apply(AreaPart part, AreaAppearance appearance)
        {
            InstallPopupHook();
            ApplyTextColor(part.Host, ParseColor(appearance?.TextColorHex));
            ApplyBackground(part, ParseColor(appearance?.BackgroundColorHex));
            ApplyScale(part.Host, appearance?.TextScale ?? 1.0);
        }

        /// <summary>The element whose subtree gets the area's transparent surface resources: the host's content.</summary>
        public static Control ContentOf(Control host) => host switch
        {
            Decorator decorator => decorator.Child,
            ContentControl content => content.Content as Control,
            _ => null,
        } ?? host;

        /// <summary>A parsed "#RRGGBB" / "#AARRGGBB", or null for an empty or invalid one.</summary>
        public static Color? ParseColor(string hex)
        {
            string normalized = AreaAppearance.NormalizeColor(hex);
            return normalized.Length > 0 && Color.TryParse(normalized, out Color color) ? color : null;
        }

        /// <summary>The wrapper scaling the host's text, or null while its size was never changed.</summary>
        public static LayoutTransformControl ScalerOf(Control host) => host.Parent as LayoutTransformControl is { } wrapper && wrapper.Child == host ? wrapper : null;

        /// <summary>Makes the theme's surface resources transparent in <paramref name="scope"/>, or restores them.</summary>
        public static void SetSurfacesTransparent(Control scope, IReadOnlyList<string> keys, bool transparent)
        {
            foreach (string key in keys)
            {
                if (transparent)
                {
                    scope.Resources[key] = Brushes.Transparent;
                    Track(key);
                }
                else
                {
                    scope.Resources.Remove(key);
                }
            }
        }

        /// <summary>Sets <paramref name="host"/>'s background above its XAML value, or (null) gives the XAML value back.</summary>
        public static void SetBackground(Control host, IBrush brush)
        {
            AvaloniaProperty property = BackgroundPropertyOf(host);
            if (property == null)
            {
                return;
            }

            // A value set above local priority comes with a token; disposing it takes the value off again.
            if (BackgroundTokens.TryGetValue(host, out IDisposable previous))
            {
                BackgroundTokens.Remove(host);
                previous?.Dispose();
            }

            if (brush != null)
            {
                IDisposable token = host.SetValue(property, brush, BindingPriority.Animation);
                if (token != null)
                {
                    BackgroundTokens.Add(host, token);
                }
            }
        }

        private static void ApplyTextColor(Control host, Color? color)
        {
            foreach ((string key, double opacity) in TextKeys)
            {
                if (color is Color c)
                {
                    host.Resources[key] = new SolidColorBrush(c, opacity);
                    Track(key);
                }
                else
                {
                    host.Resources.Remove(key);
                }
            }
        }

        private static void ApplyBackground(AreaPart part, Color? color)
        {
            SetBackground(part.Host, color is Color c ? new SolidColorBrush(c) : null);
            // The main view's surfaces may also be transparent for its background image; that is set after this.
            SetSurfacesTransparent(ContentOf(part.Host), part.BackgroundKeys, color != null);
        }

        private static void ApplyScale(Control host, double scale)
        {
            bool unchanged = Math.Abs(scale - 1.0) < 0.001;
            LayoutTransformControl wrapper = ScalerOf(host);
            if (wrapper == null)
            {
                if (unchanged)
                {
                    return; // never wrapped: nothing to undo
                }

                wrapper = Wrap(host);
                if (wrapper == null)
                {
                    return;
                }
            }

            wrapper.LayoutTransform = unchanged ? null : new ScaleTransform(scale, scale);
        }

        /// <summary>
        /// Puts the host inside a new LayoutTransformControl in the host's place: the wrapper takes the host's grid and dock
        /// position and Z order; the host keeps its own size, margin and alignment inside it.
        /// </summary>
        private static LayoutTransformControl Wrap(Control host)
        {
            var wrapper = new LayoutTransformControl { ClipToBounds = false };
            switch (host.Parent)
            {
                case Panel panel:
                    int index = panel.Children.IndexOf(host);
                    CopyPlacement(host, wrapper);
                    panel.Children.RemoveAt(index);
                    wrapper.Child = host;
                    panel.Children.Insert(index, wrapper);
                    return wrapper;
                case Decorator decorator:
                    decorator.Child = null;
                    wrapper.Child = host;
                    decorator.Child = wrapper;
                    return wrapper;
                case ContentControl content:
                    content.Content = null;
                    wrapper.Child = host;
                    content.Content = wrapper;
                    return wrapper;
                default:
                    return null; // an unusual parent: the size setting does nothing rather than break the layout
            }
        }

        private static void CopyPlacement(Control from, Control to)
        {
            Grid.SetRow(to, Grid.GetRow(from));
            Grid.SetColumn(to, Grid.GetColumn(from));
            Grid.SetRowSpan(to, Grid.GetRowSpan(from));
            Grid.SetColumnSpan(to, Grid.GetColumnSpan(from));
            DockPanel.SetDock(to, DockPanel.GetDock(from));
            to.ZIndex = from.ZIndex;
        }

        private static AvaloniaProperty BackgroundPropertyOf(Control control) => control switch
        {
            Border => Border.BackgroundProperty,
            Panel => Panel.BackgroundProperty,
            TemplatedControl => TemplatedControl.BackgroundProperty,
            _ => null,
        };

        private static void Track(string key)
        {
            lock (OverriddenKeys)
            {
                OverriddenKeys.Add(key);
            }
        }

        // ------------------------------------------------------------------ popups

        private static void InstallPopupHook()
        {
            if (s_popupHookInstalled)
            {
                return;
            }

            s_popupHookInstalled = true;
            Popup.IsOpenProperty.Changed.AddClassHandler<Popup>((popup, e) =>
            {
                if (e.NewValue is true)
                {
                    ResetPopupResources(popup);
                }
            });
        }

        /// <summary>
        /// Gives a popup's content the theme's value of every resource an area overrides (text colors, surfaces), so a
        /// menu, tooltip or suggestion list opened from a customized area keeps the theme's look.
        /// </summary>
        public static void ResetPopupResources(Popup popup)
        {
            if (popup?.Child is not Control content || Application.Current is not { } app)
            {
                return;
            }

            string[] keys;
            lock (OverriddenKeys)
            {
                if (OverriddenKeys.Count == 0)
                {
                    return;
                }

                keys = new string[OverriddenKeys.Count];
                OverriddenKeys.CopyTo(keys);
            }

            foreach (string key in keys)
            {
                if (app.TryGetResource(key, popup.ActualThemeVariant, out object value))
                {
                    content.Resources[key] = value;
                }
            }
        }
    }
}
