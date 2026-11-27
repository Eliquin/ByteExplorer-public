// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ByteExplorer.Core;
using ByteExplorer.Core.Omnibar;
using ByteExplorer.Icons;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels.Omnibar;
using ByteExplorer.Views.Menus;
using Serilog;

namespace ByteExplorer.Views.Omnibar
{
    /// <summary>
    /// The omnibar's view: everything here is presentation and input. Breadcrumbs are built in code (a handful of
    /// controls, rebuilt only when the location changes) so the panel can fold them; the suggestion list, the text and the
    /// states are bound to <see cref="OmnibarViewModel"/>.
    /// <para>
    /// Focus: nothing in the suggestion popup can take focus, so the text box keeps the caret while rows are clicked
    /// (rows act on press). Losing focus to anything else ends editing after a short grace period, which lets a click on
    /// the popup finish first.
    /// </para>
    /// </summary>
    public partial class OmnibarView : UserControl
    {
        private static readonly TimeSpan SpringLoadDelay = TimeSpan.FromMilliseconds(900);
        private static readonly TimeSpan FocusGrace = TimeSpan.FromMilliseconds(150);

        private OmnibarViewModel _vm;
        private Border _dropCrumb;
        private DispatcherTimer _springTimer;
        private DispatcherTimer _focusTimer;
        private bool _actingFromPopup;
        private int _rejectGeneration;
        private readonly TranslateTransform _shake = new TranslateTransform();

        public OmnibarView()
        {
            InitializeComponent();

            Input.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
            Input.LostFocus += OnInputLostFocus;
            Input.GotFocus += (_, _) => _focusTimer?.Stop();
            Input.PropertyChanged += OnInputPropertyChanged;
            SuggestionList.AddHandler(PointerPressedEvent, OnSuggestionPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            CrumbArea.PointerReleased += OnCrumbAreaReleased;
            Shell.SizeChanged += (_, e) => PopupSurface.Width = Math.Max(360, e.NewSize.Width);

            Shell.AddHandler(DragDrop.DragOverEvent, OnDragOver);
            Shell.AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetDropCrumb(null));
            Shell.AddHandler(DragDrop.DropEvent, OnDrop);

            DataContextChanged += (_, _) => Attach(DataContext as OmnibarViewModel);
            Shell.RenderTransform = _shake;
        }

        /// <summary>Starts typing: the whole path selected (typing replaces it), or the given text with the caret after it.</summary>
        public void BeginEdit(string initialText = null)
        {
            if (_vm == null)
            {
                return;
            }

            _vm.BeginEdit(initialText);
            Dispatcher.UIThread.Post(() =>
            {
                Input.Focus();
                if (initialText == null)
                {
                    Input.SelectAll();
                }
                else
                {
                    Input.ClearSelection();
                    Input.CaretIndex = Input.Text?.Length ?? 0;
                }

                UpdateGhost();
            }, DispatcherPriority.Input);
        }

        private void Attach(OmnibarViewModel vm)
        {
            if (ReferenceEquals(vm, _vm))
            {
                return;
            }

            if (_vm != null)
            {
                _vm.PropertyChanged -= OnViewModelPropertyChanged;
                _vm.InputRejected -= OnInputRejected;
            }

            _vm = vm;
            if (_vm != null)
            {
                _vm.PropertyChanged += OnViewModelPropertyChanged;
                _vm.InputRejected += OnInputRejected;
            }

            BuildCrumbs();
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(OmnibarViewModel.Crumbs):
                    BuildCrumbs();
                    break;
                case nameof(OmnibarViewModel.Ghost):
                case nameof(OmnibarViewModel.Text):
                    UpdateGhost();
                    break;
                case nameof(OmnibarViewModel.SelectedIndex):
                    if (_vm.SelectedIndex >= 0)
                    {
                        SuggestionList.ScrollIntoView(_vm.SelectedIndex);
                    }

                    break;
            }
        }

        // ================================================================== keyboard

        private void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            if (_vm == null)
            {
                return;
            }

            KeyModifiers command = OperatingSystem.IsMacOS() ? KeyModifiers.Alt : KeyModifiers.Control;
            switch (e.Key)
            {
                case Key.Down:
                    _vm.MoveSelection(1);
                    e.Handled = true;
                    break;
                case Key.Up:
                    _vm.MoveSelection(-1);
                    e.Handled = true;
                    break;
                case Key.PageDown:
                    _vm.MoveSelection(5);
                    e.Handled = true;
                    break;
                case Key.PageUp:
                    _vm.MoveSelection(-5);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    e.Handled = true;
                    _vm.CommitAsync().Forget("omnibar commit");
                    break;
                case Key.Escape:
                    e.Handled = true;
                    if (_vm.SelectedIndex >= 0)
                    {
                        _vm.SelectedIndex = -1;
                    }
                    else
                    {
                        _vm.CancelEdit();
                    }

                    break;
                case Key.Tab when e.KeyModifiers == KeyModifiers.None:
                    if (_vm.AcceptCompletion())
                    {
                        MoveCaretToEnd();
                        e.Handled = true;
                    }

                    break;
                case Key.Right when e.KeyModifiers == KeyModifiers.None && IsCaretAtEnd() && !string.IsNullOrEmpty(_vm.Ghost):
                    // As in fish and browser address bars: → at the end takes the faded completion.
                    if (_vm.AcceptCompletion())
                    {
                        MoveCaretToEnd();
                        e.Handled = true;
                    }

                    break;
                case Key.Back when (e.KeyModifiers & command) != 0:
                    DeleteLastSegment();
                    e.Handled = true;
                    break;
            }
        }

        /// <summary>Ctrl+Backspace (Alt+Backspace on macOS): removes the last path segment, as a word in a sentence.</summary>
        private void DeleteLastSegment()
        {
            string text = Input.Text ?? string.Empty;
            int caret = Math.Min(Input.CaretIndex, text.Length);
            if (Input.SelectionStart != Input.SelectionEnd)
            {
                int start = Math.Min(Input.SelectionStart, Input.SelectionEnd);
                Input.Text = text.Remove(start, Math.Abs(Input.SelectionEnd - Input.SelectionStart));
                Input.CaretIndex = start;
                return;
            }

            int index = caret;
            while (index > 0 && PathUtil.IsSeparator(text[index - 1]))
            {
                index--;
            }

            while (index > 0 && !PathUtil.IsSeparator(text[index - 1]) && text[index - 1] != ' ')
            {
                index--;
            }

            Input.Text = text.Remove(index, caret - index);
            Input.CaretIndex = index;
        }

        private void MoveCaretToEnd()
        {
            Input.ClearSelection();
            Input.CaretIndex = Input.Text?.Length ?? 0;
        }

        private bool IsCaretAtEnd()
        {
            string text = Input.Text ?? string.Empty;
            return Input.SelectionStart == Input.SelectionEnd && Input.CaretIndex >= text.Length;
        }

        private void OnInputPropertyChanged(object sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == TextBox.CaretIndexProperty || e.Property == TextBox.SelectionStartProperty ||
                e.Property == TextBox.SelectionEndProperty || e.Property == BoundsProperty)
            {
                UpdateGhost();
            }
        }

        /// <summary>
        /// The faded completion: the typed text drawn invisibly (so the rest lines up after it) and the completion in a
        /// muted color. Shown only with the caret at the end, nothing selected, and when it fits (the box would scroll).
        /// </summary>
        private void UpdateGhost()
        {
            string ghost = _vm?.Ghost;
            string text = Input.Text ?? string.Empty;
            if (string.IsNullOrEmpty(ghost) || !_vm.IsEditing || !IsCaretAtEnd() || !string.Equals(text, _vm.Text, StringComparison.Ordinal))
            {
                GhostText.IsVisible = false;
                return;
            }

            var typeface = new Typeface(Input.FontFamily, Input.FontStyle, Input.FontWeight);
            double width = new FormattedText(text + ghost, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, Input.FontSize, null).Width;
            if (width > Input.Bounds.Width - 4)
            {
                GhostText.IsVisible = false;
                return;
            }

            GhostText.FontFamily = Input.FontFamily;
            GhostText.Inlines.Clear();
            GhostText.Inlines.Add(new Run(text) { Foreground = Brushes.Transparent });
            var rest = new Run(ghost);
            rest[!TextElement.ForegroundProperty] = this.GetResourceObservable("TextMuted").ToBinding();
            GhostText.Inlines.Add(rest);
            GhostText.IsVisible = true;
        }

        private void OnInputLostFocus(object sender, RoutedEventArgs e)
        {
            if (_vm == null || !_vm.IsEditing)
            {
                return;
            }

            _focusTimer ??= new DispatcherTimer(FocusGrace, DispatcherPriority.Input, (_, _) =>
            {
                _focusTimer.Stop();
                if (_vm != null && _vm.IsEditing && !Input.IsFocused && !_actingFromPopup)
                {
                    _vm.CancelEdit();
                }
            });
            _focusTimer.Stop();
            _focusTimer.Start();
        }

        // ================================================================== suggestions

        private void OnSuggestionPressed(object sender, PointerPressedEventArgs e)
        {
            if (_vm == null || !e.GetCurrentPoint(SuggestionList).Properties.IsLeftButtonPressed)
            {
                return;
            }

            for (Visual visual = e.Source as Visual; visual != null && !ReferenceEquals(visual, SuggestionList); visual = visual.GetVisualParent())
            {
                if (visual is ListBoxItem item && item.DataContext is OmnibarSuggestion suggestion)
                {
                    e.Handled = true;
                    if (!suggestion.IsActionable)
                    {
                        return;
                    }

                    _actingFromPopup = true;
                    RunFromPopupAsync(suggestion).Forget("omnibar suggestion");
                    return;
                }
            }
        }

        private async Task RunFromPopupAsync(OmnibarSuggestion suggestion)
        {
            try
            {
                await _vm.ExecuteAsync(suggestion);
            }
            finally
            {
                _actingFromPopup = false;
            }
        }

        private void OnInputRejected(object sender, EventArgs e) => ShakeAsync().Forget("omnibar shake");

        /// <summary>A short horizontal shake and a red outline: "that leads nowhere".</summary>
        private async Task ShakeAsync()
        {
            int generation = ++_rejectGeneration;
            Shell.Classes.Add("rejected");
            var animation = new Animation
            {
                Duration = TimeSpan.FromMilliseconds(380),
                Easing = new Avalonia.Animation.Easings.SineEaseOut()
            };
            double[] offsets = { 0, -8, 7, -5, 3, -1, 0 };
            for (int i = 0; i < offsets.Length; i++)
            {
                animation.Children.Add(new KeyFrame
                {
                    Cue = new Cue((double)i / (offsets.Length - 1)),
                    Setters = { new Setter(TranslateTransform.XProperty, offsets[i]) }
                });
            }

            // A transform's X is a plain double Avalonia can animate (it has no keyframe animator for RenderTransform).
            await animation.RunAsync(_shake);
            await Task.Delay(1000);
            if (generation == _rejectGeneration)
            {
                Shell.Classes.Remove("rejected");
            }
        }

        // ================================================================== breadcrumbs

        private void BuildCrumbs()
        {
            Crumbs.Children.Clear();
            IReadOnlyList<Crumb> crumbs = _vm?.Crumbs ?? Array.Empty<Crumb>();

            var overflow = new Button { Classes = { "crumbChevron" }, Content = Icon(Symbol.MoreHorizontal, 14), Margin = new Thickness(2, 0, 0, 0) };
            ToolTip.SetTip(overflow, "More of this path");
            overflow.Click += (_, _) => ShowOverflowMenu(overflow);
            Crumbs.Children.Add(overflow);

            for (int i = 0; i < crumbs.Count; i++)
            {
                Crumb crumb = crumbs[i];
                bool last = i == crumbs.Count - 1;
                var segment = new StackPanel { Orientation = Orientation.Horizontal };

                var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                Symbol? symbol = crumb.Kind switch
                {
                    CrumbKind.Archive => Symbol.FolderZip,
                    CrumbKind.Network => Symbol.GlobeDesktop,
                    CrumbKind.Volume => Symbol.UsbStick,
                    CrumbKind.Phone => Symbol.Phone,
                    CrumbKind.Lens when i == 0 => Symbol.Sparkle,
                    _ => null
                };
                if (symbol.HasValue)
                {
                    label.Children.Add(Icon(symbol.Value, 13));
                }

                var text = new TextBlock
                {
                    Text = crumb.Name,
                    FontSize = 12.5,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 240
                };
                text[!TextBlock.ForegroundProperty] = this.GetResourceObservable("TextPrimary").ToBinding();
                label.Children.Add(text);

                var border = new Border { Classes = { "crumb" }, Child = label, Tag = crumb };
                if (last)
                {
                    border.Classes.Add("current");
                }

                ToolTip.SetTip(border, crumb.Path ?? crumb.Name);
                DragDrop.SetAllowDrop(border, crumb.Path != null);
                border.PointerReleased += OnCrumbReleased;
                segment.Children.Add(border);

                if (crumb.Kind == CrumbKind.Lens && !last)
                {
                    // "Lens ›": the chevron lists the lenses, to switch from one to another here.
                    var lenses = new Button { Classes = { "crumbChevron" }, Content = Icon(Symbol.ChevronRight, 10), Tag = crumb };
                    ToolTip.SetTip(lenses, "Other lenses");
                    lenses.Click += (_, _) => ShowLensMenu(lenses);
                    segment.Children.Add(lenses);
                }
                else if (crumb.Path != null)
                {
                    var chevron = new Button { Classes = { "crumbChevron" }, Content = Icon(Symbol.ChevronRight, 10), Tag = crumb };
                    ToolTip.SetTip(chevron, "Folders in " + crumb.Name);
                    string next = last ? null : crumbs[i + 1].Name;
                    chevron.Click += (_, _) => ShowSubfolderDropdown(chevron, crumb, next);
                    segment.Children.Add(chevron);
                }

                Crumbs.Children.Add(segment);
            }
        }

        private SymbolIcon Icon(Symbol symbol, double size)
        {
            var icon = new SymbolIcon { Symbol = symbol, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
            icon[!SymbolIcon.ForegroundProperty] = this.GetResourceObservable("IconGray").ToBinding();
            return icon;
        }

        private void OnCrumbAreaReleased(object sender, PointerReleasedEventArgs e)
        {
            // A click beside the breadcrumbs (not on one, not on a separator) starts typing.
            if (e.InitialPressMouseButton != MouseButton.Left || IsInsideCrumbControl(e.Source as Visual))
            {
                return;
            }

            BeginEdit();
        }

        private bool IsInsideCrumbControl(Visual source)
        {
            for (Visual visual = source; visual != null && !ReferenceEquals(visual, CrumbArea); visual = visual.GetVisualParent())
            {
                if ((visual is Border border && border.Classes.Contains("crumb")) || visual is Button)
                {
                    return true;
                }
            }

            return false;
        }

        private void OnCrumbReleased(object sender, PointerReleasedEventArgs e)
        {
            if (_vm == null || !(sender is Border border) || !(border.Tag is Crumb crumb))
            {
                return;
            }

            e.Handled = true;
            if (e.InitialPressMouseButton == MouseButton.Left)
            {
                _vm.NavigateToCrumbAsync(crumb).Forget("breadcrumb");
            }
            else if (e.InitialPressMouseButton == MouseButton.Right && crumb.Path != null && _vm.Pane != null)
            {
                // The folder's own menu: open, terminal, copy path, bookmark, paste into, properties…
                FileContextMenu.ShowForItems(border, _vm.Pane, new[] { crumb.Path }, pathsAreSelection: false);
            }
        }

        /// <summary>The "…" button: the segments folded away, deepest last, as a menu.</summary>
        private void ShowOverflowMenu(Button anchor)
        {
            IReadOnlyList<Crumb> crumbs = _vm?.Crumbs;
            int hidden = Crumbs.HiddenCount;
            if (crumbs == null || hidden <= 0)
            {
                return;
            }

            var items = new List<MenuItem>();
            for (int i = 0; i < Math.Min(hidden, crumbs.Count); i++)
            {
                Crumb crumb = crumbs[i];
                var item = new MenuItem { Header = crumb.Name, Icon = Icon(i == 0 ? _vm.LocationSymbol : Symbol.Folder, 14) };
                item.Click += (_, _) => _vm.NavigateToCrumbAsync(crumb).Forget("breadcrumb overflow");
                items.Add(item);
            }

            var flyout = new MenuFlyout { ItemsSource = items, Placement = PlacementMode.BottomEdgeAlignedLeft };
            flyout.ShowAt(anchor);
        }

        /// <summary>
        /// The separator after a segment: that folder's subfolders, filterable by typing, with the one on the current path
        /// highlighted. Listed through the index cache (a folder visited a moment ago lists instantly).
        /// </summary>
        /// <summary>The "Lens ›" chevron's menu: every lens, the one shown checked.</summary>
        private void ShowLensMenu(Button anchor)
        {
            if (_vm?.Lenses == null)
            {
                return;
            }

            var items = new List<MenuItem>();
            foreach (ViewModels.Lens.LensEntryViewModel lens in _vm.Lenses)
            {
                ViewModels.Lens.LensEntryViewModel chosen = lens;
                var item = new MenuItem
                {
                    Header = lens.Name,
                    Icon = Icon(lens.Icon, 14),
                    ToggleType = MenuItemToggleType.Radio,
                    IsChecked = lens.IsActive,
                    GroupName = "lens"
                };
                item.Click += (_, _) => _vm.OpenLensAsync(chosen).Forget("switch lens from the address bar");
                items.Add(item);
            }

            var flyout = new MenuFlyout { ItemsSource = items, Placement = PlacementMode.BottomEdgeAlignedLeft };
            flyout.ShowAt(anchor);
        }

        private void ShowSubfolderDropdown(Button anchor, Crumb crumb, string highlight)
        {
            if (_vm == null || crumb.Path == null)
            {
                return;
            }

            var filter = new TextBox { Watermark = "Filter folders", FontSize = 12.5, MinHeight = 28, Margin = new Thickness(0, 0, 0, 6) };
            var status = new TextBlock { Text = "Loading…", FontSize = 12, Margin = new Thickness(8, 4) };
            status[!TextBlock.ForegroundProperty] = this.GetResourceObservable("TextSecondary").ToBinding();
            var list = new ListBox
            {
                MaxHeight = 380,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                ItemTemplate = new FuncDataTemplate<string>((name, _) =>
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(2, 1) };
                    row.Children.Add(new SymbolIcon
                    {
                        Symbol = Core.Services.ArchiveBrowser.IsArchiveFile(name) ? Symbol.FolderZip : Symbol.Folder,
                        IconVariant = IconVariant.Filled,
                        FontSize = 15,
                        Foreground = this.TryFindResource("FolderYellow", ActualThemeVariant, out object brush) ? brush as IBrush : Brushes.Goldenrod
                    });
                    row.Children.Add(new TextBlock { Text = name, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 280 });
                    return row;
                })
            };

            var content = new StackPanel { Width = 300 };
            content.Children.Add(filter);
            content.Children.Add(status);
            content.Children.Add(list);
            var flyout = new Flyout { Content = content, Placement = PlacementMode.BottomEdgeAlignedLeft, FlyoutPresenterClasses = { "tools" } };

            IReadOnlyList<string> all = Array.Empty<string>();
            void Apply()
            {
                string text = filter.Text?.Trim() ?? string.Empty;
                IReadOnlyList<string> shown = text.Length == 0
                    ? all
                    : all.Where(n => n.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
                list.ItemsSource = shown;
                status.Text = all.Count == 0 ? "No subfolders" : shown.Count == 0 ? "No folder matches" : null;
                status.IsVisible = status.Text != null;
                if (text.Length > 0 && shown.Count > 0)
                {
                    list.SelectedIndex = 0;
                }
            }

            void Open(string name)
            {
                if (string.IsNullOrEmpty(name))
                {
                    return;
                }

                flyout.Hide();
                _vm.NavigateToCrumbAsync(new Crumb(name, Path.Combine(crumb.Path, name), CrumbKind.Folder)).Forget("breadcrumb dropdown");
            }

            filter.TextChanged += (_, _) => Apply();
            filter.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    Open(list.SelectedItem as string ?? (list.ItemCount > 0 ? list.Items[0] as string : null));
                    e.Handled = true;
                }
                else if (e.Key == Key.Down && list.ItemCount > 0)
                {
                    list.SelectedIndex = Math.Max(0, list.SelectedIndex);
                    list.ContainerFromIndex(list.SelectedIndex)?.Focus();
                    e.Handled = true;
                }
            };
            list.AddHandler(PointerReleasedEvent, (_, e) =>
            {
                if (e.InitialPressMouseButton == MouseButton.Left)
                {
                    for (Visual v = e.Source as Visual; v != null && !ReferenceEquals(v, list); v = v.GetVisualParent())
                    {
                        if (v is ListBoxItem item)
                        {
                            Open(item.DataContext as string);
                            return;
                        }
                    }
                }
            }, RoutingStrategies.Bubble, handledEventsToo: true);
            list.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    Open(list.SelectedItem as string);
                    e.Handled = true;
                }
            };

            flyout.Opened += (_, _) => filter.Focus();
            flyout.ShowAt(anchor);
            LoadSubfoldersAsync(crumb.Path, highlight, names =>
            {
                all = names;
                Apply();
                if (highlight != null)
                {
                    int index = IndexOf(names, highlight);
                    if (index >= 0)
                    {
                        list.SelectedIndex = index;
                        list.ScrollIntoView(index);
                    }
                }
            }).Forget("breadcrumb dropdown");
        }

        private async Task LoadSubfoldersAsync(string folder, string highlight, Action<IReadOnlyList<string>> apply)
        {
            IReadOnlyList<string> names = await _vm.GetSubfoldersAsync(folder);
            apply(names);
        }

        private static int IndexOf(IReadOnlyList<string> names, string name)
        {
            for (int i = 0; i < names.Count; i++)
            {
                if (string.Equals(names[i], name, PathUtil.Comparison))
                {
                    return i;
                }
            }

            return -1;
        }

        // ================================================================== drag and drop

        private static Border CrumbFrom(object source)
        {
            for (Visual visual = source as Visual; visual != null; visual = visual.GetVisualParent())
            {
                if (visual is Border border && border.Classes.Contains("crumb"))
                {
                    return border.Tag is Crumb crumb && crumb.Path != null ? border : null;
                }
            }

            return null;
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            Border target = CrumbFrom(e.Source);
            if (target == null || _vm == null || !FileDragDropHelper.TryGetDroppedPaths(e.DataTransfer, out IReadOnlyList<string> paths))
            {
                e.DragEffects = DragDropEffects.None;
                SetDropCrumb(null);
                return;
            }

            e.DragEffects = FileDragDropHelper.ResolveEffect(e, ((Crumb)target.Tag).Path, paths);
            e.Handled = true;
            SetDropCrumb(e.DragEffects != DragDropEffects.None ? target : null);
        }

        private async void OnDrop(object sender, DragEventArgs e)
        {
            try
            {
                Border target = CrumbFrom(e.Source);
                SetDropCrumb(null);
                if (target == null || _vm == null || !FileDragDropHelper.TryGetDroppedPaths(e.DataTransfer, out IReadOnlyList<string> paths))
                {
                    return;
                }

                string folder = ((Crumb)target.Tag).Path;
                e.Handled = true;
                await Menus.DropChoice.RunAsync(target, e, folder, paths, _vm.Pane, move => _vm.DropAsync(paths, folder, move));
            }
            catch (Exception ex)
            {
                // async void: nothing may escape into the UI loop.
                Log.Logger.Warning(ex, "Dropping on a breadcrumb failed");
            }
        }

        /// <summary>
        /// Highlights the segment under a drag; holding the drag over it opens that folder ("spring-loaded", as in Finder),
        /// so files can be dropped deeper than the breadcrumb shows.
        /// </summary>
        private void SetDropCrumb(Border crumb)
        {
            if (ReferenceEquals(_dropCrumb, crumb))
            {
                return;
            }

            _dropCrumb?.Classes.Remove("dropTarget");
            _dropCrumb = crumb;
            _dropCrumb?.Classes.Add("dropTarget");
            Shell.Classes.Set("dropTarget", crumb != null);

            _springTimer?.Stop();
            if (crumb == null)
            {
                return;
            }

            _springTimer ??= new DispatcherTimer(SpringLoadDelay, DispatcherPriority.Normal, (_, _) =>
            {
                _springTimer.Stop();
                if (_dropCrumb?.Tag is Crumb target && _vm != null && !(_dropCrumb.Classes.Contains("current")))
                {
                    _vm.NavigateToCrumbAsync(target).Forget("spring-loaded breadcrumb");
                }
            });
            _springTimer.Start();
        }
    }

    /// <summary>Small converters for the omnibar's templates.</summary>
    public static class OmnibarConverters
    {
        /// <summary>Rows that do nothing (hints) are drawn a little dimmer.</summary>
        public static readonly IValueConverter ActionableOpacity =
            new FuncValueConverter<bool, double>(actionable => actionable ? 1.0 : 0.7);
    }
}
