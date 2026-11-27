// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace ByteExplorer.Infrastructure
{
    /// <summary>What the password dialog asks for (<see cref="IDialogService.PromptPasswordAsync"/>).</summary>
    public sealed class PasswordRequest
    {
        public string Title { get; init; } = "Password";

        /// <summary>The line under the title ("Type the password of "Photos.bxlock".").</summary>
        public string Message { get; init; }

        public string OkText { get; init; } = "OK";

        /// <summary>
        /// A new password: asked twice, checked against the rules (<see cref="Core.Security.ProtectedItems.PasswordProblem"/>)
        /// and given a strength label.
        /// </summary>
        public bool IsNew { get; init; }

        /// <summary>A notice shown in a box above the buttons (that a forgotten password can't be recovered, …); null for none.</summary>
        public string Warning { get; init; }

        /// <summary>Show "keep it unlocked" (with <see cref="Remember"/> as its first state).</summary>
        public bool OfferRemember { get; init; }

        public bool Remember { get; init; } = true;

        /// <summary>
        /// Run when OK is pressed, with the dialog still open: returns null to accept, or what is wrong ("The password is
        /// wrong."), which is shown in the dialog so the person can try again. Cancelled when the dialog is cancelled.
        /// </summary>
        public Func<string, CancellationToken, Task<string>> Check { get; init; }
    }

    /// <summary>The password typed, and whether to keep the item unlocked.</summary>
    public sealed class PasswordAnswer
    {
        public string Password { get; init; }
        public bool Remember { get; init; }
    }
}
