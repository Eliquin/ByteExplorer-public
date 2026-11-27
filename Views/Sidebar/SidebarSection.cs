// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace ByteExplorer.Views.Sidebar
{
    /// <summary>
    /// A titled sidebar section that folds away: a header ("QUICK ACCESS ›") with optional buttons on its right, and the
    /// rows below. Whether it is collapsed is remembered per <see cref="SectionKey"/> in the settings, read when the
    /// section appears in a window and written when it is toggled. The look is the control theme in Themes/Styles.axaml;
    /// the left panel's design (Settings › Left panel › Design: Classic, Quiet, Cards, Guides, Chips) is a class on the
    /// Modern panel that shows or hides the template's parts (an icon, the count, where the chevron sits, a card, a guide
    /// line). What a section does is the same in every design.
    /// </summary>
    [PseudoClasses(":collapsed", ":counted")]
    public sealed class SidebarSection : ContentControl
    {
        public static readonly StyledProperty<string> HeaderProperty =
            AvaloniaProperty.Register<SidebarSection, string>(nameof(Header));

        public static readonly StyledProperty<object> HeaderActionsProperty =
            AvaloniaProperty.Register<SidebarSection, object>(nameof(HeaderActions));

        public static readonly StyledProperty<bool> IsExpandedProperty =
            AvaloniaProperty.Register<SidebarSection, bool>(nameof(IsExpanded), defaultValue: true);

        public static readonly StyledProperty<string> SectionKeyProperty =
            AvaloniaProperty.Register<SidebarSection, string>(nameof(SectionKey));

        public static readonly StyledProperty<Thickness> HeaderMarginProperty =
            AvaloniaProperty.Register<SidebarSection, Thickness>(nameof(HeaderMargin), new Thickness(16, 4, 10, 2));

        /// <summary>The section's icon (Cards and Guides designs show it beside the title).</summary>
        public static readonly StyledProperty<Icons.Symbol> IconProperty =
            AvaloniaProperty.Register<SidebarSection, Icons.Symbol>(nameof(Icon), Icons.Symbol.Folder);

        /// <summary>How many items it holds; the compact designs show it while the section is collapsed.</summary>
        public static readonly StyledProperty<int> CountProperty =
            AvaloniaProperty.Register<SidebarSection, int>(nameof(Count));

        public static readonly DirectProperty<SidebarSection, string> CountTextProperty =
            AvaloniaProperty.RegisterDirect<SidebarSection, string>(nameof(CountText), s => s.CountText);

        private bool _restoring;
        private string _countText = string.Empty;

        static SidebarSection()
        {
            IsExpandedProperty.Changed.AddClassHandler<SidebarSection>((section, _) => section.OnExpandedChanged());
            HeaderProperty.Changed.AddClassHandler<SidebarSection>((section, _) =>
                section.SetAndRaise(HeaderCapsProperty, ref section._headerCaps, (section.Header ?? string.Empty).ToUpper(System.Globalization.CultureInfo.CurrentCulture)));
                        CountProperty.Changed.AddClassHandler<SidebarSection>((section, _) =>
            {
                string text = section.Count > 0 ? section.Count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) : string.Empty;
                section.SetAndRaise(CountTextProperty, ref section._countText, text);
                section.PseudoClasses.Set(":counted", section.Count > 0);
                StateChanged?.Invoke(section, System.EventArgs.Empty);
            });
        }

        public static readonly DirectProperty<SidebarSection, string> HeaderCapsProperty =
            AvaloniaProperty.RegisterDirect<SidebarSection, string>(nameof(HeaderCaps), s => s.HeaderCaps);

        private string _headerCaps = string.Empty;

        /// <summary>The title in capitals ("QUICK ACCESS"): the Quiet and Chips designs' small letter-spaced labels.</summary>
        public string HeaderCaps => _headerCaps;

        /// <summary>A section was collapsed or expanded, appeared, or its count changed (the Chips design's row follows).</summary>
        public static event System.EventHandler StateChanged;

        public Icons.Symbol Icon
        {
            get => GetValue(IconProperty);
            set => SetValue(IconProperty, value);
        }

        public int Count
        {
            get => GetValue(CountProperty);
            set => SetValue(CountProperty, value);
        }

        /// <summary>The count as shown ("12"); empty for none.</summary>
        public string CountText => _countText;

        public string Header
        {
            get => GetValue(HeaderProperty);
            set => SetValue(HeaderProperty, value);
        }

        /// <summary>Buttons on the right of the header ("+" to add).</summary>
        public object HeaderActions
        {
            get => GetValue(HeaderActionsProperty);
            set => SetValue(HeaderActionsProperty, value);
        }

        public bool IsExpanded
        {
            get => GetValue(IsExpandedProperty);
            set => SetValue(IsExpandedProperty, value);
        }

        /// <summary>The name its collapsed state is saved under ("QuickAccess").</summary>
        public string SectionKey
        {
            get => GetValue(SectionKeyProperty);
            set => SetValue(SectionKeyProperty, value);
        }

        public Thickness HeaderMargin
        {
            get => GetValue(HeaderMarginProperty);
            set => SetValue(HeaderMarginProperty, value);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            ViewModels.MainViewModel main = Main();
            if (main != null && !string.IsNullOrEmpty(SectionKey))
            {
                _restoring = true;
                IsExpanded = !main.IsSidebarSectionCollapsed(SectionKey);
                _restoring = false;
            }

            PseudoClasses.Set(":collapsed", !IsExpanded);
            StateChanged?.Invoke(this, System.EventArgs.Empty);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            StateChanged?.Invoke(this, System.EventArgs.Empty);
        }

        private void OnExpandedChanged()
        {
            PseudoClasses.Set(":collapsed", !IsExpanded);
            if (!_restoring && !string.IsNullOrEmpty(SectionKey))
            {
                Main()?.SetSidebarSectionCollapsed(SectionKey, !IsExpanded);
            }

            StateChanged?.Invoke(this, System.EventArgs.Empty);
        }

        private ViewModels.MainViewModel Main() => this.FindAncestorOfType<MainWindowBase>()?.ViewModel;
    }
}
