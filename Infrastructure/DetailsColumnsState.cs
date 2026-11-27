// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Controls;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Services;
using Serilog;

namespace ByteExplorer.Infrastructure
{
    /// <summary>
    /// The Details view's columns for the whole app: every pane and tab shows the same ones, as chosen from a column
    /// header's menu (Explorer remembers them per folder type; one set is simpler and is what most people want). Kept in
    /// the settings (FileList.DetailsColumns), saved a moment after the last change, so dragging a column edge doesn't
    /// write the file on every pixel. UI thread.
    /// </summary>
    public static class DetailsColumnsState
    {
        private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(600);

        private static ISettingsService s_settings;
        private static DetailsColumnLayout s_current;
        private static CancellationTokenSource s_pendingSave;

        /// <summary>The columns changed (any pane): every Details header and list takes the new layout.</summary>
        public static event EventHandler Changed;

        /// <summary>The columns shown now.</summary>
        public static DetailsColumnLayout Current => s_current ??= FromSettings();

        /// <summary>Reads the saved columns (at start, once the settings are loaded) and tells the views.</summary>
        public static void Attach(ISettingsService settings)
        {
            s_settings = settings;
            s_current = FromSettings();
            Changed?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>Shows other columns or widths everywhere, and saves them (shortly).</summary>
        public static void Set(DetailsColumnLayout layout)
        {
            if (layout == null || ReferenceEquals(layout, s_current))
            {
                return;
            }

            s_current = layout;
            Changed?.Invoke(null, EventArgs.Empty);
            SaveSoon(layout);
        }

        /// <summary>The default columns again (the menu's "Reset columns").</summary>
        public static void Reset() => Set(DetailsColumnLayout.Default);

        private static DetailsColumnLayout FromSettings()
        {
            System.Collections.Generic.List<DetailsColumnEntry> saved = s_settings?.Current?.FileList?.DetailsColumns;
            return saved == null || saved.Count == 0 ? DetailsColumnLayout.Default : DetailsColumnLayout.FromEntries(saved);
        }

        private static void SaveSoon(DetailsColumnLayout layout)
        {
            if (s_settings?.Current?.FileList == null)
            {
                return;
            }

            s_settings.Current.FileList.DetailsColumns = ReferenceEquals(layout, DetailsColumnLayout.Default)
                ? new System.Collections.Generic.List<DetailsColumnEntry>()
                : layout.ToEntries();
            s_pendingSave?.Cancel();
            var cts = new CancellationTokenSource();
            s_pendingSave = cts;
            SaveAfterDelayAsync(cts.Token).Forget("save the Details columns");
        }

        private static async Task SaveAfterDelayAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(SaveDelay, token);
                await s_settings.SaveAsync(token);
            }
            catch (OperationCanceledException)
            {
                // A newer change will save.
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "Could not save the Details columns");
            }
        }
    }
}
