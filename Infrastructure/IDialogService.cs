// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Core.Services.FileOperations;

namespace ByteExplorer.Infrastructure
{
    /// <summary>
    /// Dialogs a view model needs an answer from. The view models never create windows (the WPF build's rename handler
    /// constructed its dialog directly, tying the view model to WPF); the main window implements this interface and the
    /// view models receive it from <see cref="ViewModels.MainViewModel"/>.
    /// </summary>
    public interface IDialogService
    {
        /// <summary>Asks for a new name for an item; null when cancelled.</summary>
        Task<string> PromptRenameAsync(string currentName, bool isDirectory);

        /// <summary>Asks whether to delete the items (to the trash, or permanently); true to go ahead.</summary>
        /// <summary>Asks before deleting (to the trash, or permanently), naming what is deleted.</summary>
        Task<bool> ConfirmDeleteAsync(IReadOnlyList<string> paths, bool permanent);

        /// <summary>A yes/no question; true when the person picks the confirm button.</summary>
        Task<bool> ConfirmAsync(string title, string message, string confirmText);

        /// <summary>Shows the Properties window for one or more items (does not wait for it to close).</summary>
        void ShowProperties(IReadOnlyList<string> paths);

        /// <summary>
        /// Asks for one line of text; <paramref name="validate"/> returns null when the text is acceptable, else the reason
        /// (shown while typing). Returns the trimmed text, or null when cancelled.
        /// </summary>
        Task<string> PromptTextAsync(string title, string message, string initialText, string placeholder, string okText, System.Func<string, string> validate);

        /// <summary>
        /// Asks for a password (masked, with a show button; twice for a new one). Returns null when cancelled. See
        /// <see cref="PasswordRequest.Check"/> for checking it before the dialog closes.
        /// </summary>
        Task<PasswordAnswer> PromptPasswordAsync(PasswordRequest request);

        /// <summary>A message with an OK button.</summary>
        Task ShowMessageAsync(string title, string message);

        /// <summary>
        /// Asks what to do about a name clash: Replace (Merge for folders), Skip, Keep both, or Cancel, with "do this for
        /// all". Closes itself and returns Cancel when <paramref name="cancellationToken"/> is cancelled.
        /// </summary>
        Task<ConflictDecision> ResolveConflictAsync(ConflictInfo conflict, CancellationToken cancellationToken);

        /// <summary>The error question for an item a file operation couldn't handle (Retry, Skip, Skip all, Cancel).</summary>
        Task<ErrorChoice> ResolveErrorAsync(ErrorInfo error, CancellationToken cancellationToken);
    }
}
