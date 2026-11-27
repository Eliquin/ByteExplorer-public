// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace ByteExplorer.Core.Models
{
    /// <summary>
    /// Root of everything persisted to settings.json. Grouped into
    /// nested sections (Navigation, Appearance) rather than one flat
    /// bag of properties, so the file stays readable as it grows and a
    /// future settings window can bind to each section independently.
    ///
    /// SchemaVersion exists from day one even though nothing reads it
    /// yet — it's the hook a future migration step needs if a field is
    /// ever renamed or restructured, so old settings.json files don't
    /// just silently lose data.
    /// </summary>
    public sealed class AppSettings
    {
        /// <summary>The settings file shape this build writes; older files are brought up to it by <see cref="Migrate"/>.</summary>
        public const int CurrentSchemaVersion = 3;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public NavigationSettings Navigation { get; set; } = new NavigationSettings();

        public AppearanceSettings Appearance { get; set; } = new AppearanceSettings();

        public FileListSettings FileList { get; set; } = new FileListSettings();

        public AnalyticsSettings Analytics { get; set; } = new AnalyticsSettings();

        public IndexSettings Index { get; set; } = new IndexSettings();

        public GreetingSettings Greeting { get; set; } = new GreetingSettings();

        public FolderOptionsSettings FolderOptions { get; set; } = new FolderOptionsSettings();

        public TabSettings Tabs { get; set; } = new TabSettings();

        /// <summary>
        /// Brings a file written by an older build up to <see cref="CurrentSchemaVersion"/>, once (the version is saved).
        /// Version 2: the greeting and goodbye toasts became off by default. Files before it hold true for both because
        /// that was the default, not necessarily a choice, so they are switched off once; a later choice is kept.
        /// Version 3: the tab bar's default became the title bar (Modern layout; the others show it on top). Files before
        /// it hold "Top", which was the default and the only horizontal choice then: it moves to the title bar once.
        /// </summary>
        public void Migrate()
        {
            if (SchemaVersion < 2)
            {
                Greeting ??= new GreetingSettings();
                Greeting.EnableGreeting = false;
                Greeting.EnableGoodbye = false;
            }

            if (SchemaVersion < 3 && Tabs != null && Tabs.Placement == TabSettings.PlacementTop)
            {
                Tabs.Placement = TabSettings.PlacementTitleBar;
            }

            if (SchemaVersion < CurrentSchemaVersion)
            {
                SchemaVersion = CurrentSchemaVersion;
            }
        }

        /// <summary>
        /// Called after every load (including a fresh default instance)
        /// so a hand-edited or corrupt-in-part settings.json can't put
        /// the app into a broken state.
        /// </summary>
        public void Sanitize()
        {
            Navigation = Navigation ?? new NavigationSettings();
            Appearance = Appearance ?? new AppearanceSettings();
            FileList = FileList ?? new FileListSettings();
            Analytics = Analytics ?? new AnalyticsSettings();
            Index = Index ?? new IndexSettings();
            Greeting = Greeting ?? new GreetingSettings();
            FolderOptions = FolderOptions ?? new FolderOptionsSettings();
            Tabs = Tabs ?? new TabSettings();
            Navigation.Sanitize();
            Appearance.Sanitize();
            FileList.Sanitize();
            Analytics.Sanitize();
            Index.Sanitize();
            Greeting.Sanitize();
            FolderOptions.Sanitize();
            Tabs.Sanitize();
        }
    }
}
