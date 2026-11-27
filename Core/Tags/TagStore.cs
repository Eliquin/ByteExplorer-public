// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Core.Models;
using ByteExplorer.Infrastructure;
using Serilog;

namespace ByteExplorer.Core.Tags
{
    /// <summary>
    /// Every tag and every tagged item: tags (name, color, optional parent for sub-tags, description), and per file or
    /// folder its tags and a comment. Saved as tags.json in the data folder.
    /// <para>
    /// Why a file of its own rather than metadata on the files (NTFS streams, extended attributes): those exist on some
    /// file systems only, are lost on FAT and USB sticks, network shares and in archives, and need a write to every file
    /// tagged; a central store works the same everywhere and tags read-only files too. The price is that the store must
    /// follow items: renames and moves done in ByteExplorer (and their undo) move the tags with them
    /// (<see cref="MovePath"/>), a folder's tagged contents included; items that disappear are set aside when their
    /// folder is refreshed and come back if the item does (an undone delete); and at startup, items gone for good are
    /// removed — but only where their folder is still reachable, so tags on an unplugged drive or an offline share are
    /// kept. Paths are saved portable (PortablePaths), so the tags of files in the user's folders survive the move of the
    /// portable app to another computer, as Quick access does.
    /// </para>
    /// <para>
    /// Thread safety: every read and write takes one lock (reads are dictionary lookups, cheap enough for each drawn row);
    /// <see cref="Changed"/> is raised on the thread that made the change (the UI thread in the app). Saving is
    /// debounced and done off the UI thread from a snapshot, atomically (a temporary file swapped in).
    /// </para>
    /// </summary>
    public sealed class TagStore
    {
        /// <summary>The store the views draw from; the app sets it once its store is loaded (tests may replace it).</summary>
        public static TagStore Default { get; set; } = new TagStore(null);

        private const int SaveDelayMs = 400;
        private const int GraveyardLimit = 2000;
        private static readonly IReadOnlyList<TagDefinition> NoTags = Array.Empty<TagDefinition>();
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        private readonly object _gate = new object();
        private readonly string _filePath;
        private readonly ILogger _logger;
        private readonly Dictionary<string, TagDefinition> _tags = new Dictionary<string, TagDefinition>(StringComparer.Ordinal);
        private readonly Dictionary<string, TaggedItem> _items = new Dictionary<string, TaggedItem>(PathUtil.Comparer);
        private readonly Dictionary<string, TaggedItem> _graveyard = new Dictionary<string, TaggedItem>(PathUtil.Comparer);
        private readonly SemaphoreSlim _saveGate = new SemaphoreSlim(1, 1);
        private IReadOnlyList<TagDefinition> _ordered = NoTags;
        private CancellationTokenSource _saveDelay;
        private int _colorCursor;

        /// <param name="filePath">Where the store is saved; null keeps it in memory only.</param>
        public TagStore(string filePath, ILogger logger = null)
        {
            _filePath = filePath;
            _logger = (logger ?? Log.Logger).ForContext<TagStore>();
        }

        /// <summary>Raised after any change (on the thread that made it).</summary>
        public event EventHandler<TagsChangedEventArgs> Changed;

        /// <summary>
        /// Raised after a change in any store (sender: the store). The views listen here rather than to one store because
        /// <see cref="Default"/> is replaced when the app loads its own; they ignore other stores.
        /// </summary>
        public static event EventHandler<TagsChangedEventArgs> AnyChanged;

        public string FilePath => _filePath;

        private long _version;

        /// <summary>Goes up with every change (sorted views by tag know when they are stale).</summary>
        public long Version => Interlocked.Read(ref _version);

        /// <summary>
        /// A sort key for an item's tags: their full names, sorted and joined; empty for an untagged item. Sorting by tags
        /// groups items with the same tags together, in tag-name order.
        /// </summary>
        public string SortKey(string path)
        {
            IReadOnlyList<TagDefinition> tags = GetTags(path);
            if (tags.Count == 0)
            {
                return string.Empty;
            }

            if (tags.Count == 1)
            {
                return FullName(tags[0]);
            }

            var names = new List<string>(tags.Count);
            foreach (TagDefinition tag in tags)
            {
                names.Add(FullName(tag));
            }

            names.Sort(StringComparer.CurrentCultureIgnoreCase);
            return string.Join("\u0001", names);
        }

        /// <summary>Every tag, parents before their sub-tags, siblings by name.</summary>
        public IReadOnlyList<TagDefinition> Tags
        {
            get
            {
                lock (_gate)
                {
                    return _ordered;
                }
            }
        }

        public int ItemCount
        {
            get
            {
                lock (_gate)
                {
                    return _items.Count;
                }
            }
        }

        // ------------------------------------------------------------------ reading

        public TagDefinition Find(string id)
        {
            if (id == null)
            {
                return null;
            }

            lock (_gate)
            {
                return _tags.TryGetValue(id, out TagDefinition tag) ? tag : null;
            }
        }

        /// <summary>A tag by its full name ("Work/Project A", case-insensitive), or null.</summary>
        public TagDefinition FindByFullName(string fullName)
        {
            string[] parts = SplitPath(fullName);
            if (parts.Length == 0)
            {
                return null;
            }

            lock (_gate)
            {
                TagDefinition current = null;
                foreach (string part in parts)
                {
                    current = ChildNamedLocked(current?.Id, part);
                    if (current == null)
                    {
                        return null;
                    }
                }

                return current;
            }
        }

        /// <summary>"Work/Project A".</summary>
        public string FullName(TagDefinition tag)
        {
            if (tag == null)
            {
                return string.Empty;
            }

            lock (_gate)
            {
                var names = new List<string>();
                for (TagDefinition current = tag; current != null && names.Count < 64; current = current.ParentId != null && _tags.TryGetValue(current.ParentId, out TagDefinition parent) ? parent : null)
                {
                    names.Add(current.Name);
                }

                names.Reverse();
                return string.Join("/", names);
            }
        }

        /// <summary>How many parents the tag has (0 for a top-level tag).</summary>
        public int Depth(TagDefinition tag)
        {
            lock (_gate)
            {
                int depth = 0;
                for (string parent = tag?.ParentId; parent != null && depth < 64 && _tags.TryGetValue(parent, out TagDefinition p); parent = p.ParentId)
                {
                    depth++;
                }

                return depth;
            }
        }

        public IReadOnlyList<TagDefinition> Children(string parentId)
        {
            lock (_gate)
            {
                return _ordered.Where(t => t.ParentId == parentId).ToList();
            }
        }

        /// <summary>The tag and all its sub-tags, at any depth.</summary>
        public IReadOnlyCollection<string> WithDescendants(string tagId)
        {
            lock (_gate)
            {
                return DescendantsLocked(tagId);
            }
        }

        /// <summary>The item's tags, in tag order; an empty list for an untagged item. Called for every drawn row.</summary>
        public IReadOnlyList<TagDefinition> GetTags(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return NoTags;
            }

            lock (_gate)
            {
                if (!_items.TryGetValue(path, out TaggedItem item) || item.TagIds.Count == 0)
                {
                    return NoTags;
                }

                var tags = new List<TagDefinition>(item.TagIds.Count);
                foreach (string id in item.TagIds)
                {
                    if (_tags.TryGetValue(id, out TagDefinition tag))
                    {
                        tags.Add(tag);
                    }
                }

                return tags;
            }
        }

        public string GetComment(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            lock (_gate)
            {
                return _items.TryGetValue(path, out TaggedItem item) ? item.Comment : string.Empty;
            }
        }

        public TaggedItem GetItem(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            lock (_gate)
            {
                return _items.TryGetValue(path, out TaggedItem item) ? item : null;
            }
        }

        /// <summary>The items carrying the tag (with <paramref name="includeSubTags"/>: or any of its sub-tags).</summary>
        public IReadOnlyList<string> ItemsWithTag(string tagId, bool includeSubTags = true)
        {
            lock (_gate)
            {
                IReadOnlyCollection<string> ids = includeSubTags ? DescendantsLocked(tagId) : new[] { tagId };
                return _items.Values.Where(i => i.TagIds.Any(ids.Contains)).Select(i => i.Path).ToList();
            }
        }

        /// <summary>How many items carry the tag or one of its sub-tags.</summary>
        public int CountItems(string tagId, bool includeSubTags = true)
        {
            lock (_gate)
            {
                IReadOnlyCollection<string> ids = includeSubTags ? DescendantsLocked(tagId) : new[] { tagId };
                int count = 0;
                foreach (TaggedItem item in _items.Values)
                {
                    foreach (string id in item.TagIds)
                    {
                        if (ids.Contains(id))
                        {
                            count++;
                            break;
                        }
                    }
                }

                return count;
            }
        }

        // ------------------------------------------------------------------ tags

        /// <summary>
        /// Creates a tag (or returns the sibling of that name that exists). A name with slashes creates the whole chain:
        /// "Work/Project A" makes "Work" if needed and "Project A" under it — sub-tags created automatically.
        /// </summary>
        public TagDefinition CreateTag(string name, string color = null, string parentId = null, string description = null)
        {
            string[] parts = SplitPath(name);
            if (parts.Length == 0)
            {
                throw new ArgumentException("A tag needs a name.", nameof(name));
            }

            TagDefinition created;
            lock (_gate)
            {
                if (parentId != null && !_tags.ContainsKey(parentId))
                {
                    throw new ArgumentException("The parent tag does not exist.", nameof(parentId));
                }

                created = null;
                string parent = parentId;
                for (int i = 0; i < parts.Length; i++)
                {
                    TagDefinition existing = ChildNamedLocked(parent, parts[i]);
                    if (existing == null)
                    {
                        bool last = i == parts.Length - 1;
                        string tagColor = Models.PinColors.Normalize(last ? color : null)
                                          ?? (parent != null && _tags.TryGetValue(parent, out TagDefinition p) ? p.Color : NextColorLocked());
                        existing = new TagDefinition(NewId(), parts[i], tagColor, parent, last ? description : null);
                        _tags[existing.Id] = existing;
                    }

                    created = existing;
                    parent = existing.Id;
                }

                ReorderLocked();
            }

            RaiseDefinitions();
            return created;
        }

        public void RenameTag(string tagId, string newName)
        {
            string name = CleanName(newName);
            if (name == null)
            {
                throw new ArgumentException("A tag needs a name (no slashes).", nameof(newName));
            }

            lock (_gate)
            {
                TagDefinition tag = RequireLocked(tagId);
                TagDefinition clash = ChildNamedLocked(tag.ParentId, name);
                if (clash != null && clash.Id != tagId)
                {
                    throw new InvalidOperationException($"There is already a tag named \"{name}\" here.");
                }

                _tags[tagId] = tag.With(name: name);
                ReorderLocked();
            }

            RaiseDefinitions();
        }

        public void SetColor(string tagId, string color)
        {
            string normalized = Models.PinColors.Normalize(color) ?? throw new ArgumentException("Unknown color.", nameof(color));
            lock (_gate)
            {
                _tags[tagId] = RequireLocked(tagId).With(color: normalized);
                ReorderLocked();
            }

            RaiseDefinitions();
        }

        public void SetDescription(string tagId, string description)
        {
            lock (_gate)
            {
                _tags[tagId] = RequireLocked(tagId).With(description: (description ?? string.Empty).Trim());
                ReorderLocked();
            }

            RaiseDefinitions();
        }

        /// <summary>Moves a tag under another (null: to the top level). Refused when it would make a loop or a name clash.</summary>
        public void SetParent(string tagId, string parentId)
        {
            lock (_gate)
            {
                TagDefinition tag = RequireLocked(tagId);
                if (parentId != null)
                {
                    RequireLocked(parentId);
                    if (DescendantsLocked(tagId).Contains(parentId))
                    {
                        throw new InvalidOperationException("A tag cannot be moved under itself or one of its sub-tags.");
                    }
                }

                TagDefinition clash = ChildNamedLocked(parentId, tag.Name);
                if (clash != null && clash.Id != tagId)
                {
                    throw new InvalidOperationException($"There is already a tag named \"{tag.Name}\" there.");
                }

                _tags[tagId] = tag.With(parentId: parentId, changeParent: true);
                ReorderLocked();
            }

            RaiseDefinitions();
        }

        /// <summary>Deletes the tag and its sub-tags, and takes them off every item (the items themselves are untouched).</summary>
        public void DeleteTag(string tagId)
        {
            lock (_gate)
            {
                IReadOnlyCollection<string> doomed = DescendantsLocked(tagId);
                if (doomed.Count == 0)
                {
                    return;
                }

                foreach (string id in doomed)
                {
                    _tags.Remove(id);
                }

                foreach (TaggedItem item in _items.Values.ToList())
                {
                    if (item.TagIds.Any(doomed.Contains))
                    {
                        PutLocked(item.Path, item.TagIds.Where(id => !doomed.Contains(id)).ToList(), item.Comment);
                    }
                }

                ReorderLocked();
            }

            RaiseDefinitions();
        }

        // ------------------------------------------------------------------ items

        /// <summary>Adds the tag to every item (items that have it keep it once).</summary>
        public void Assign(IEnumerable<string> paths, string tagId) =>
            ChangeItems(paths, (item, tags) =>
            {
                if (!tags.Contains(tagId))
                {
                    tags.Add(tagId);
                }
            }, requireTag: tagId);

        public void Unassign(IEnumerable<string> paths, string tagId) => ChangeItems(paths, (item, tags) => tags.Remove(tagId));

        /// <summary>Gives every item exactly these tags.</summary>
        public void SetTags(IEnumerable<string> paths, IEnumerable<string> tagIds)
        {
            List<string> ids = tagIds?.Distinct().ToList() ?? new List<string>();
            ChangeItems(paths, (item, tags) =>
            {
                tags.Clear();
                tags.AddRange(ids);
            });
        }

        /// <summary>Takes every tag off the items (their comments stay).</summary>
        public void ClearTags(IEnumerable<string> paths) => ChangeItems(paths, (item, tags) => tags.Clear());

        /// <summary>Sets the items' comment (empty removes it).</summary>
        public void SetComment(IEnumerable<string> paths, string comment)
        {
            string text = (comment ?? string.Empty).Trim();
            if (text.Length > 2000)
            {
                text = text.Substring(0, 2000);
            }

            ChangeItems(paths, null, comment: text);
        }

        /// <summary>
        /// The item moved or was renamed (in ByteExplorer, or undone): its tags move with it, and for a folder so do the
        /// tags of everything inside it.
        /// </summary>
        public void MovePath(string from, string to)
        {
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to) || PathUtil.AreSame(from, to) && string.Equals(from, to, StringComparison.Ordinal))
            {
                return;
            }

            var changed = new List<string>();
            lock (_gate)
            {
                foreach (TaggedItem item in _items.Values.ToList())
                {
                    string moved = Rebase(item.Path, from, to);
                    if (moved != null)
                    {
                        _items.Remove(item.Path);
                        _items[moved] = new TaggedItem(moved, item.TagIds, item.Comment, item.ModifiedUtc);
                        changed.Add(item.Path);
                        changed.Add(moved);
                    }
                }
            }

            if (changed.Count > 0)
            {
                Raise(changed, false);
                SaveSoon();
            }
        }

        /// <summary>
        /// Checks the tagged items directly in these folders (just refreshed): one that is gone is set aside, one set
        /// aside that is back (an undone delete or move) gets its tags again.
        /// </summary>
        public void ReconcileFolders(IEnumerable<string> folders)
        {
            List<string> folderList = folders?.Where(f => !string.IsNullOrEmpty(f)).Distinct(PathUtil.Comparer).ToList();
            if (folderList == null || folderList.Count == 0)
            {
                return;
            }

            var changed = new List<string>();
            lock (_gate)
            {
                foreach (string folder in folderList)
                {
                    foreach (TaggedItem item in _items.Values.Where(i => PathUtil.AreSame(PathUtil.GetParent(i.Path), folder)).ToList())
                    {
                        if (!Exists(item.Path))
                        {
                            _items.Remove(item.Path);
                            _graveyard[item.Path] = item;
                            changed.Add(item.Path);
                        }
                    }

                    foreach (TaggedItem item in _graveyard.Values.Where(i => PathUtil.AreSame(PathUtil.GetParent(i.Path), folder)).ToList())
                    {
                        if (Exists(item.Path) && !_items.ContainsKey(item.Path))
                        {
                            _graveyard.Remove(item.Path);
                            _items[item.Path] = item;
                            changed.Add(item.Path);
                        }
                    }
                }

                while (_graveyard.Count > GraveyardLimit)
                {
                    _graveyard.Remove(_graveyard.Keys.First());
                }
            }

            if (changed.Count > 0)
            {
                Raise(changed, false);
                SaveSoon();
            }
        }

        /// <summary>
        /// Removes the items that no longer exist, but only those whose folder is still there (an unplugged drive, an
        /// offline share or a missing parent folder keeps its items' tags). Runs the checks off the calling thread.
        /// </summary>
        public async Task<int> PruneMissingAsync(CancellationToken cancellationToken = default)
        {
            List<TaggedItem> snapshot;
            lock (_gate)
            {
                snapshot = _items.Values.ToList();
            }

            List<string> gone = await Task.Run(() => snapshot
                .Where(item =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string parent = PathUtil.GetParent(item.Path);
                    return !string.IsNullOrEmpty(parent) && Exists(parent) && !Exists(item.Path);
                })
                .Select(item => item.Path)
                .ToList(), cancellationToken).ConfigureAwait(true);

            if (gone.Count == 0)
            {
                return 0;
            }

            lock (_gate)
            {
                foreach (string path in gone)
                {
                    _items.Remove(path);
                }
            }

            _logger.Information("Removed the tags of {Count} items that no longer exist", gone.Count);
            Raise(gone, false);
            SaveSoon();
            return gone.Count;
        }

        // ------------------------------------------------------------------ persistence

        public async Task LoadAsync(CancellationToken cancellationToken = default)
        {
            if (_filePath == null)
            {
                return;
            }

            TagFile file = null;
            try
            {
                if (File.Exists(_filePath))
                {
                    string json = await File.ReadAllTextAsync(_filePath, cancellationToken).ConfigureAwait(false);
                    file = JsonSerializer.Deserialize<TagFile>(json, JsonOptions);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A broken file is kept aside for inspection; the app starts with no tags rather than failing.
                _logger.Warning(ex, "Could not read the tags file {Path}; starting without tags", _filePath);
                TryCopy(_filePath, _filePath + ".corrupt");
                file = null;
            }

            lock (_gate)
            {
                _tags.Clear();
                _items.Clear();
                _graveyard.Clear();
                if (file != null)
                {
                    LoadLocked(file);
                }

                ReorderLocked();
            }

            _logger.Information("Tags loaded: {Tags} tags on {Items} items", _tags.Count, _items.Count);
            Raise(null, true);
        }

        /// <summary>Saves now (a pending debounced save included); call before the app exits.</summary>
        public async Task FlushAsync()
        {
            _saveDelay?.Cancel();
            await SaveNowAsync().ConfigureAwait(false);
        }

        private void LoadLocked(TagFile file)
        {
            foreach (TagFileTag saved in file.Tags ?? new List<TagFileTag>())
            {
                string name = CleanName(saved?.Name);
                if (saved == null || string.IsNullOrWhiteSpace(saved.Id) || name == null || _tags.ContainsKey(saved.Id))
                {
                    continue;
                }

                _tags[saved.Id] = new TagDefinition(saved.Id, name, Models.PinColors.Normalize(saved.Color) ?? "Gray", saved.Parent, saved.Description);
            }

            // Parents that are missing, or loops (a hand-edited file), make the tag a top-level one.
            foreach (TagDefinition tag in _tags.Values.ToList())
            {
                if (tag.ParentId != null && (!_tags.ContainsKey(tag.ParentId) || HasLoopLocked(tag.Id)))
                {
                    _tags[tag.Id] = tag.With(parentId: null, changeParent: true);
                }
            }

            foreach (TagFileItem saved in file.Items ?? new List<TagFileItem>())
            {
                if (saved == null || string.IsNullOrWhiteSpace(saved.Path))
                {
                    continue;
                }

                string path = PortablePaths.Resolve(saved.Path);
                List<string> ids = (saved.Tags ?? new List<string>()).Where(_tags.ContainsKey).Distinct().ToList();
                var item = new TaggedItem(path, ids, saved.Comment, saved.Modified);
                if (!item.IsEmpty)
                {
                    _items[path] = item;
                }
            }
        }

        private void SaveSoon()
        {
            if (_filePath == null)
            {
                return;
            }

            _saveDelay?.Cancel();
            var cts = new CancellationTokenSource();
            _saveDelay = cts;
            Task.Delay(SaveDelayMs, cts.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled)
                {
                    SaveNowAsync().Forget("TagStore.Save");
                }
            }, TaskScheduler.Default).Forget("TagStore.SaveSoon");
        }

        private async Task SaveNowAsync()
        {
            if (_filePath == null)
            {
                return;
            }

            TagFile file;
            lock (_gate)
            {
                file = new TagFile
                {
                    Tags = _ordered.Select(t => new TagFileTag { Id = t.Id, Name = t.Name, Color = t.Color, Parent = t.ParentId, Description = string.IsNullOrEmpty(t.Description) ? null : t.Description }).ToList(),
                    Items = _items.Values
                        .OrderBy(i => i.Path, PathUtil.Comparer)
                        .Select(i => new TagFileItem { Path = PortablePaths.ToPortable(i.Path), Tags = i.TagIds.ToList(), Comment = i.Comment.Length == 0 ? null : i.Comment, Modified = i.ModifiedUtc })
                        .ToList()
                };
            }

            await _saveGate.WaitAsync().ConfigureAwait(false);
            try
            {
                string json = JsonSerializer.Serialize(file, JsonOptions);
                string temp = _filePath + ".tmp";
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath) ?? ".");
                await File.WriteAllTextAsync(temp, json).ConfigureAwait(false);
                File.Move(temp, _filePath, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Could not save the tags to {Path}; changes last this session", _filePath);
            }
            finally
            {
                _saveGate.Release();
            }
        }

        // ------------------------------------------------------------------ helpers

        private void ChangeItems(IEnumerable<string> paths, Action<TaggedItem, List<string>> edit, string comment = null, string requireTag = null)
        {
            List<string> list = paths?.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(PathUtil.Comparer).ToList();
            if (list == null || list.Count == 0)
            {
                return;
            }

            lock (_gate)
            {
                if (requireTag != null)
                {
                    RequireLocked(requireTag);
                }

                foreach (string path in list)
                {
                    _items.TryGetValue(path, out TaggedItem item);
                    var tags = item?.TagIds.ToList() ?? new List<string>();
                    edit?.Invoke(item, tags);
                    PutLocked(path, tags.Where(_tags.ContainsKey).Distinct().ToList(), comment ?? item?.Comment);
                }
            }

            Raise(list, false);
            SaveSoon();
        }

        private void PutLocked(string path, List<string> tagIds, string comment)
        {
            var item = new TaggedItem(path, tagIds, comment, DateTime.UtcNow);
            if (item.IsEmpty)
            {
                _items.Remove(path);
            }
            else
            {
                _items[path] = item;
            }

            _graveyard.Remove(path);
        }

        private void RaiseDefinitions()
        {
            Raise(null, true);
            SaveSoon();
        }

        private void Raise(IReadOnlyCollection<string> paths, bool definitions)
        {
            try
            {
                Interlocked.Increment(ref _version);
                var args = new TagsChangedEventArgs(paths, definitions);
                Changed?.Invoke(this, args);
                AnyChanged?.Invoke(this, args);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "A tags change handler failed");
            }
        }

        private TagDefinition RequireLocked(string tagId) =>
            tagId != null && _tags.TryGetValue(tagId, out TagDefinition tag) ? tag : throw new ArgumentException("The tag does not exist.", nameof(tagId));

        private TagDefinition ChildNamedLocked(string parentId, string name) =>
            _tags.Values.FirstOrDefault(t => t.ParentId == parentId && string.Equals(t.Name, name, StringComparison.CurrentCultureIgnoreCase));

        private IReadOnlyCollection<string> DescendantsLocked(string tagId)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (tagId == null || !_tags.ContainsKey(tagId))
            {
                return result;
            }

            var pending = new Stack<string>();
            pending.Push(tagId);
            while (pending.Count > 0)
            {
                string id = pending.Pop();
                if (!result.Add(id))
                {
                    continue;
                }

                foreach (TagDefinition child in _tags.Values)
                {
                    if (child.ParentId == id)
                    {
                        pending.Push(child.Id);
                    }
                }
            }

            return result;
        }

        private bool HasLoopLocked(string tagId)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (string id = tagId; id != null && _tags.TryGetValue(id, out TagDefinition tag); id = tag.ParentId)
            {
                if (!seen.Add(id))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The tree order: each tag followed by its sub-tags, siblings by name.</summary>
        private void ReorderLocked()
        {
            var ordered = new List<TagDefinition>(_tags.Count);
            ILookup<string, TagDefinition> byParent = _tags.Values.ToLookup(t => t.ParentId ?? string.Empty);

            void Visit(string parentKey, int depth)
            {
                if (depth > 64)
                {
                    return;
                }

                foreach (TagDefinition tag in byParent[parentKey].OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    ordered.Add(tag);
                    Visit(tag.Id, depth + 1);
                }
            }

            Visit(string.Empty, 0);
            _ordered = ordered;
        }

        private string NextColorLocked()
        {
            // New top-level tags take the palette in turn, so a fresh set of tags is told apart at a glance.
            IReadOnlyList<(string Name, string Hex)> all = Models.PinColors.All;
            string color = all[_colorCursor % all.Count].Name;
            _colorCursor = (_colorCursor + 1) % all.Count;
            return color;
        }

        private static string NewId() => Guid.NewGuid().ToString("N").Substring(0, 12);

        /// <summary>A tag name: trimmed, no slashes (they separate sub-tags), at most 80 characters; null when empty.</summary>
        public static string CleanName(string name)
        {
            string trimmed = name?.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.Contains('/'))
            {
                return null;
            }

            return trimmed.Length > 80 ? trimmed.Substring(0, 80) : trimmed;
        }

        private static string[] SplitPath(string name) =>
            (name ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => part.Length > 80 ? part.Substring(0, 80) : part)
                .ToArray();

        /// <summary>The path moved from <paramref name="from"/> to <paramref name="to"/>, or null when it is not at or under it.</summary>
        private static string Rebase(string path, string from, string to)
        {
            if (PathUtil.AreSame(path, from))
            {
                return to;
            }

            if (!PathUtil.IsInside(path, from))
            {
                return null;
            }

            string relative = path.Substring(PathUtil.WithTrailingSeparator(from).Length);
            return Path.Combine(to, relative);
        }

        private static bool Exists(string path)
        {
            try
            {
                return File.Exists(path) || Directory.Exists(path);
            }
            catch (Exception)
            {
                return true; // unknown: keep the tags
            }
        }

        private static void TryCopy(string from, string to)
        {
            try
            {
                File.Copy(from, to, overwrite: true);
            }
            catch (Exception)
            {
                // Nothing more to do; the warning above is in the log.
            }
        }
    }
}
