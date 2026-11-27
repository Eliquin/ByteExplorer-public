// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Core.Platform.Phones;
using ByteExplorer.Core.Platform.Phones.Wpd;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// The thread every Windows Portable Devices call runs on (WpdThread): one long-lived thread, calls one at a time in
    /// order, results and exceptions handed back as they are, nested calls run at once; and the "Show phones and cameras"
    /// switch, which keeps the app from asking devices anything.
    /// </summary>
#pragma warning disable CA1416 // WpdThread is used on Windows only; its queue and thread are plain .NET, tested everywhere
    [Xunit.Collection(GlobalStateCollection.Name)]
    public sealed class PortableDevicesThreadTests
    {
        [Fact]
        public async Task Calls_RunOneAtATime_InOrder_OnOneLongLivedThread()
        {
            int threadId = WpdThread.Run(() => Environment.CurrentManagedThreadId);
            Assert.NotEqual(Environment.CurrentManagedThreadId, threadId);
            Assert.False(WpdThread.IsCurrent);

            int running = 0;
            int overlaps = 0;
            var order = new List<int>();
            var threads = new HashSet<int>();
            Task[] callers = Enumerable.Range(0, 40).Select(i => Task.Run(() => WpdThread.Run(() =>
            {
                if (Interlocked.Increment(ref running) > 1)
                {
                    Interlocked.Increment(ref overlaps);
                }

                Thread.Sleep(2);
                lock (order)
                {
                    order.Add(i);
                    threads.Add(Environment.CurrentManagedThreadId);
                }

                Interlocked.Decrement(ref running);
            }))).ToArray();
            await Task.WhenAll(callers);

            Assert.Equal(0, overlaps); // the driver never sees two calls at once
            Assert.Equal(40, order.Count);
            Assert.Equal(new[] { threadId }, threads); // always the same thread: the objects outlive no thread
            Assert.Equal(threadId, WpdThread.Run(() => Environment.CurrentManagedThreadId));
        }

        [Fact]
        public void Exceptions_ComeBackUnwrapped_AndNestedCallsDontWaitForThemselves()
        {
            var thrown = Assert.Throws<IOException>(() => WpdThread.Run<int>(() => throw new IOException("unplugged")));
            Assert.Equal("unplugged", thrown.Message);

            // A provider method calling another (Find → GetDevices): runs at once on the same thread.
            int nested = WpdThread.Run(() => WpdThread.IsCurrent ? WpdThread.Run(() => 7) : -1);
            Assert.Equal(7, nested);

            // The thread keeps working after a failed call.
            Assert.Equal(3, WpdThread.Run(() => 3));
        }

        /// <summary>
        /// In a COM interface an array parameter is marshalled as a SAFEARRAY unless it says otherwise. A C array declared
        /// as a bare T[] (IPortableDeviceManager.GetDevices was) is corrupted by the marshaller: an access violation that
        /// kills the process silently. Every array in the app's hand-written COM interfaces must state LPArray.
        /// </summary>
        [Fact]
        public void ComInterfaces_NeverLetAnArrayDefaultToSafeArray()
        {
            var offenders = new List<string>();
            foreach (Type type in typeof(Phones).Assembly.GetTypes().Where(t => t.IsInterface && t.IsDefined(typeof(System.Runtime.InteropServices.ComImportAttribute), false)))
            {
                foreach (System.Reflection.MethodInfo method in type.GetMethods())
                {
                    foreach (System.Reflection.ParameterInfo parameter in method.GetParameters())
                    {
                        Type parameterType = parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType() : parameter.ParameterType;
                        if (parameterType == typeof(string))
                        {
                            // A string defaults to BSTR in a COM interface; these APIs take LPWSTR: say so on each.
                            var stringMarshal = (System.Runtime.InteropServices.MarshalAsAttribute)Attribute.GetCustomAttribute(parameter, typeof(System.Runtime.InteropServices.MarshalAsAttribute));
                            if (stringMarshal == null)
                            {
                                offenders.Add($"{type.Name}.{method.Name}({parameter.Name}): string without MarshalAs");
                            }

                            continue;
                        }

                        if (!parameterType.IsArray)
                        {
                            continue;
                        }

                        var marshal = (System.Runtime.InteropServices.MarshalAsAttribute)Attribute.GetCustomAttribute(parameter, typeof(System.Runtime.InteropServices.MarshalAsAttribute));
                        if (marshal?.Value != System.Runtime.InteropServices.UnmanagedType.LPArray)
                        {
                            offenders.Add($"{type.Name}.{method.Name}({parameter.Name})");
                        }
                    }
                }
            }

            Assert.Empty(offenders);
            Assert.Contains(typeof(Phones).Assembly.GetTypes(), t => t.Name == "IPortableDeviceManager"); // the check saw the interfaces
            Assert.Contains(typeof(Phones).Assembly.GetTypes(), t => t.Name == "IShellLinkW");
        }

        [Fact]
        public void TheSwitch_TurnsPhonesOff()
        {
            Func<bool> previous = Phones.IsEnabled;
            try
            {
                Phones.IsEnabled = () => false;
                Assert.False(Phones.ShowsPhones);
                Assert.Empty(Phones.GetDevices());

                // A provider set by tests is used whatever the setting (the phone tests rely on it).
                using (Phones.UseProviderForTests(new FakePhoneProvider()))
                {
                    Assert.True(Phones.ShowsPhones);
                }

                Phones.IsEnabled = () => throw new InvalidOperationException("settings not ready");
                Assert.True(Phones.ShowsPhones); // a broken source never hides phones by accident
            }
            finally
            {
                Phones.IsEnabled = previous;
            }
        }
    }
#pragma warning restore CA1416
}
