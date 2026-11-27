// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ByteExplorer.Infrastructure;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// The cause of the "1 ms" PlatformNotSupportedException in Dispatcher.PushFrame: when Avalonia's headless runner
    /// isolates tests it clears <see cref="Dispatcher.UIThread"/> before each one, and another thread that reads the getter in
    /// that gap creates a dispatcher with no platform behind it, which then serves the next test (the suite now runs as one
    /// application, see HeadlessSetup.cs). The app's background code posts through <see cref="UiThread"/>, which never
    /// creates a dispatcher.
    /// </summary>
    public sealed class UiThreadTests
    {
        private static readonly FieldInfo UiThreadField =
            typeof(Dispatcher).GetField("s_uiThread", BindingFlags.NonPublic | BindingFlags.Static);

        [Fact]
        public void TheTestHarness_FindsTheAvaloniaInternalsItUses() =>
            Assert.True(SharedApplication.InternalsFound, "HeadlessSetup.cs: Avalonia renamed an internal it clears between tests");

        [AvaloniaFact]
        public void BackgroundPosts_InTheResetGap_NeverCreateADispatcher()
        {
            Assert.NotNull(UiThreadField); // Avalonia's field: if it is renamed, this test needs updating
            Dispatcher current = Dispatcher.UIThread;
            Assert.Same(current, UiThread.Dispatcher);
            try
            {
                UiThreadField.SetValue(null, null); // what Dispatcher.ResetBeforeUnitTests does before the next test
                // Blocking on purpose: an await here would let the test runner read the cleared getter itself.
                Task.Run(() =>
                {
                    UiThread.Post(() => { });
                    UiThread.Post(() => { }, DispatcherPriority.Background);
                    _ = UiThread.CheckAccess();
                }).GetAwaiter().GetResult();
                Assert.Null(UiThreadField.GetValue(null));
            }
            finally
            {
                UiThreadField.SetValue(null, current);
            }
        }

        [AvaloniaFact]
        public void TheStaticGetter_InTheResetGap_CreatesAPlatformlessDispatcher()
        {
            // The failure UiThread avoids, reproduced: in the gap the runner has cleared the dispatcher and entered a fresh,
            // still empty locator scope; a dispatcher created then by a background thread has no platform and can't run frames.
            Type locator = typeof(Dispatcher).Assembly.GetType("Avalonia.AvaloniaLocator", throwOnError: true); // internal
            const BindingFlags Statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            PropertyInfo current = locator.GetProperty("Current", Statics);
            PropertyInfo currentMutable = locator.GetProperty("CurrentMutable", Statics);
            Dispatcher dispatcher = Dispatcher.UIThread;
            object resolver = current.GetValue(null), mutable = currentMutable.GetValue(null);
            try
            {
                object empty = Activator.CreateInstance(locator, nonPublic: true);
                currentMutable.GetSetMethod(true).Invoke(null, new object[] { empty });
                current.GetSetMethod(true).Invoke(null, new object[] { empty });
                UiThreadField.SetValue(null, null);
                Dispatcher stray = Task.Run(() => Dispatcher.UIThread).GetAwaiter().GetResult();
                Assert.NotSame(dispatcher, stray);
                Assert.Throws<PlatformNotSupportedException>(() => stray.PushFrame(new DispatcherFrame()));
            }
            finally
            {
                UiThreadField.SetValue(null, dispatcher);
                currentMutable.GetSetMethod(true).Invoke(null, new object[] { mutable });
                current.GetSetMethod(true).Invoke(null, new object[] { resolver });
            }
        }
    }
}
