// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ByteExplorer.Core.Security;
using ByteExplorer.Icons;
using ByteExplorer.Infrastructure;
using Serilog;

namespace ByteExplorer.Views.Dialogs
{
    /// <summary>
    /// Asks for a password: masked, with a button that shows it while needed; for a new password, a second box to confirm
    /// it, the rules and a strength label. With <see cref="PasswordRequest.Check"/>, OK checks the password with the
    /// dialog still open (unlocking takes a moment: the boxes are disabled meanwhile) and a wrong one is said there, so the
    /// person can try again. ShowDialog&lt;PasswordAnswer&gt; returns null when cancelled.
    /// </summary>
    public sealed class PasswordDialog : Window
    {
        private const char Mask = '●';

        private readonly PasswordRequest _request;
        private readonly TextBox _password;
        private readonly TextBox _confirm;
        private readonly TextBlock _strength;
        private readonly TextBlock _problem;
        private readonly CheckBox _remember;
        private readonly Button _ok;
        private readonly Button _reveal;
        private readonly SymbolIcon _revealIcon;
        private CancellationTokenSource _checking;
        private bool _closing;

        public PasswordDialog()
            : this(new PasswordRequest())
        {
        }

        public PasswordDialog(PasswordRequest request)
        {
            _request = request ?? new PasswordRequest();
            DialogChrome.Apply(this, _request.Title, 480);

            _password = PasswordBox(_request.IsNew ? "New password" : "Password");
            _password.Name = "PasswordBox";
            _revealIcon = new SymbolIcon { Symbol = Symbol.Eye, FontSize = 16 };
            _reveal = new Button
            {
                Name = "RevealButton",
                Content = _revealIcon,
                Padding = new Thickness(8, 4),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Stretch
            };
            AutomationProperties.SetName(_reveal, "Show the password");
            ToolTip.SetTip(_reveal, "Show the password");
            _reveal.Click += (_, _) => ToggleReveal();

            var passwordRow = new DockPanel();
            DockPanel.SetDock(_reveal, Dock.Right);
            passwordRow.Children.Add(_reveal);
            passwordRow.Children.Add(_password);

            _confirm = PasswordBox("Type it again");
            _confirm.Name = "ConfirmBox";
            _confirm.IsVisible = _request.IsNew;

            _strength = DialogChrome.Text(string.Empty, 12, FontWeight.Normal, "TextSecondary");
            _strength.IsVisible = false;
            _problem = DialogChrome.Text(string.Empty, 12, FontWeight.Normal, "DangerBrush");
            _problem.Name = "ProblemText";
            _problem.IsVisible = false;

            _remember = new CheckBox
            {
                Name = "RememberBox",
                Content = "Keep it unlocked until I lock it or close ByteExplorer",
                IsChecked = _request.Remember,
                IsVisible = _request.OfferRemember
            };

            _ok = DialogChrome.Button(_request.OkText ?? "OK", isDefault: true, isCancel: false, accent: true);
            _ok.Name = "OkButton";
            _ok.Click += (_, _) => AcceptAsync().Forget("password dialog OK");
            var cancel = DialogChrome.Button("Cancel", isDefault: false, isCancel: true, accent: false);
            cancel.Click += (_, _) => Cancel();

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
            buttons.Children.Add(_ok);
            buttons.Children.Add(cancel);

            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            titleRow.Children.Add(new SymbolIcon { Symbol = Symbol.LockClosed, FontSize = 22, VerticalAlignment = VerticalAlignment.Center });
            titleRow.Children.Add(DialogChrome.Text(_request.Title, 16, FontWeight.SemiBold));

            var body = new StackPanel { Margin = new Thickness(24, 20, 24, 20), Spacing = 12 };
            body.Children.Add(titleRow);
            if (!string.IsNullOrEmpty(_request.Message))
            {
                body.Children.Add(DialogChrome.Text(_request.Message, 13, FontWeight.Normal, "TextSecondary"));
            }

            body.Children.Add(passwordRow);
            body.Children.Add(_confirm);
            if (_request.IsNew)
            {
                body.Children.Add(DialogChrome.Text($"At least {ProtectedItems.MinPasswordLength} characters. A long phrase is stronger than a short word with symbols.", 12, FontWeight.Normal, "TextMuted"));
            }

            body.Children.Add(_strength);
            body.Children.Add(_problem);
            body.Children.Add(_remember);
            if (!string.IsNullOrEmpty(_request.Warning))
            {
                var warning = new Border
                {
                    Name = "WarningBox",
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(12, 10),
                    Child = new DockPanel
                    {
                        Children =
                        {
                            new SymbolIcon { Symbol = Symbol.Warning, FontSize = 16, Margin = new Thickness(0, 1, 10, 0), VerticalAlignment = VerticalAlignment.Top },
                            DialogChrome.Text(_request.Warning, 12, FontWeight.Normal, "TextPrimary")
                        }
                    }
                };
                warning[!Border.BackgroundProperty] = warning.GetResourceObservable("SettingsInfoBarBrush").ToBinding();
                body.Children.Add(warning);
            }

            body.Children.Add(buttons);
            Content = body;

            _password.TextChanged += (_, _) => Validate();
            _confirm.TextChanged += (_, _) => Validate();
            Opened += (_, _) => _password.Focus();
            Closing += (_, e) =>
            {
                if (!_closing)
                {
                    _checking?.Cancel(); // closed by the window's X: stop a check in progress
                }
            };
            Validate();
        }

        private TextBox PasswordBox(string watermark)
        {
            var box = new TextBox { PasswordChar = Mask, Watermark = watermark, MinHeight = 32, RevealPassword = false };
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    if (_request.IsNew && ReferenceEquals(box, _password) && string.IsNullOrEmpty(_confirm.Text))
                    {
                        _confirm.Focus();
                        return;
                    }

                    AcceptAsync().Forget("password dialog Enter");
                }
            };

            // Copying a password out of the box would put it on the clipboard: not offered, as in system password boxes.
            box.CopyingToClipboard += (_, e) => e.Handled = true;
            box.CuttingToClipboard += (_, e) => e.Handled = true;
            return box;
        }

        private void ToggleReveal()
        {
            bool reveal = !_password.RevealPassword;
            _password.RevealPassword = reveal;
            _confirm.RevealPassword = reveal;
            _revealIcon.Symbol = reveal ? Symbol.EyeOff : Symbol.Eye;
            ToolTip.SetTip(_reveal, reveal ? "Hide the password" : "Show the password");
            AutomationProperties.SetName(_reveal, reveal ? "Hide the password" : "Show the password");
        }

        /// <summary>What stops OK, or null; empty boxes disable OK without a message.</summary>
        private string Problem(out bool silent)
        {
            silent = false;
            string password = _password.Text ?? string.Empty;
            if (password.Length == 0)
            {
                silent = true;
                return string.Empty;
            }

            if (!_request.IsNew)
            {
                return null;
            }

            string rule = ProtectedItems.PasswordProblem(password);
            if (rule != null)
            {
                return rule;
            }

            string confirm = _confirm.Text ?? string.Empty;
            if (confirm.Length == 0)
            {
                silent = true;
                return string.Empty;
            }

            return string.Equals(password, confirm, StringComparison.Ordinal) ? null : "The two passwords don't match.";
        }

        private void Validate()
        {
            string problem = Problem(out bool silent);
            _ok.IsEnabled = problem == null && _checking == null;
            ShowProblem(silent ? null : problem);
            if (_request.IsNew)
            {
                string strength = ProtectedItems.Strength(_password.Text);
                _strength.Text = strength.Length == 0 ? string.Empty : "Strength: " + strength;
                _strength.IsVisible = strength.Length > 0;
            }
        }

        private void ShowProblem(string problem)
        {
            _problem.Text = problem ?? string.Empty;
            _problem.IsVisible = !string.IsNullOrEmpty(problem);
        }

        private async Task AcceptAsync()
        {
            if (_checking != null || Problem(out _) != null)
            {
                return;
            }

            string password = _password.Text;
            var answer = new PasswordAnswer { Password = password, Remember = _request.OfferRemember && _remember.IsChecked == true };
            if (_request.Check == null)
            {
                Finish(answer);
                return;
            }

            var cts = new CancellationTokenSource();
            _checking = cts;
            SetBusy(true);
            string error;
            try
            {
                error = await _request.Check(password, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "The password check failed");
                error = ex.Message;
            }
            finally
            {
                _checking = null;
                cts.Dispose();
            }

            if (_closing || !IsVisible)
            {
                return;
            }

            SetBusy(false);
            if (error == null)
            {
                Finish(answer);
                return;
            }

            ShowProblem(error);
            _password.Focus();
            _password.SelectAll();
        }

        private void SetBusy(bool busy)
        {
            _password.IsEnabled = !busy;
            _confirm.IsEnabled = !busy;
            _remember.IsEnabled = !busy;
            _ok.IsEnabled = !busy && Problem(out _) == null;
            if (busy)
            {
                _problem[!TextBlock.ForegroundProperty] = _problem.GetResourceObservable("TextSecondary").ToBinding();
                ShowProblem("Checking the password…");
            }
            else
            {
                _problem[!TextBlock.ForegroundProperty] = _problem.GetResourceObservable("DangerBrush").ToBinding();
                ShowProblem(null);
            }
        }

        private void Finish(PasswordAnswer answer)
        {
            _closing = true;
            _password.Text = string.Empty;
            _confirm.Text = string.Empty;
            Close(answer);
        }

        private void Cancel()
        {
            _checking?.Cancel();
            _closing = true;
            Close(null);
        }
    }
}
