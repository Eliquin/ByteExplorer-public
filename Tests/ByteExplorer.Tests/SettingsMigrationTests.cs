// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using System.Threading.Tasks;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Services;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>The greeting and goodbye are off by default; files from before that change are switched off once.</summary>
    public sealed class SettingsMigrationTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "be-settings-" + Guid.NewGuid().ToString("N"));

        public SettingsMigrationTests() => Directory.CreateDirectory(_folder);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_folder, true);
            }
            catch (IOException)
            {
            }
        }

        [Fact]
        public void GreetingAndGoodbye_AreOffByDefault()
        {
            var settings = new AppSettings();
            Assert.False(settings.Greeting.EnableGreeting);
            Assert.False(settings.Greeting.EnableGoodbye);
            Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
        }

        [Fact]
        public async Task AnOlderFile_HasThemSwitchedOffOnce_AndIsSavedAtTheNewVersion()
        {
            string path = Path.Combine(_folder, "settings.json");
            File.WriteAllText(path, "{ \"SchemaVersion\": 1, \"Greeting\": { \"EnableGreeting\": true, \"EnableGoodbye\": true, \"GoodbyeDwellMs\": 2500 } }");

            var service = new SettingsService(path);
            await service.LoadAsync();
            Assert.False(service.Current.Greeting.EnableGreeting);
            Assert.False(service.Current.Greeting.EnableGoodbye);
            Assert.Equal(2500, service.Current.Greeting.GoodbyeDwellMs); // the rest is kept
            Assert.Contains("\"SchemaVersion\": " + AppSettings.CurrentSchemaVersion, File.ReadAllText(path)); // saved at the current version

            // Turned back on afterwards: the choice stays.
            service.Current.Greeting.EnableGreeting = true;
            await service.SaveAsync();
            var again = new SettingsService(path);
            await again.LoadAsync();
            Assert.True(again.Current.Greeting.EnableGreeting);
        }

        [Fact]
        public void AnImportedOlderFile_IsMigratedToo()
        {
            Assert.True(SettingsService.TryParse("{ \"SchemaVersion\": 1, \"Greeting\": { \"EnableGoodbye\": true } }", out AppSettings settings, out _));
            Assert.False(settings.Greeting.EnableGoodbye);
            Assert.True(SettingsService.TryParse("{ \"SchemaVersion\": 2, \"Greeting\": { \"EnableGoodbye\": true } }", out settings, out _));
            Assert.True(settings.Greeting.EnableGoodbye);
        }
    }
}
