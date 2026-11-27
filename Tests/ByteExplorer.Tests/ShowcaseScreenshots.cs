// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels;
using SkiaSharp;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// The README's showcase screenshots (docs/screenshots), rendered by the real app on a demo home folder. Runs only
    /// when BYTEEXPLORER_SHOWCASE_DIR names the output folder (and, for tidy paths, with HOME pointing at a demo home such
    /// as /home/alex); otherwise it does nothing, so the normal test run is unaffected.
    /// <code>HOME=/home/alex BYTEEXPLORER_SHOWCASE_DIR=docs/screenshots dotnet test --filter Showcase</code>
    /// </summary>
    [Collection(GlobalStateCollection.Name)]
    public sealed class ShowcaseScreenshots
    {
        private static readonly string OutputDirectory = Environment.GetEnvironmentVariable("BYTEEXPLORER_SHOWCASE_DIR");

        [AvaloniaFact]
        public async Task Showcase()
        {
            if (string.IsNullOrEmpty(OutputDirectory))
            {
                return;
            }

            Directory.CreateDirectory(OutputDirectory);
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            DemoHome.Create(home);
            Core.Platform.MountedVolumes.HideForTests = mount => mount != "/"; // the build machine's own mounts aren't a desktop's
            try
            {

            await ShootAsync("Trove", "Light", FileListViewPreset.Details, Path.Combine(home, "Documents"), "trove-details.png");
            await ShootAsync("Folio", "Light", FileListViewPreset.Columns, Path.Combine(home, "Documents", "Projects"), "folio-columns.png",
                selectInColumns: true);
            await ShootAsync("Modern", "Light", FileListViewPreset.Timeline, Path.Combine(home, "Pictures"), "modern-light-timeline.png");
            await ShootAsync("Modern", "Dark", FileListViewPreset.Timeline, Path.Combine(home, "Pictures"), "modern-dark-timeline.png");
            await ShootTabsOnTopAsync(home, "modern-dark-details-tabs.png");
            await ShootLensAsync(home, "modern-light-lens-tabs-left.png");
            await ShootSettingsAsync(Path.Combine(home, "Documents"), "settings.png");
            }
            finally
            {
                Core.Platform.MountedVolumes.HideForTests = null;
                App.ApplyTheme("System");
            }
        }

        private static async Task<MainWindowBase> OpenAsync(string layout, string theme, FileListViewPreset preset, string folder)
        {
            MainWindowBase window = App.CreateWindowForLayout(layout);
            window.Width = 1360;
            window.Height = 860;
            window.Show();
            await Pump(40);
            MainViewModel main = window.ViewModel;
            main.SettingsService.Current.Appearance.Theme = theme; // the settings page shows the theme the window has
            App.ApplyTheme(theme);
            foreach (var pin in main.Pins.Items.ToList())
            {
                await main.Pins.UnpinAsync(pin);
            }

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (string place in new[] { "Desktop", "Documents", "Downloads", "Pictures", "Music", "Videos" })
            {
                await main.Pins.PinAsync(Path.Combine(home, place));
            }

            main.IsDetailsPaneVisible = false;
            FileListViewModel pane = main.FileListViewModel;
            TagDocuments(main, Path.Combine(home, "Documents"));

            // "Recent" from the demo's folders, not from the test runs.
            main.ClearActivityData();
            foreach (string visited in new[] { Path.Combine(home, "Documents", "Projects", "Website redesign"), Path.Combine(home, "Documents"), Path.Combine(home, "Documents", "Projects") })
            {
                for (int i = 0; i < 3; i++)
                {
                    await pane.LoadFolderAsync(visited);
                }
            }

            pane.SelectedPreset = preset;
            await pane.LoadFolderAsync(folder);
            await Pump(40);
            if (window.FindControl<ScrollViewer>("SidebarScroller") is ScrollViewer sidebar)
            {
                sidebar.Offset = default; // the panel from its top: Home, Quick access, Lens
            }

            await Pump(10);
            return window;
        }

        private static async Task ShootAsync(string layout, string theme, FileListViewPreset preset, string folder, string name, bool selectInColumns = false)
        {
            MainWindowBase window = await OpenAsync(layout, theme, preset, folder);
            try
            {
                FileListViewModel pane = window.ViewModel.FileListViewModel;
                if (selectInColumns)
                {
                    // Click down the path as a person would: Projects › Website redesign › Assets › hero.png.
                    foreach (string step in new[] { "Website redesign", "Assets", "hero.png" })
                    {
                        ColumnPane column = pane.ColumnPanes?.LastOrDefault();
                        FileListItemViewModel item = column?.Items?.OfType<FileListItemViewModel>().FirstOrDefault(i => i.Name == step);
                        if (item == null)
                        {
                            break;
                        }

                        column.SelectedItems.Clear();
                        column.SelectedItems.Add(item);
                        await pane.OnColumnSelectionChangedAsync(column, new[] { item });
                        await Pump(20);
                    }
                }
                else if (preset == FileListViewPreset.Details)
                {

                    FileListItemViewModel report = pane.Items?.OfType<FileListItemViewModel>().FirstOrDefault(i => i.Name.StartsWith("Annual report", StringComparison.Ordinal));
                    if (report != null)
                    {
                        pane.SelectedItem = report;
                    }
                }

                await Pump(60); // thumbnails and icons arrive
                Save(window, name);
            }
            finally
            {
                UntagDocuments(window.ViewModel);
                window.CloseWithoutAsking();
                await Pump(5);
            }
        }

        /// <summary>The dark Modern layout, Details view, the tabs in the title bar (Settings › Tabs › Title bar) with a few tabs open.</summary>
        private static async Task ShootTabsOnTopAsync(string home, string name)
        {
            string documents = Path.Combine(home, "Documents");
            MainWindowBase window = await OpenAsync("Modern", "Dark", FileListViewPreset.Details, documents);
            MainViewModel main = window.ViewModel;
            string placement = main.SettingsService.Current.Tabs.Placement;
            try
            {
                main.SettingsService.Current.Tabs.Placement = Core.Models.TabSettings.PlacementTitleBar; // the very top, in the title bar (as Windows 11)
                main.Tabs.NotifySettingsChanged();
                foreach (string folder in new[] { Path.Combine(home, "Pictures"), Path.Combine(documents, "Projects"), Path.Combine(home, "Downloads") })
                {
                    await main.Tabs.OpenInNewTabAsync(folder, select: false);
                }

                await Pump(30);
                FileListViewModel pane = main.FileListViewModel;
                FileListItemViewModel report = pane.Items?.OfType<FileListItemViewModel>().FirstOrDefault(i => i.Name.StartsWith("Annual report", StringComparison.Ordinal));
                if (report != null)
                {
                    pane.SelectedItem = report;
                }

                await Pump(60);
                Save(window, name);
            }
            finally
            {
                main.SettingsService.Current.Tabs.Placement = placement;
                main.Tabs.NotifySettingsChanged();
                UntagDocuments(main);
                window.CloseWithoutAsking();
                await Pump(5);
            }
        }

        /// <summary>The light Modern layout with the tabs on the left (Settings › Tabs › Left) showing the Photos lens.</summary>
        private static async Task ShootLensAsync(string home, string name)
        {
            MainWindowBase window = await OpenAsync("Modern", "Light", FileListViewPreset.Details, Path.Combine(home, "Documents"));
            MainViewModel main = window.ViewModel;
            string placement = main.SettingsService.Current.Tabs.Placement;
            try
            {
                main.SettingsService.Current.Tabs.Placement = Core.Models.TabSettings.PlacementLeft;
                main.Tabs.NotifySettingsChanged();
                foreach (string folder in new[] { Path.Combine(home, "Pictures"), Path.Combine(home, "Documents", "Projects"), Path.Combine(home, "Downloads") })
                {
                    await main.Tabs.OpenInNewTabAsync(folder, select: false);
                }

                main.Lens.UseSourceForTests(new DemoLensSource(home));
                await main.OpenLensAsync(main.Lens.Lenses.First(l => l.Definition.Id == "photos"));
                await Pump(80);
                Save(window, name);
            }
            finally
            {
                main.SettingsService.Current.Tabs.Placement = placement;
                main.Tabs.NotifySettingsChanged();
                UntagDocuments(main);
                window.CloseWithoutAsking();
                await Pump(5);
            }
        }

        /// <summary>The lenses' search over the demo home's real files (the build machine's index would be anything).</summary>
        private sealed class DemoLensSource : ViewModels.Lens.ILensSource
        {
            private readonly string _home;

            public DemoLensSource(string home)
            {
                _home = home;
            }

            public bool IsReady => true;

            public event EventHandler Changed
            {
                add { }
                remove { }
            }

            public Task<ByteSearcher.SearchResults> SearchAsync(string query, ByteSearcher.SearchOptions options, System.Threading.CancellationToken cancellationToken)
            {
                string q = query ?? string.Empty;
                var hits = new System.Collections.Generic.List<ByteSearcher.SearchHit>();
                int id = 0;
                foreach (FileInfo file in new DirectoryInfo(_home).EnumerateFiles("*", SearchOption.AllDirectories).OrderByDescending(f => f.LastWriteTimeUtc))
                {
                    Core.FileKind kind = Core.FileKinds.Of(file.Name);
                    bool wanted = q.Contains("kind:image", StringComparison.Ordinal) ? kind == Core.FileKind.Image
                        : q.Contains("kind:document", StringComparison.Ordinal) ? kind == Core.FileKind.Document
                        : !q.Contains("kind:", StringComparison.Ordinal);
                    if (wanted)
                    {
                        hits.Add(new ByteSearcher.SearchHit(id++, file.Name, file.FullName, false, false, file.Length, file.LastWriteTimeUtc, kind, Array.Empty<ByteSearcher.TextRange>()));
                    }
                }

                return Task.FromResult(ByteSearcher.SearchResults.FromHits(hits, q, options ?? new ByteSearcher.SearchOptions(), TimeSpan.Zero));
            }
        }

        private static async Task ShootSettingsAsync(string folder, string name)
        {
            MainWindowBase window = await OpenAsync("Modern", "Light", FileListViewPreset.Details, folder);
            try
            {
                window.OpenSettings();
                await Pump(20);
                var settings = window.SettingsWindowForTests;
                settings.Width = 1100;
                settings.Height = 760;
                settings.ViewModel.SelectedPage = settings.ViewModel.Pages.First(p => p.Key == "appearance");
                await Pump(30);
                Save(settings, name);
                settings.Close();
            }
            finally
            {
                UntagDocuments(window.ViewModel);
                window.CloseWithoutAsking();
                await Pump(5);
            }
        }

        /// <summary>Two tags on a few documents, so the Details view's Tags column and the sidebar's Tags section show.</summary>
        private static void TagDocuments(MainViewModel main, string documents)
        {
            Core.Tags.TagStore store = main.Tags;
            foreach (string name in new[] { "Work", "Personal" })
            {
                if (store.FindByFullName(name) is Core.Tags.TagDefinition old)
                {
                    store.DeleteTag(old.Id);
                }
            }

            Core.Tags.TagDefinition work = store.CreateTag("Work", "Blue");
            Core.Tags.TagDefinition personal = store.CreateTag("Personal", "Green");
            store.Assign(new[] { "Annual report 2026.docx", "Budget plan.xlsx", "Product launch.pptx", "Projects" }.Select(n => Path.Combine(documents, n)), work.Id);
            store.Assign(new[] { "Lease agreement.pdf", "Travel checklist.txt", "Passport scan.jpg", "Recipes" }.Select(n => Path.Combine(documents, n)), personal.Id);
        }

        /// <summary>Removes the demo tags (the tests' tag file is shared).</summary>
        private static void UntagDocuments(MainViewModel main)
        {
            foreach (string name in new[] { "Work", "Personal" })
            {
                if (main.Tags.FindByFullName(name) is Core.Tags.TagDefinition tag)
                {
                    main.Tags.DeleteTag(tag.Id);
                }
            }
        }

        private static void Save(Window window, string name)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame()?.Save(Path.Combine(OutputDirectory, name));
        }

        private static async Task Pump(int rounds)
        {
            for (int i = 0; i < rounds; i++)
            {
                await Task.Delay(20);
                Dispatcher.UIThread.RunJobs();
            }
        }

        /// <summary>A believable home folder: documents, projects, photos spread over the last months, music, videos.</summary>
        private static class DemoHome
        {
            public static void Create(string home)
            {
                DateTime now = DateTime.Now;
                string documents = Folder(home, "Documents");
                foreach (string place in new[] { "Desktop", "Downloads", "Music", "Videos" })
                {
                    Folder(home, place);
                }

                Folder(documents, "Invoices 2026", now.AddDays(-3));
                Folder(documents, "Taxes", now.AddDays(-40));
                Folder(documents, "Recipes", now.AddDays(-90));
                string projects = Folder(documents, "Projects", now.AddDays(-1));
                File(documents, "Annual report 2026.docx", 284_310, now.AddHours(-2));
                File(documents, "Budget plan.xlsx", 96_512, now.AddDays(-1));
                File(documents, "Lease agreement.pdf", 1_204_880, now.AddDays(-12));
                File(documents, "Meeting notes.md", 6_204, now.AddMinutes(-35));
                File(documents, "Product launch.pptx", 4_830_120, now.AddDays(-6));
                File(documents, "Passport scan.jpg", 2_310_412, now.AddDays(-70));
                File(documents, "Travel checklist.txt", 1_418, now.AddDays(-20));
                File(documents, "Résumé.pdf", 188_204, now.AddDays(-150));

                string website = Folder(projects, "Website redesign", now.AddHours(-5));
                Folder(projects, "Mobile app", now.AddDays(-9));
                Folder(projects, "Brand guidelines", now.AddDays(-30));
                File(projects, "Roadmap.xlsx", 41_220, now.AddDays(-2));
                string assets = Folder(website, "Assets", now.AddHours(-6));
                Folder(website, "Drafts", now.AddDays(-4));
                File(website, "index.html", 18_402, now.AddHours(-5));
                File(website, "styles.css", 9_877, now.AddHours(-5));
                File(website, "app.js", 22_310, now.AddHours(-7));
                File(website, "Wireframes.pdf", 3_120_550, now.AddDays(-3));
                Image(assets, "hero.png", now.AddHours(-6), new SKColor(0x3a, 0x7b, 0xd5), new SKColor(0x9b, 0x5d, 0xe5));
                Image(assets, "logo.png", now.AddDays(-2), new SKColor(0xff, 0x8a, 0x3d), new SKColor(0xff, 0xd1, 0x66));

                string pictures = Folder(home, "Pictures");
                var palette = new (string Name, int DaysAgo, SKColor A, SKColor B)[]
                {
                    ("Sunrise over the lake.jpg", 0, new SKColor(0xff, 0x9a, 0x55), new SKColor(0x6a, 0x5a, 0xcd)),
                    ("Morning coffee.jpg", 0, new SKColor(0x8b, 0x5e, 0x3c), new SKColor(0xe8, 0xc9, 0xa0)),
                    ("City lights.jpg", 1, new SKColor(0x1d, 0x26, 0x71), new SKColor(0xf7, 0x9d, 0x00)),
                    ("Forest trail.jpg", 1, new SKColor(0x1f, 0x6f, 0x43), new SKColor(0xa8, 0xe0, 0x63)),
                    ("Beach day.jpg", 3, new SKColor(0x00, 0xb4, 0xd8), new SKColor(0xff, 0xe6, 0xa7)),
                    ("Mountain view.jpg", 4, new SKColor(0x48, 0x5a, 0x7a), new SKColor(0xdc, 0xe6, 0xf0)),
                    ("Birthday cake.jpg", 9, new SKColor(0xff, 0x6f, 0x91), new SKColor(0xff, 0xf0, 0xf5)),
                    ("Garden flowers.jpg", 10, new SKColor(0xc1, 0x2f, 0x6b), new SKColor(0xf9, 0xd4, 0x4a)),
                    ("Snowy morning.jpg", 24, new SKColor(0x9f, 0xb8, 0xd6), new SKColor(0xff, 0xff, 0xff)),
                    ("Road trip.jpg", 26, new SKColor(0xd9, 0x48, 0x2b), new SKColor(0x2b, 0x2d, 0x42)),
                    ("Autumn leaves.jpg", 45, new SKColor(0xc4, 0x5c, 0x26), new SKColor(0xf2, 0xb1, 0x34)),
                    ("Night sky.jpg", 47, new SKColor(0x0b, 0x10, 0x33), new SKColor(0x5b, 0x3f, 0x9a)),
                };
                foreach ((string name, int days, SKColor a, SKColor b) in palette)
                {
                    Image(pictures, name, now.AddDays(-days).AddHours(-1), a, b);
                }

                // Older camera pictures in Downloads, across the year, so the Photos lens's time river has its months.
                string downloads = Path.Combine(home, "Downloads");
                int[] ages = { 62, 70, 95, 101, 128, 131, 140, 158, 190, 196, 222, 250, 255, 284, 300, 340 };
                for (int i = 0; i < ages.Length; i++)
                {
                    (string _, int _, SKColor a, SKColor b) = palette[i % palette.Length];
                    Image(downloads, $"IMG_{2040 + i:0000}.jpg", now.AddDays(-ages[i]).AddHours(-3), b, a);
                }
            }

            private static string Folder(string parent, string name, DateTime? modified = null)
            {
                string path = Path.Combine(parent, name);
                Directory.CreateDirectory(path);
                if (modified.HasValue)
                {
                    Directory.SetLastWriteTime(path, modified.Value);
                }

                return path;
            }

            private static void File(string folder, string name, long size, DateTime modified)
            {
                string path = Path.Combine(folder, name);
                using (var stream = new FileStream(path, FileMode.Create))
                {
                    stream.SetLength(size);
                }

                System.IO.File.SetLastWriteTime(path, modified);
            }

            /// <summary>A soft two-color gradient with a few circles, as a photo stand-in that thumbnails nicely.</summary>
            private static void Image(string folder, string name, DateTime modified, SKColor from, SKColor to)
            {
                string path = Path.Combine(folder, name);
                using (var bitmap = new SKBitmap(640, 420))
                using (var canvas = new SKCanvas(bitmap))
                {
                    using (var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(640, 420), new[] { from, to }, SKShaderTileMode.Clamp))
                    using (var paint = new SKPaint { Shader = shader })
                    {
                        canvas.DrawRect(0, 0, 640, 420, paint);
                    }

                    var random = new Random(name.GetHashCode(StringComparison.Ordinal) & 0x7fffffff);
                    using (var circle = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 46) })
                    {
                        for (int i = 0; i < 5; i++)
                        {
                            canvas.DrawCircle(random.Next(40, 600), random.Next(40, 380), random.Next(30, 140), circle);
                        }
                    }

                    using (SKData data = bitmap.Encode(name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg, 88))
                    using (FileStream file = System.IO.File.Create(path))
                    {
                        data.SaveTo(file);
                    }
                }

                System.IO.File.SetLastWriteTime(path, modified);
            }
        }
    }
}
