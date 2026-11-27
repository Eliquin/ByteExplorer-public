// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ByteExplorer.Infrastructure;
using Serilog;

namespace ByteExplorer.Views.Dialogs
{
    /// <summary>
    /// The Hashes tab: MD5, SHA-1 and SHA-256 of a file, computed on request in one pass over the file (all three
    /// hashes fed from the same buffer), off the UI thread, with progress, cancellable (the button, or closing the window).
    /// A compare box tells whether a pasted checksum matches any of them.
    /// </summary>
    public sealed partial class PropertiesWindow
    {
        private const int HashBufferSize = 1 << 20;
        private Button _hashButton;
        private ProgressBar _hashProgress;
        private TextBlock _hashStatus;
        private SelectableTextBlock _md5Text;
        private SelectableTextBlock _sha1Text;
        private SelectableTextBlock _sha256Text;
        private TextBox _compareBox;
        private TextBlock _compareResult;
        private CancellationTokenSource _hashCts;

        private Control BuildHashes()
        {
            var list = new PropertyList();
            TextBlock intro = DialogChrome.Text("Checksums identify the file's exact contents. Compare them with the ones a download page publishes.", 12, FontWeight.Normal, "TextSecondary");
            intro.Margin = new Thickness(0, 0, 0, 12);
            list.Wide(intro);

            _md5Text = HashValue("Md5Value");
            _sha1Text = HashValue("Sha1Value");
            _sha256Text = HashValue("Sha256Value");
            list.Row("MD5", WithCopy(_md5Text, "Copy MD5"));
            list.Row("SHA-1", WithCopy(_sha1Text, "Copy SHA-1"));
            list.Row("SHA-256", WithCopy(_sha256Text, "Copy SHA-256"));

            _hashButton = DialogChrome.Button("Calculate", isDefault: false, isCancel: false, accent: false);
            _hashButton.Name = "HashButton";
            _hashButton.Click += (_, _) =>
            {
                if (_hashCts != null)
                {
                    _hashCts.Cancel();
                }
                else
                {
                    HashTask = ComputeHashesAsync();
                }
            };
            _hashProgress = new ProgressBar { Minimum = 0, Maximum = 1, Height = 4, MinHeight = 4, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
            _hashStatus = DialogChrome.Text(string.Empty, 11, FontWeight.Normal, "TextMuted");
            _hashStatus.Name = "HashStatus";
            _hashStatus.VerticalAlignment = VerticalAlignment.Center;
            var progress = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            progress.Children.Add(_hashProgress);
            progress.Children.Add(_hashStatus);
            var actions = new DockPanel { Margin = new Thickness(0, 4, 0, 14) };
            DockPanel.SetDock(_hashButton, Dock.Left);
            actions.Children.Add(_hashButton);
            actions.Children.Add(progress);
            list.Wide(actions);

            list.Separator();
            _compareBox = new TextBox { Name = "CompareBox", Watermark = "Paste a checksum to compare", FontSize = 12 };
            _compareBox.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBox.TextProperty)
                {
                    UpdateCompare();
                }
            };
            list.Row("Compare", _compareBox);
            _compareResult = DialogChrome.Text(string.Empty, 12, FontWeight.SemiBold, "TextSecondary");
            _compareResult.Name = "CompareResult";
            _compareResult.IsVisible = false;
            list.Row(string.Empty, _compareResult);
            return list.Grid;
        }

        private static SelectableTextBlock HashValue(string name)
        {
            SelectableTextBlock block = Value("Not calculated", name);
            block.FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, SF Mono, DejaVu Sans Mono, Liberation Mono, monospace");
            block.FontSize = 11;
            block.TextWrapping = TextWrapping.Wrap;
            return block;
        }

        private async Task ComputeHashesAsync()
        {
            string path = _path;
            var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
            _hashCts = cts;
            CancellationToken token = cts.Token;
            _hashButton.Content = "Cancel";
            _hashProgress.Value = 0;
            _hashProgress.IsVisible = true;
            _hashStatus.Text = "Reading…";
            _md5Text.Text = _sha1Text.Text = _sha256Text.Text = "Calculating…";
            UpdateCompare();
            try
            {
                long lastPublish = 0;
                (string md5, string sha1, string sha256) = await Task.Run(() =>
                {
                    using var md5Hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                    using var sha1Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                    using var sha256Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(HashBufferSize);
                    try
                    {
                        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
                        long length = stream.Length;
                        long done = 0;
                        int read;
                        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            token.ThrowIfCancellationRequested();
                            md5Hash.AppendData(buffer, 0, read);
                            sha1Hash.AppendData(buffer, 0, read);
                            sha256Hash.AppendData(buffer, 0, read);
                            done += read;
                            long now = Environment.TickCount64;
                            if (now - lastPublish >= UpdateIntervalMs)
                            {
                                lastPublish = now;
                                double fraction = length > 0 ? Math.Min(1, (double)done / length) : 1;
                                long d = done;
                                UiThread.Post(() =>
                                {
                                    // A late update of a finished (or newer) run is dropped.
                                    if (!token.IsCancellationRequested && !_closed && ReferenceEquals(_hashCts, cts))
                                    {
                                        _hashProgress.Value = fraction;
                                        _hashStatus.Text = $"{FormatSize(d)} of {FormatSize(length)}";
                                    }
                                });
                            }
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }

                    return (Convert.ToHexStringLower(md5Hash.GetHashAndReset()), Convert.ToHexStringLower(sha1Hash.GetHashAndReset()),
                        Convert.ToHexStringLower(sha256Hash.GetHashAndReset()));
                }, token);

                if (_closed)
                {
                    return;
                }

                _md5Text.Text = md5;
                _sha1Text.Text = sha1;
                _sha256Text.Text = sha256;
                _hashStatus.Text = "Done";
            }
            catch (OperationCanceledException)
            {
                if (!_closed)
                {
                    _md5Text.Text = _sha1Text.Text = _sha256Text.Text = "Not calculated";
                    _hashStatus.Text = "Cancelled";
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is CryptographicException || ex is PlatformNotSupportedException)
            {
                Log.Logger.Debug(ex, "Hashing {Path} failed", path);
                if (!_closed)
                {
                    _md5Text.Text = _sha1Text.Text = _sha256Text.Text = "Not calculated";
                    _hashStatus.Text = "Couldn't read the file: " + ex.Message;
                }
            }
            finally
            {
                _hashCts = null;
                cts.Dispose();
                if (!_closed)
                {
                    _hashButton.Content = "Calculate";
                    _hashProgress.IsVisible = false;
                    UpdateCompare();
                }
            }
        }

        private void UpdateCompare()
        {
            if (_compareBox == null)
            {
                return;
            }

            string wanted = (_compareBox.Text ?? string.Empty).Trim().Replace(" ", string.Empty, StringComparison.Ordinal);
            if (wanted.Length == 0)
            {
                _compareResult.IsVisible = false;
                return;
            }

            string match = null;
            if (string.Equals(wanted, _md5Text.Text, StringComparison.OrdinalIgnoreCase)) match = "MD5";
            else if (string.Equals(wanted, _sha1Text.Text, StringComparison.OrdinalIgnoreCase)) match = "SHA-1";
            else if (string.Equals(wanted, _sha256Text.Text, StringComparison.OrdinalIgnoreCase)) match = "SHA-256";

            bool calculated = _hashCts == null && _sha256Text.Text != null && _sha256Text.Text.Length == 64;
            string brush = match != null ? "CheckGreen" : calculated ? "DangerBrush" : "TextSecondary";
            _compareResult.Text = match != null ? $"Matches the {match} checksum" : calculated ? "Does not match any checksum" : "Calculate the checksums to compare";
            _compareResult[!TextBlock.ForegroundProperty] = _compareResult.GetResourceObservable(brush).ToBinding();
            _compareResult.IsVisible = true;
        }
    }
}
