// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;

namespace ByteExplorer.Core.Tags
{
    /// <summary>
    /// A tag: a name, a color and an optional parent (sub-tags: "Work" › "Project A"). Immutable; the store replaces the
    /// instance when a tag changes, so a reader holding one never sees it half-updated.
    /// </summary>
    public sealed class TagDefinition
    {
        public TagDefinition(string id, string name, string color, string parentId, string description)
        {
            Id = id;
            Name = name;
            Color = color;
            ParentId = parentId;
            Description = description ?? string.Empty;
        }

        /// <summary>Stable identity (assignments refer to it, so renaming or moving a tag keeps its items).</summary>
        public string Id { get; }

        /// <summary>The tag's own name ("Project A"); its full name includes its parents ("Work/Project A").</summary>
        public string Name { get; }

        /// <summary>One of <see cref="Models.PinColors"/> by name ("Blue").</summary>
        public string Color { get; }

        /// <summary>The parent tag's id, or null for a top-level tag.</summary>
        public string ParentId { get; }

        /// <summary>What the tag is for (shown in tool tips and the tag editor).</summary>
        public string Description { get; }

        /// <summary>The color as #RRGGBB (Gray for an unknown name).</summary>
        public string ColorHex => Models.PinColors.HexOf(Color) ?? Models.PinColors.HexOf("Gray");

        internal TagDefinition With(string name = null, string color = null, string parentId = null, bool changeParent = false, string description = null) =>
            new TagDefinition(Id, name ?? Name, color ?? Color, changeParent ? parentId : ParentId, description ?? Description);

        public override string ToString() => Name;
    }

    /// <summary>The tags of one file or folder, and its comment.</summary>
    public sealed class TaggedItem
    {
        internal TaggedItem(string path, IReadOnlyList<string> tagIds, string comment, DateTime modifiedUtc)
        {
            Path = path;
            TagIds = tagIds;
            Comment = comment ?? string.Empty;
            ModifiedUtc = modifiedUtc;
        }

        public string Path { get; }

        public IReadOnlyList<string> TagIds { get; }

        public string Comment { get; }

        public DateTime ModifiedUtc { get; }

        internal bool IsEmpty => TagIds.Count == 0 && Comment.Length == 0;
    }

    /// <summary>What changed in the store: some items' tags (their paths), the tags themselves, or everything.</summary>
    public sealed class TagsChangedEventArgs : EventArgs
    {
        internal TagsChangedEventArgs(IReadOnlyCollection<string> paths, bool definitionsChanged)
        {
            Paths = paths;
            DefinitionsChanged = definitionsChanged;
        }

        /// <summary>The items whose tags or comment changed; null when it may be any (a load, a tag renamed or deleted).</summary>
        public IReadOnlyCollection<string> Paths { get; }

        /// <summary>Tags were created, renamed, recolored, moved or deleted (the sidebar rebuilds its list).</summary>
        public bool DefinitionsChanged { get; }
    }

    /// <summary>The tags file as saved (tags.json in the data folder).</summary>
    internal sealed class TagFile
    {
        public int Version { get; set; } = 1;

        public List<TagFileTag> Tags { get; set; } = new List<TagFileTag>();

        public List<TagFileItem> Items { get; set; } = new List<TagFileItem>();
    }

    internal sealed class TagFileTag
    {
        public string Id { get; set; }

        public string Name { get; set; }

        public string Color { get; set; }

        public string Parent { get; set; }

        public string Description { get; set; }
    }

    internal sealed class TagFileItem
    {
        /// <summary>Saved portable (PortablePaths): the user's folders as tokens, so the tags follow the app to another computer.</summary>
        public string Path { get; set; }

        public List<string> Tags { get; set; } = new List<string>();

        public string Comment { get; set; }

        public DateTime Modified { get; set; }
    }
}
