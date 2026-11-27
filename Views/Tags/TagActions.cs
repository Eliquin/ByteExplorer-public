// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using ByteExplorer.Core.Models;
using ByteExplorer.Core.Tags;
using ByteExplorer.Icons;
using ByteExplorer.Infrastructure;
using Serilog;

namespace ByteExplorer.Views.Tags
{
    /// <summary>
    /// The tag actions shared by the file context menu, the sidebar's Tags section and the tag editor: creating a tag (a
    /// name with slashes creates its sub-tags), renaming, recoloring, deleting, and the "Tags" submenu of selected items.
    /// </summary>
    public static class TagActions
    {
        /// <summary>The most tags the quick submenu lists; the rest are a click away in the editor.</summary>
        private const int QuickMenuLimit = 25;

        /// <summary>Why a typed tag name is refused, or null when it is fine (slashes separate sub-tags).</summary>
        public static string ValidateName(string text)
        {
            string[] parts = (text ?? string.Empty).Split('/', StringSplitOptions.TrimEntries);
            if (parts.All(p => p.Length == 0))
            {
                return "Type a name.";
            }

            if (parts.Any(p => p.Length == 0))
            {
                return "Each part between slashes needs a name.";
            }

            return parts.Any(p => p.Length > 80) ? "Names can be up to 80 characters long." : null;
        }

        /// <summary>Asks for a name and creates the tag (under <paramref name="parentId"/>); null when cancelled.</summary>
        public static async Task<TagDefinition> PromptNewTagAsync(IDialogService dialogs, TagStore store, string parentId = null)
        {
            if (dialogs == null)
            {
                return null;
            }

            string parentName = parentId == null ? null : store.FullName(store.Find(parentId));
            string name = await dialogs.PromptTextAsync(
                parentName == null ? "New tag" : "New sub-tag",
                parentName == null
                    ? "Name the tag. Use a slash for sub-tags: \"Work/Project A\" creates Project A under Work."
                    : $"A sub-tag of {parentName.Replace("/", " › ")}. Slashes create deeper sub-tags.",
                string.Empty, "Tag name", "Create", ValidateName).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            try
            {
                return store.CreateTag(name, parentId: parentId);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
            {
                await dialogs.ShowMessageAsync("New tag", ex.Message).ConfigureAwait(true);
                return null;
            }
        }

        public static async Task RenameAsync(IDialogService dialogs, TagStore store, TagDefinition tag)
        {
            if (dialogs == null || tag == null)
            {
                return;
            }

            string name = await dialogs.PromptTextAsync("Rename tag", "The new name of the tag (its items keep it).", tag.Name, "Tag name", "Rename",
                text => TagStore.CleanName(text) == null ? "Type a name without slashes." : null).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(name) || name == tag.Name)
            {
                return;
            }

            try
            {
                store.RenameTag(tag.Id, name);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
            {
                await dialogs.ShowMessageAsync("Rename tag", ex.Message).ConfigureAwait(true);
            }
        }

        public static async Task DeleteAsync(IDialogService dialogs, TagStore store, TagDefinition tag)
        {
            if (tag == null)
            {
                return;
            }

            int subTags = store.WithDescendants(tag.Id).Count - 1;
            int items = store.CountItems(tag.Id);
            string what = subTags > 0 ? $"\"{tag.Name}\" and its {subTags} sub-tag{(subTags == 1 ? string.Empty : "s")}" : $"\"{tag.Name}\"";
            string message = $"Delete the tag {what}? It is taken off {items:N0} item{(items == 1 ? string.Empty : "s")}; the items themselves are not changed.";
            if (dialogs != null && !await dialogs.ConfirmAsync("Delete tag", message, "Delete").ConfigureAwait(true))
            {
                return;
            }

            store.DeleteTag(tag.Id);
        }

        /// <summary>A "Color" submenu with the palette, the tag's color checked.</summary>
        public static MenuItem ColorMenu(TagStore store, TagDefinition tag)
        {
            var colors = new List<Control>();
            foreach ((string name, string hex) in PinColors.All)
            {
                var item = new MenuItem
                {
                    Header = name,
                    Icon = Dot(hex, 12),
                    ToggleType = MenuItemToggleType.Radio,
                    GroupName = "tagcolor",
                    IsChecked = string.Equals(tag.Color, name, StringComparison.OrdinalIgnoreCase)
                };
                string chosen = name;
                item.Click += (_, _) => Guard(() => store.SetColor(tag.Id, chosen));
                colors.Add(item);
            }

            return new MenuItem { Header = "Color", Icon = Dot(tag.ColorHex, 12), ItemsSource = colors };
        }

        /// <summary>
        /// The "Tags" submenu for selected items. Quick: every tag (up to a limit) with a check when all the items have it;
        /// clicking adds it to all of them, or takes it off when they all have it. Then New tag…, the detailed editor
        /// (tags and comment) and Remove all tags.
        /// </summary>
        public static MenuItem BuildItemsMenu(IReadOnlyList<string> paths, TagStore store, IDialogService dialogs, Func<Window> owner)
        {
            var children = new List<Control>();
            IReadOnlyList<TagDefinition> tags = store.Tags;
            List<IReadOnlyList<TagDefinition>> current = paths.Select(store.GetTags).ToList();
            bool anyTagged = current.Any(list => list.Count > 0);

            foreach (TagDefinition tag in tags.Take(QuickMenuLimit))
            {
                int having = current.Count(list => list.Any(t => t.Id == tag.Id));
                bool all = having == paths.Count;
                int depth = store.Depth(tag);
                var item = new MenuItem
                {
                    Header = new string(' ', depth * 3) + tag.Name + (having > 0 && !all ? $"  ({having} of {paths.Count})" : string.Empty),
                    Icon = Dot(tag.ColorHex, 11),
                    ToggleType = MenuItemToggleType.CheckBox,
                    IsChecked = all,
                    StaysOpenOnClick = true
                };
                string id = tag.Id;
                item.Click += (_, _) => Guard(() =>
                {
                    bool everyone = paths.All(p => store.GetTags(p).Any(t => t.Id == id));
                    if (everyone)
                    {
                        store.Unassign(paths, id);
                    }
                    else
                    {
                        store.Assign(paths, id);
                    }

                    item.IsChecked = !everyone;
                    item.Header = new string(' ', depth * 3) + store.Find(id)?.Name;
                });
                children.Add(item);
            }

            if (tags.Count > QuickMenuLimit)
            {
                children.Add(new MenuItem { Header = $"{tags.Count - QuickMenuLimit} more in the tag editor…", IsEnabled = false });
            }

            if (tags.Count > 0)
            {
                children.Add(new Separator());
            }

            var create = new MenuItem { Header = "New tag…", Icon = Symbolic(Symbol.Add) };
            create.Click += async (_, _) =>
            {
                try
                {
                    TagDefinition tag = await PromptNewTagAsync(dialogs, store).ConfigureAwait(true);
                    if (tag != null)
                    {
                        store.Assign(paths, tag.Id);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Creating a tag failed");
                }
            };
            children.Add(create);

            var edit = new MenuItem { Header = paths.Count == 1 ? "Edit tags and comment…" : $"Edit tags and comment of {paths.Count} items…", Icon = Symbolic(Symbol.Bookmark) };
            edit.Click += (_, _) => Guard(() => TagEditorWindow.ShowFor(owner?.Invoke(), store, paths));
            children.Add(edit);

            var clear = new MenuItem { Header = "Remove all tags", Icon = Symbolic(Symbol.Dismiss), IsEnabled = anyTagged };
            clear.Click += (_, _) => Guard(() => store.ClearTags(paths));
            children.Add(clear);

            return new MenuItem { Header = "Tags", Icon = Symbolic(Symbol.Bookmark), ItemsSource = children };
        }

        /// <summary>A small filled circle of a tag color (menu icons, the editor's rows).</summary>
        public static Control Dot(string hex, double size) => new Ellipse
        {
            Width = size,
            Height = size,
            Fill = new SolidColorBrush(Color.Parse(hex ?? "#8B8D98")),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

        private static SymbolIcon Symbolic(Symbol symbol) => new SymbolIcon { Symbol = symbol, FontSize = 16 };

        private static void Guard(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "A tag action failed");
            }
        }
    }
}
