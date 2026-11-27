// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using ByteExplorer.Infrastructure.Launch;
using Xunit;

namespace ByteExplorer.Tests
{
    /// <summary>
    /// The installer's command-line switches (<c>--unregister-default-file-manager</c>, <c>--register-default-file-manager</c>):
    /// run without a window through a fake default-file-manager seam, and ignored by the launch request parser.
    /// </summary>
    public sealed class MaintenanceCommandTests
    {
        [Fact]
        public void NormalStarts_AreNotMaintenanceCommands()
        {
            var control = new FakeControl { Supported = true, Registered = true };
            Assert.Null(MaintenanceCommand.TryRun(Array.Empty<string>(), control));
            Assert.Null(MaintenanceCommand.TryRun(null, control));
            Assert.Null(MaintenanceCommand.TryRun(new[] { LaunchRequest.FromShellSwitch, "/tmp" }, control));
            Assert.Null(MaintenanceCommand.TryRun(new[] { "--unregister-default-file-manager-please" }, control));
            Assert.Empty(control.Calls);
        }

        [Fact]
        public void Unregister_GivesFoldersBack_OnlyWhenRegistered()
        {
            var registered = new FakeControl { Supported = true, Registered = true };
            Assert.Equal(0, MaintenanceCommand.TryRun(new[] { MaintenanceCommand.UnregisterDefaultFileManagerSwitch }, registered));
            Assert.Equal(new[] { false }, registered.Calls);

            var notRegistered = new FakeControl { Supported = true, Registered = false };
            Assert.Equal(0, MaintenanceCommand.TryRun(new[] { "--UNREGISTER-DEFAULT-FILE-MANAGER" }, notRegistered));
            Assert.Empty(notRegistered.Calls);

            var unsupported = new FakeControl { Supported = false, Registered = true }; // macOS
            Assert.Equal(0, MaintenanceCommand.TryRun(new[] { " --unregister-default-file-manager " }, unsupported));
            Assert.Empty(unsupported.Calls);

            var failing = new FakeControl { Supported = true, Registered = true, Fail = true };
            Assert.Equal(1, MaintenanceCommand.TryRun(new[] { MaintenanceCommand.UnregisterDefaultFileManagerSwitch }, failing));
        }

        [Fact]
        public void Register_MakesItTheDefault_WhereSupported()
        {
            var control = new FakeControl { Supported = true };
            Assert.Equal(0, MaintenanceCommand.TryRun(new[] { MaintenanceCommand.RegisterDefaultFileManagerSwitch }, control));
            Assert.Equal(new[] { true }, control.Calls);

            var unsupported = new FakeControl { Supported = false };
            Assert.Equal(1, MaintenanceCommand.TryRun(new[] { MaintenanceCommand.RegisterDefaultFileManagerSwitch }, unsupported));
            Assert.Empty(unsupported.Calls);

            // The first maintenance switch wins.
            var both = new FakeControl { Supported = true, Registered = true };
            MaintenanceCommand.TryRun(new[] { MaintenanceCommand.UnregisterDefaultFileManagerSwitch, MaintenanceCommand.RegisterDefaultFileManagerSwitch }, both);
            Assert.Equal(new[] { false }, both.Calls);
        }

        [Fact]
        public void LaunchRequest_IgnoresTheSwitches()
        {
            foreach (string flag in new[] { MaintenanceCommand.UnregisterDefaultFileManagerSwitch, MaintenanceCommand.RegisterDefaultFileManagerSwitch })
            {
                Assert.True(MaintenanceCommand.IsMaintenanceSwitch(flag));
                LaunchRequest alone = LaunchRequest.Parse(new[] { flag }, Path.GetTempPath());
                Assert.False(alone.HasFolder);
                Assert.False(alone.FromShell);
                Assert.False(alone.ShouldForward); // never handed to a running ByteExplorer

                string folder = Path.GetTempPath();
                LaunchRequest withFolder = LaunchRequest.Parse(new[] { flag, folder }, folder);
                Assert.True(withFolder.HasFolder);
                Assert.Null(withFolder.Select);
            }

            Assert.False(MaintenanceCommand.IsMaintenanceSwitch(LaunchRequest.FromShellSwitch));
        }

        private sealed class FakeControl : IDefaultFileManagerControl
        {
            public bool Supported { get; set; }

            public bool Registered { get; set; }

            public bool Fail { get; set; }

            public List<bool> Calls { get; } = new List<bool>();

            public bool IsSupported => Supported;

            public bool HasRegistration => Registered;

            public void SetDefault(bool on)
            {
                Calls.Add(on);
                if (Fail)
                {
                    throw new UnauthorizedAccessException("denied");
                }
            }
        }
    }
}
