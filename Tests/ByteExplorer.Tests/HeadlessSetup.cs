// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(ByteExplorer.Tests.TestAppBuilder))]

// One headless application for the whole assembly, not one per test. Per test, Avalonia tears the app down and clears
// Dispatcher.UIThread, then builds the platform again; any other thread that reads Dispatcher.UIThread in that gap
// creates a dispatcher with no platform behind it (NullDispatcherImpl). Its getter is not atomic, so such a thread can
// even overwrite the good dispatcher after setup. That stray dispatcher then serves the next test, whose first await
// fails at once in Dispatcher.PushFrame with PlatformNotSupportedException (the "1 ms" flake). The readers can't all be
// removed: plain [Fact] tests run in parallel with the UI tests, Avalonia's bindings marshal background PropertyChanged
// through the getter, and background work from an earlier test may still be finishing. Without the per-test reset there
// is no gap. Each test still makes and closes its own windows and view models (UiThreadTests shows the gap itself).
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]
[assembly: ByteExplorer.Tests.CloseLeftoverWindows]

namespace ByteExplorer.Tests
{
    /// <summary>The real App (theme, styles, resources) on the headless platform with Skia rendering, for UI tests.</summary>
    public static class TestAppBuilder
    {
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure(() =>
                {
                    // The platform is registered by now, the app not yet created: a dispatcher that another thread created
                    // before that (a plain [Fact] running in parallel) has no platform behind it and is replaced here.
                    SharedApplication.ReplacePlatformlessDispatcher();
                    return new App();
                })
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}

namespace ByteExplorer.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;
    using Avalonia.Controls;
    using Avalonia.Input;
    using Avalonia.Interactivity;
    using Avalonia.Threading;

    /// <summary>
    /// Keeps tests from affecting each other through the one shared application: it starts before any test, and every
    /// UI test starts with no window left open by an earlier one.
    /// </summary>
    internal static class SharedApplication
    {
        private static readonly HashSet<Window> OpenWindows = new();

        /// <summary>KeyboardDevice.Instance and SetFocusedElement (internal in Avalonia 11.3).</summary>
        private static readonly PropertyInfo KeyboardInstance =
            typeof(KeyboardDevice).GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

        private static readonly MethodInfo SetFocusedElement =
            typeof(KeyboardDevice).GetMethod("SetFocusedElement", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>
        /// Starts the headless application's setup as soon as this assembly loads, before xUnit runs a test, so plain
        /// [Fact] tests running in parallel have as little time as possible before the platform's dispatcher exists (see
        /// TestAppBuilder for the check at setup). It does not wait: the session's thread needs this assembly's types,
        /// which are not usable until this initializer returns.
        /// </summary>
        [ModuleInitializer]
        internal static void StartBeforeAnyTest()
        {
            // On the thread pool: starting the session waits for its thread, which needs this assembly's types.
            Task.Run(() => HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SharedApplication).Assembly)
                    .Dispatch(() =>
                    {
                        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => OpenWindows.Add(window));
                        Window.WindowClosedEvent.AddClassHandler<Window>((window, _) => OpenWindows.Remove(window));
                    }, CancellationToken.None))
                .ContinueWith(t => Console.Error.WriteLine($"[SharedApplication] setup failed: {t.Exception}"), TaskContinuationOptions.OnlyOnFaulted);
        }

        /// <summary>
        /// Closes the windows a test left open (on the UI thread, after a UI test). Keyboard focus is global: a leftover
        /// window could take the next test's key presses, and its view model keeps watching folders.
        /// </summary>
        internal static void CloseLeftoverWindows(MethodInfo test)
        {
            // The keyboard is one device for the whole application, and a key goes to its focused element if there is one,
            // whatever window that element is in (KeyboardDevice.ProcessRawEvent: FocusedElement ?? the window). A test's
            // focus left behind would take the next test's key presses; per-test isolation used to start with no focus.
            if (KeyboardInstance?.GetValue(null) is object keyboard)
            {
                SetFocusedElement?.Invoke(keyboard, new object[] { null, NavigationMethod.Unspecified, KeyModifiers.None });
            }

            if (OpenWindows.Count == 0)
            {
                return;
            }

            foreach (Window window in OpenWindows.ToArray())
            {
                Report($"{test.DeclaringType?.Name}.{test.Name} left {window.GetType().Name} open; closing it");
                try
                {
                    if (window is MainWindowBase main)
                    {
                        main.CloseWithoutAsking(); // a main window's own close asks questions and says goodbye first
                    }
                    else
                    {
                        window.Close();
                    }
                }
                catch (Exception ex)
                {
                    Report($"closing it failed: {ex.Message}");
                }
            }

            Dispatcher.UIThread.RunJobs();
            foreach (Window window in OpenWindows.ToArray())
            {
                Report($"{window.GetType().Name} did not close");
                OpenWindows.Remove(window); // reported once, not after every later test
            }
        }

        /// <summary>The Avalonia internals the harness relies on were found (an Avalonia update may rename them).</summary>
        internal static bool InternalsFound => KeyboardInstance != null && SetFocusedElement != null && s_uiThread != null;

        /// <summary>Notes for whoever reads a test run: stderr (hidden by dotnet test) and shared-application.log next to the tests.</summary>
        internal static void Report(string line)
        {
            Console.Error.WriteLine("[SharedApplication] " + line);
            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "shared-application.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
            catch (System.IO.IOException)
            {
            }
        }

        /// <summary>Clears a UI dispatcher with no platform behind it, so the next read creates it from the platform.</summary>
        internal static void ReplacePlatformlessDispatcher()
        {
            FieldInfo controlled = typeof(Dispatcher).GetField("_controlledImpl", BindingFlags.NonPublic | BindingFlags.Instance);
            if (s_uiThread.GetValue(null) is Dispatcher dispatcher && controlled?.GetValue(dispatcher) == null)
            {
                Report("replaced a UI dispatcher created before the platform existed");
                s_uiThread.SetValue(null, null);
            }
        }

        /// <summary>True on the shared application's UI thread (read without Dispatcher.UIThread's create-on-read getter).</summary>
        internal static bool OnUiThread => s_uiThread.GetValue(null) is Dispatcher dispatcher && dispatcher.CheckAccess();

        private static readonly FieldInfo s_uiThread = typeof(Dispatcher).GetField("s_uiThread", BindingFlags.NonPublic | BindingFlags.Static);
    }

    /// <summary>After each UI test: <see cref="SharedApplication.CloseLeftoverWindows"/>.</summary>
    [AttributeUsage(AttributeTargets.Assembly)]
    public sealed class CloseLeftoverWindowsAttribute : Xunit.Sdk.BeforeAfterTestAttribute
    {
        public override void After(MethodInfo methodUnderTest)
        {
            if (SharedApplication.OnUiThread)
            {
                SharedApplication.CloseLeftoverWindows(methodUnderTest);
            }
        }
    }
}

namespace ByteExplorer.Tests
{
    /// <summary>
    /// Test classes that change app-wide state: the phone provider (Phones.UseProviderForTests), the Folder options source
    /// and the counted folder sizes. They run alone, never interleaved with other tests' windows (which would see a fake
    /// phone appear among their drives, or options change under them).
    /// </summary>
    [Xunit.CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class GlobalStateCollection
    {
        public const string Name = "App-wide state";
    }
}
