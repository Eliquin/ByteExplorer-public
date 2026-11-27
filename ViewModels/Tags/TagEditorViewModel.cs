// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Media;
using ByteExplorer.Core;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Tags;
using ByteExplorer.Infrastructure;

namespace ByteExplorer.ViewModels.Tags
{
    /// <summary>A tag in the editor's list: whether the edited items have it (all, none, some), and its place in the tree.</summary>
    public sealed class TagEditorRow : ViewModelBase
    {
        private const double IndentPerLevel = 18;
        private bool? _state;

        internal TagEditorRow(TagDefinition tag, string fullName, int depth, int count, bool? state)
        {
            Tag = tag;
            FullName = fullName;
            Depth = depth;
            Count = count;
            _state = state;
            Brush = new SolidColorBrush(Color.Parse(tag.ColorHex)).ToImmutable();
        }

        public TagDefinition Tag { get; }

        public string Id => Tag.Id;

        public string Name => Tag.Name;

        public string FullName { get; }

        public int Depth { get; }

        public Thickness Indent => new Thickness(Depth * IndentPerLevel, 0, 0, 0);

        public int Count { get; }

        public string CountText => Count == 1 ? "1 item" : $"{Count:N0} items";

        public IBrush Brush { get; }

        public string Description => Tag.Description;

        public bool HasDescription => !string.IsNullOrEmpty(Tag.Description);

        /// <summary>True: every edited item has the tag; false: none; null: some (a click gives it to all).</summary>
        public bool? State
        {
            get => _state;
            set
            {
                // A click on a mixed box gives the tag to every item; after that it toggles all/none.
                bool? next = value ?? true;
                if (SetProperty(ref _state, next))
                {
                    Changed?.Invoke(this);
                }
            }
        }

        internal Action<TagEditorRow> Changed { get; set; }
    }

    /// <summary>
    /// The tag editor: the detailed way to tag items (the quick way is the context menu's Tags submenu). For the edited
    /// items: every tag with a check box (all, none, or some of the items), a find-or-create box (a name with slashes
    /// creates sub-tags), and their comment; for the selected tag: name, color, parent and description. Without items it
    /// manages the tags alone. Tag definitions change at once (they are shared by every item); the items' tags and comment
    /// change when the person saves, so Cancel leaves the items as they were.
    /// </summary>
    public sealed class TagEditorViewModel : ViewModelBase, IDisposable
    {
        private readonly TagStore _store;
        private readonly Dictionary<string, bool?> _states = new Dictionary<string, bool?>(StringComparer.Ordinal);
        private readonly Dictionary<string, bool?> _initialStates = new Dictionary<string, bool?>(StringComparer.Ordinal);
        private readonly string _initialComment;
        private readonly bool _commentsDiffer;
        private string _filter = string.Empty;
        private string _comment;
        private bool _commentEdited;
        private TagEditorRow _selected;
        private IReadOnlyList<TagEditorRow> _rows = Array.Empty<TagEditorRow>();
        private string _error;

        public TagEditorViewModel(TagStore store, IReadOnlyList<string> paths)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            Paths = paths ?? Array.Empty<string>();
            foreach (TagDefinition tag in store.Tags)
            {
                int having = Paths.Count(p => store.GetTags(p).Any(t => t.Id == tag.Id));
                bool? state = having == 0 ? false : having == Paths.Count ? true : null;
                _states[tag.Id] = state;
                _initialStates[tag.Id] = state;
            }

            List<string> comments = Paths.Select(store.GetComment).Distinct(StringComparer.Ordinal).ToList();
            _commentsDiffer = comments.Count > 1;
            _initialComment = comments.Count == 1 ? comments[0] : string.Empty;
            _comment = _initialComment;
            Colors = PinColors.All.Select(c => new TagColorChoice(c.Name, c.Hex)).ToList();
            CreateCommand = new RelayCommand(_ => CreateFromFilter(), _ => !string.IsNullOrWhiteSpace(_filter));
            ClearFilterCommand = new RelayCommand(_ => Filter = string.Empty);
            SetColorCommand = new RelayCommand(color => SetSelectedColor(color as string));
            RemoveAllCommand = new RelayCommand(_ => SetAll(false));
            TagStore.AnyChanged += OnStoreChanged;
            Rebuild();
        }

        public IReadOnlyList<string> Paths { get; }

        public TagStore Store => _store;

        /// <summary>Editing items (false: managing the tags only).</summary>
        public bool HasItems => Paths.Count > 0;

        public string Title => Paths.Count == 0 ? "Manage tags" : Paths.Count == 1 ? "Tags for " + PathUtil.GetName(Paths[0]) : $"Tags for {Paths.Count:N0} items";

        public string Subtitle => Paths.Count == 1 ? Paths[0] : Paths.Count == 0 ? "Create, rename, recolor and arrange tags." : "Checked tags go on every item; a dash means some items have it.";

        public IReadOnlyList<TagEditorRow> Rows
        {
            get => _rows;
            private set => SetProperty(ref _rows, value);
        }

        public bool HasNoTags => _store.Tags.Count == 0;

        public IReadOnlyList<TagColorChoice> Colors { get; }

        /// <summary>Filters the list as typed; Enter (or Create) makes a tag of that name and checks it.</summary>
        public string Filter
        {
            get => _filter;
            set
            {
                if (SetProperty(ref _filter, value ?? string.Empty))
                {
                    Error = null;
                    Rebuild();
                    RaisePropertyChanged(nameof(CreateText));
                    (CreateCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>"Create "Work/Project A"" or, when the tag exists, what Enter does with it.</summary>
        public string CreateText
        {
            get
            {
                string name = _filter.Trim();
                if (name.Length == 0)
                {
                    return "Create";
                }

                TagDefinition existing = _store.FindByFullName(name);
                return existing == null ? $"Create \"{name}\"" : HasItems ? $"Check \"{name}\"" : $"Select \"{name}\"";
            }
        }

        public string Error
        {
            get => _error;
            private set
            {
                if (SetProperty(ref _error, value))
                {
                    RaisePropertyChanged(nameof(HasError));
                }
            }
        }

        public bool HasError => !string.IsNullOrEmpty(_error);

        public string Comment
        {
            get => _comment;
            set
            {
                if (SetProperty(ref _comment, value ?? string.Empty))
                {
                    _commentEdited = true;
                }
            }
        }

        /// <summary>The comment box's hint: several items with different comments get the typed one only if it is edited.</summary>
        public string CommentHint => _commentsDiffer ? "The items have different comments; what you type here replaces them all." : "A short note about this item (shown in its tool tip and the details pane).";

        // ------------------------------------------------------------------ selected tag

        public TagEditorRow Selected
        {
            get => _selected;
            set
            {
                if (SetProperty(ref _selected, value))
                {
                    // While the panel switches to another tag, its controls re-select their values (the Parent drop-down
                    // picks "None" from its new list and writes it back): those writes are not edits.
                    _switchingSelection = true;
                    try
                    {
                        SyncParentChoices();
                        RaisePropertyChanged(nameof(HasSelection));
                        RaisePropertyChanged(nameof(SelectedName));
                        RaisePropertyChanged(nameof(SelectedDescription));
                        RaisePropertyChanged(nameof(SelectedParent));
                    }
                    finally
                    {
                        _switchingSelection = false;
                    }

                    foreach (TagColorChoice color in Colors)
                    {
                        color.IsSelected = value != null && string.Equals(color.Name, value.Tag.Color, StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
        }

        public bool HasSelection => _selected != null;

        /// <summary>The selected tag's name; a change renames it (refused with a message on a clash).</summary>
        public string SelectedName
        {
            get => _selected?.Name ?? string.Empty;
            set => EditSelected(tag =>
            {
                string name = TagStore.CleanName(value);
                if (name == null)
                {
                    throw new ArgumentException("A tag needs a name without slashes.");
                }

                if (name != tag.Name)
                {
                    _store.RenameTag(tag.Id, name);
                }
            });
        }

        public string SelectedDescription
        {
            get => _selected?.Description ?? string.Empty;
            set => EditSelected(tag =>
            {
                if ((value ?? string.Empty).Trim() != tag.Description)
                {
                    _store.SetDescription(tag.Id, value);
                }
            });
        }

        private readonly Dictionary<string, TagParentChoice> _choiceById = new Dictionary<string, TagParentChoice>(StringComparer.Ordinal);
        private bool _switchingSelection;

        /// <summary>
        /// The tags the selected one can move under (not itself or its sub-tags), "None" first. One collection updated in
        /// place, with one choice object per tag kept for good: the drop-down matches its selected item by instance and
        /// resets its selection when its list is replaced — which once wrote "no parent" back and moved the tag to the top.
        /// </summary>
        public System.Collections.ObjectModel.ObservableCollection<TagParentChoice> ParentChoices { get; } =
            new System.Collections.ObjectModel.ObservableCollection<TagParentChoice> { TagParentChoice.None };

        public TagParentChoice SelectedParent
        {
            get => ParentChoices.FirstOrDefault(c => c.Id == _selected?.Tag.ParentId) ?? TagParentChoice.None;
            set
            {
                if (value == null || _switchingSelection)
                {
                    return; // the drop-down clears or re-selects while its items change; only a person's pick is an edit
                }

                EditSelected(tag =>
                {
                    if (value.Id != tag.ParentId)
                    {
                        _store.SetParent(tag.Id, value.Id);
                    }
                });
            }
        }

        /// <summary>Brings <see cref="ParentChoices"/> to the selected tag's possible parents, moving as few items as it can.</summary>
        private void SyncParentChoices()
        {
            var desired = new List<TagParentChoice> { TagParentChoice.None };
            if (_selected != null)
            {
                IReadOnlyCollection<string> own = _store.WithDescendants(_selected.Id);
                foreach (TagDefinition tag in _store.Tags.Where(t => !own.Contains(t.Id)))
                {
                    string name = _store.FullName(tag).Replace("/", " › ");
                    if (!_choiceById.TryGetValue(tag.Id, out TagParentChoice choice))
                    {
                        choice = new TagParentChoice(tag.Id, name);
                        _choiceById[tag.Id] = choice;
                    }

                    choice.Name = name;
                    desired.Add(choice);
                }
            }

            for (int i = ParentChoices.Count - 1; i >= 0; i--)
            {
                if (!desired.Contains(ParentChoices[i]))
                {
                    ParentChoices.RemoveAt(i);
                }
            }

            for (int i = 0; i < desired.Count; i++)
            {
                int at = ParentChoices.IndexOf(desired[i]);
                if (at < 0)
                {
                    ParentChoices.Insert(i, desired[i]);
                }
                else if (at != i)
                {
                    ParentChoices.Move(at, i);
                }
            }
        }

        public ICommand CreateCommand { get; }

        public ICommand ClearFilterCommand { get; }

        public ICommand SetColorCommand { get; }

        public ICommand RemoveAllCommand { get; }

        /// <summary>Creates a sub-tag of the selected tag (named by the caller's prompt) and checks it.</summary>
        public TagDefinition CreateSubTag(string name)
        {
            if (_selected == null)
            {
                return null;
            }

            return CreateAndCheck(name, _selected.Id);
        }

        public void SelectById(string id)
        {
            Selected = _rows.FirstOrDefault(r => r.Id == id);
        }

        // ------------------------------------------------------------------ saving

        /// <summary>Whether Save would change the items.</summary>
        public bool HasChanges =>
            HasItems && (_states.Any(s => !_initialStates.TryGetValue(s.Key, out bool? initial) || initial != s.Value) ||
                         _commentEdited && _comment != _initialComment);

        /// <summary>Applies the checked tags and the comment to the items: added, removed, and mixed ones left as they are.</summary>
        public void Save()
        {
            if (!HasItems)
            {
                return;
            }

            foreach ((string id, bool? state) in _states.ToList())
            {
                _initialStates.TryGetValue(id, out bool? initial);
                if (state == initial || state == null || _store.Find(id) == null)
                {
                    continue;
                }

                if (state == true)
                {
                    _store.Assign(Paths, id);
                }
                else
                {
                    _store.Unassign(Paths, id);
                }
            }

            if (_commentEdited && _comment != _initialComment)
            {
                _store.SetComment(Paths, _comment);
            }
        }

        public void Dispose() => TagStore.AnyChanged -= OnStoreChanged;

        // ------------------------------------------------------------------ helpers

        private void CreateFromFilter()
        {
            string name = _filter.Trim();
            string invalid = Views.Tags.TagActions.ValidateName(name);
            if (invalid != null)
            {
                Error = invalid;
                return;
            }

            TagDefinition tag = CreateAndCheck(name, null);
            if (tag != null)
            {
                Filter = string.Empty;
                SelectById(tag.Id);
            }
        }

        private TagDefinition CreateAndCheck(string name, string parentId)
        {
            try
            {
                TagDefinition tag = _store.CreateTag(name, parentId: parentId);
                if (HasItems)
                {
                    _states[tag.Id] = true;
                }

                Rebuild();
                return tag;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
            {
                Error = ex.Message;
                return null;
            }
        }

        private void SetAll(bool value)
        {
            foreach (string id in _states.Keys.ToList())
            {
                _states[id] = value;
            }

            Rebuild();
        }

        private void SetSelectedColor(string color) => EditSelected(tag => _store.SetColor(tag.Id, color));

        private void EditSelected(Action<TagDefinition> edit)
        {
            TagDefinition tag = _selected == null ? null : _store.Find(_selected.Id);
            if (tag == null)
            {
                return;
            }

            try
            {
                Error = null;
                edit(tag);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
            {
                Error = ex.Message;
                RaisePropertyChanged(nameof(SelectedName));
                RaisePropertyChanged(nameof(SelectedParent));
            }
        }

        private void OnStoreChanged(object sender, TagsChangedEventArgs e)
        {
            if (ReferenceEquals(sender, _store) && e.DefinitionsChanged)
            {
                Rebuild();
            }
        }

        private void Rebuild()
        {
            string selectedId = _selected?.Id;
            string filter = _filter.Trim();
            var rows = new List<TagEditorRow>();
            foreach (TagDefinition tag in _store.Tags)
            {
                if (!_states.ContainsKey(tag.Id))
                {
                    _states[tag.Id] = false;
                    _initialStates[tag.Id] = false;
                }

                string fullName = _store.FullName(tag);
                if (filter.Length > 0 && fullName.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) < 0)
                {
                    continue;
                }

                var row = new TagEditorRow(tag, fullName, filter.Length > 0 ? 0 : _store.Depth(tag), _store.CountItems(tag.Id), _states[tag.Id])
                {
                    Changed = r => _states[r.Id] = r.State
                };
                rows.Add(row);
            }

            foreach (string gone in _states.Keys.Where(id => _store.Find(id) == null).ToList())
            {
                _states.Remove(gone);
            }

            Rows = rows;
            RaisePropertyChanged(nameof(HasNoTags));
            _selected = null;
            Selected = selectedId == null ? null : rows.FirstOrDefault(r => r.Id == selectedId);
        }
    }

    /// <summary>A color of the palette in the editor, with whether the selected tag has it.</summary>
    public sealed class TagColorChoice : ViewModelBase
    {
        private bool _isSelected;

        public TagColorChoice(string name, string hex)
        {
            Name = name;
            Brush = new SolidColorBrush(Color.Parse(hex)).ToImmutable();
        }

        public string Name { get; }

        public IBrush Brush { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }
    }

    /// <summary>A possible parent of the selected tag ("None" for the top level). Kept per tag; its name follows renames.</summary>
    public sealed class TagParentChoice : ViewModelBase
    {
        public static readonly TagParentChoice None = new TagParentChoice(null, "None (top level)");

        private string _name;

        public TagParentChoice(string id, string name)
        {
            Id = id;
            _name = name;
        }

        public string Id { get; }

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        public override string ToString() => Name;
    }
}
