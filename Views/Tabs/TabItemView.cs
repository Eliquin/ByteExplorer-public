// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ByteExplorer.Icons;
using ByteExplorer.ViewModels.Tabs;

namespace ByteExplorer.Views.Tabs
{
    /// <summary>
    /// One tab in the bar: icon (a spinner while its folder is read), title, close button; a pin on pinned vertical tabs.
    /// Its look comes from Themes/TabStyles.axaml through the classes set here; the bar handles the pointer.
    /// </summary>
    internal sealed class TabItemView : Border
    {
        /// <summary>A folder read that ends sooner than this never shows the spinner (no flicker on fast folders).</summary>
        private static readonly TimeSpan SpinnerDelay = TimeSpan.FromMilliseconds(300);

        private readonly SymbolIcon _icon;
        private readonly Panel _iconHost;
        private readonly TextBlock _title;
        private readonly DispatcherTimer _spinnerTimer;
        private Path _spinner;

        public TabItemView(TabViewModel tab)
        {
            Tab = tab ?? throw new ArgumentNullException(nameof(tab));
            Classes.Add("tab");
            Focusable = false;

            _icon = new SymbolIcon { Classes = { "tabIcon" } };
            _iconHost = new Panel { Width = 18, Height = 18, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Children = { _icon } };
            _title = new TextBlock { Classes = { "tabTitle" } };
            CloseButton = new Button
            {
                Classes = { "cmd", "tabClose" },
                Content = new SymbolIcon { Symbol = Symbol.Dismiss, FontSize = 11 },
                Focusable = false
            };
            ToolTip.SetTip(CloseButton, "Close tab (" + (OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl") + "+W)");
            var pin = new SymbolIcon { Symbol = Symbol.Pin, Classes = { "tabPin" } };

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
            grid.Children.Add(_iconHost);
            Grid.SetColumn(_title, 1);
            grid.Children.Add(_title);
            Grid.SetColumn(pin, 2);
            grid.Children.Add(pin);
            Grid.SetColumn(CloseButton, 3);
            grid.Children.Add(CloseButton);

            Child = new Panel
            {
                Children =
                {
                    new Border { Classes = { "tabAccent" } },
                    grid,
                    new Border { Classes = { "tabSeparator" } }
                }
            };

            _spinnerTimer = new DispatcherTimer { Interval = SpinnerDelay };
            _spinnerTimer.Tick += (_, _) =>
            {
                _spinnerTimer.Stop();
                ShowSpinner(Tab.IsLoading);
            };

            Tab.PropertyChanged += OnTabPropertyChanged;
            ApplyAll();
        }

        public TabViewModel Tab { get; }

        /// <summary>Styled as a Border (Themes/TabStyles.axaml selects Border.tab): Avalonia matches a selector's type exactly.</summary>
        protected override Type StyleKeyOverride => typeof(Border);

        public Button CloseButton { get; }

        /// <summary>The tab was pinned or unpinned.</summary>
        public event EventHandler PinnedChanged;

        /// <summary>The bar stops listening (the tab closed, or the bar is rebuilt).</summary>
        public void Detach()
        {
            Tab.PropertyChanged -= OnTabPropertyChanged;
            _spinnerTimer.Stop();
        }

        /// <summary>Horizontal or vertical, and (vertical) shrunk to its icon.</summary>
        public void SetMode(bool vertical, bool collapsed)
        {
            Classes.Set("horizontal", !vertical);
            Classes.Set("vertical", vertical);
            Classes.Set("collapsed", vertical && collapsed);
            bool iconOnly = (vertical && collapsed) || (!vertical && Tab.IsPinned);
            _iconHost.Margin = iconOnly ? new Thickness(0) : new Thickness(10, 0, 0, 0);
            _iconHost.HorizontalAlignment = iconOnly ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            if (Child is Panel panel && panel.Children[1] is Grid grid)
            {
                Grid.SetColumnSpan(_iconHost, iconOnly ? 4 : 1);
                grid.ColumnDefinitions[0].Width = iconOnly ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            }
        }

        private void OnTabPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => OnTabPropertyChanged(sender, e));
                return;
            }

            switch (e.PropertyName)
            {
                case nameof(TabViewModel.Title):
                case nameof(TabViewModel.ToolTip):
                    ApplyText();
                    break;
                case nameof(TabViewModel.Icon):
                    _icon.Symbol = Tab.Icon;
                    break;
                case nameof(TabViewModel.IsSelected):
                    Classes.Set("selected", Tab.IsSelected);
                    break;
                case nameof(TabViewModel.IsPinned):
                    Classes.Set("pinned", Tab.IsPinned);
                    SetMode(Classes.Contains("vertical"), Classes.Contains("collapsed"));
                    PinnedChanged?.Invoke(this, EventArgs.Empty); // the bar re-lays out: a pinned tab's slot differs
                    break;
                case nameof(TabViewModel.IsDropTarget):
                    Classes.Set("drop", Tab.IsDropTarget);
                    break;
                case nameof(TabViewModel.IsLoading):
                    ApplyLoading();
                    break;
            }
        }

        private void ApplyAll()
        {
            _icon.Symbol = Tab.Icon;
            Classes.Set("selected", Tab.IsSelected);
            Classes.Set("pinned", Tab.IsPinned);
            Classes.Set("drop", Tab.IsDropTarget);
            ApplyText();
            ApplyLoading();
        }

        private void ApplyText()
        {
            _title.Text = Tab.Title;
            ToolTip.SetTip(this, Tab.ToolTip);
            AutomationProperties.SetName(this, Tab.Title);
        }

        private void ApplyLoading()
        {
            if (Tab.IsLoading)
            {
                if (_spinner == null && !_spinnerTimer.IsEnabled)
                {
                    _spinnerTimer.Start();
                }
            }
            else
            {
                _spinnerTimer.Stop();
                ShowSpinner(false);
            }
        }

        private void ShowSpinner(bool show)
        {
            if (show && _spinner == null)
            {
                // Created only while needed: an endless animation that isn't on screen still costs a little every frame.
                _spinner = new Path
                {
                    Classes = { "tabSpinner" },
                    Data = Geometry.Parse("M 7,1 A 6,6 0 1 1 1,7"),
                    RenderTransform = new RotateTransform()
                };
                _iconHost.Children.Add(_spinner);
                _icon.IsVisible = false;
            }
            else if (!show && _spinner != null)
            {
                _iconHost.Children.Remove(_spinner);
                _spinner = null;
                _icon.IsVisible = true;
            }
        }
    }
}
