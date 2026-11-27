// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ByteExplorer.Core.Services.FileOperations;
using ByteExplorer.Core.Tags;
using ByteExplorer.Infrastructure;
using ByteExplorer.ViewModels.Tags;
using ByteExplorer.Views.Dialogs;
using Serilog;

namespace ByteExplorer.Views.Tags
{
    /// <summary>The tag editor window (see TagEditorViewModel); its prompts and confirmations open over it.</summary>
    public partial class TagEditorWindow : Window
    {
        private readonly TagEditorViewModel _viewModel;

        /// <summary>For the XAML loader and previewer only.</summary>
        public TagEditorWindow()
        {
            InitializeComponent();
        }

        public TagEditorWindow(TagEditorViewModel viewModel)
            : this()
        {
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            DataContext = viewModel;
            Dialogs = new OwnedDialogs(this);
            Opened += (_, _) => FilterBox.Focus();
            Closed += (_, _) => viewModel.Dispose();
            KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape && !viewModel.HasItems)
                {
                    Close();
                }
            };
        }

        public TagEditorViewModel ViewModel => _viewModel;

        /// <summary>Prompts and confirmations owned by this window.</summary>
        internal IDialogService Dialogs { get; }

        /// <summary>Opens the editor for items (or, with none, to manage the tags): modal over the owner when there is one.</summary>
        public static TagEditorWindow ShowFor(Window owner, TagStore store, IReadOnlyList<string> paths)
        {
            var window = new TagEditorWindow(new TagEditorViewModel(store, paths ?? Array.Empty<string>()));
            if (owner != null)
            {
                window.ShowDialog(owner).Forget("TagEditorWindow");
            }
            else
            {
                window.Show();
            }

            return window;
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            try
            {
                CommitFocusedEdit();
                _viewModel?.Save();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Saving the tags failed");
            }

            Close();
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            CommitFocusedEdit(); // tag definitions (name, description) apply as typed; only the items' tags are cancelled
            Close();
        }

        private async void OnNewSubTagClick(object sender, RoutedEventArgs e)
        {
            try
            {
                CommitFocusedEdit();
                if (_viewModel?.Selected == null)
                {
                    return;
                }

                string parent = _viewModel.Selected.FullName.Replace("/", " › ");
                string name = await Dialogs.PromptTextAsync("New sub-tag", $"A sub-tag of {parent}. Slashes create deeper sub-tags.", string.Empty, "Tag name", "Create",
                    TagActions.ValidateName);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    TagDefinition tag = _viewModel.CreateSubTag(name);
                    if (tag != null)
                    {
                        _viewModel.SelectById(tag.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Creating a sub-tag failed");
            }
        }

        private async void OnDeleteTagClick(object sender, RoutedEventArgs e)
        {
            try
            {
                TagDefinition tag = _viewModel?.Selected == null ? null : _viewModel.Selected.Tag;
                await TagActions.DeleteAsync(Dialogs, _viewModel.Store, tag);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Deleting a tag failed");
            }
        }

        /// <summary>Text boxes bound on lost focus: a click on a button applies what is being typed first.</summary>
        private void CommitFocusedEdit()
        {
            if (FocusManager?.GetFocusedElement() is TextBox box)
            {
                Avalonia.Data.BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateSource();
            }
        }

        /// <summary>The dialogs the tag actions need, opened over this window.</summary>
        private sealed class OwnedDialogs : IDialogService
        {
            private readonly Window _owner;

            public OwnedDialogs(Window owner) => _owner = owner;

            public Task<string> PromptTextAsync(string title, string message, string initialText, string placeholder, string okText, Func<string, string> validate) =>
                new TextPromptDialog(title, message, initialText, placeholder, okText, validate).ShowDialog<string>(_owner);

            public Task<bool> ConfirmAsync(string title, string message, string confirmText) =>
                new MessageDialog(title, message, confirmText, "Cancel", destructive: true).ShowDialog<bool>(_owner);

            public Task ShowMessageAsync(string title, string message) =>
                new MessageDialog(title, message, "OK", null, destructive: false).ShowDialog<bool>(_owner);

            public Task<string> PromptRenameAsync(string currentName, bool isDirectory) => throw new NotSupportedException();

            public Task<PasswordAnswer> PromptPasswordAsync(PasswordRequest request) => throw new NotSupportedException();

            public Task<bool> ConfirmDeleteAsync(IReadOnlyList<string> paths, bool permanent) => throw new NotSupportedException();

            public void ShowProperties(IReadOnlyList<string> paths) => throw new NotSupportedException();

            public Task<ConflictDecision> ResolveConflictAsync(ConflictInfo conflict, CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task<ErrorChoice> ResolveErrorAsync(ErrorInfo error, CancellationToken cancellationToken) => throw new NotSupportedException();
        }
    }
}
