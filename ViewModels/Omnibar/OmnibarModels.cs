// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ByteExplorer.Core.Models;
using ByteExplorer.Icons;

namespace ByteExplorer.ViewModels.Omnibar
{
    /// <summary>What choosing a suggestion does.</summary>
    public enum OmnibarActionKind
    {
        /// <summary>Nothing: an informational row ("No folder here").</summary>
        None,

        /// <summary>Open a folder (or a ZIP file as a folder).</summary>
        Navigate,

        /// <summary>Open the file's folder and select the file.</summary>
        Reveal,

        /// <summary>Connect to a network address, then open it.</summary>
        OpenNetwork,

        /// <summary>Open a web address in the browser.</summary>
        OpenWebAddress,

        RunCommand,

        /// <summary>Filter the current folder by the text.</summary>
        FilterHere,

        /// <summary>Search the whole index for the text.</summary>
        SearchEverywhere,

        /// <summary>The This PC page.</summary>
        ThisPc,

        /// <summary>The Home dashboard (recent and frequent folders).</summary>
        Dashboard,

        /// <summary>The trash / Recycle Bin view.</summary>
        Trash
    }

    /// <summary>One row of the omnibar's suggestion list.</summary>
    public sealed class OmnibarSuggestion
    {
        public OmnibarSuggestion(string title, Symbol icon, OmnibarActionKind action, string target)
        {
            Title = title;
            Icon = icon;
            Action = action;
            Target = target;
        }

        public string Title { get; }

        /// <summary>Character indexes of <see cref="Title"/> that matched what was typed (shown bold).</summary>
        public int[] TitleHighlights { get; init; } = Array.Empty<int>();

        /// <summary>A second, dimmer line: the folder an item is in, a size, a description.</summary>
        public string Subtitle { get; init; }

        public Symbol Icon { get; }

        /// <summary>Section title ("Folders", "Recent", "Commands"); shown above the first row of each section.</summary>
        public string Group { get; init; }

        /// <summary>Set by the engine: this row starts a section.</summary>
        public bool IsGroupStart { get; internal set; }

        /// <summary>Right-aligned hint: a keyboard shortcut, or what Enter does.</summary>
        public string Hint { get; init; }

        public OmnibarActionKind Action { get; }

        /// <summary>The path, address or text the action uses.</summary>
        public string Target { get; }

        public OmnibarCommand Command { get; init; }

        /// <summary>The text Tab puts in the box (completing a folder ends with a separator, to keep going).</summary>
        public string CompletionText { get; init; }

        /// <summary>What appears faded after the typed text when this is the top suggestion; null for none.</summary>
        public string GhostSuffix { get; init; }

        public int Score { get; init; }

        public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);

        public bool HasHint => !string.IsNullOrEmpty(Hint);

        public bool IsActionable => Action != OmnibarActionKind.None;
    }

    /// <summary>A command the omnibar can run ("&gt; new folder").</summary>
    public sealed class OmnibarCommand
    {
        public OmnibarCommand(string title, string keywords, Symbol icon, string gesture, Func<bool> canRun, Action run)
        {
            Title = title;
            Keywords = keywords ?? string.Empty;
            Icon = icon;
            Gesture = gesture;
            CanRun = canRun ?? (() => true);
            Run = run ?? throw new ArgumentNullException(nameof(run));
        }

        public string Title { get; }

        /// <summary>Other words it answers to ("mkdir create directory").</summary>
        public string Keywords { get; }

        public Symbol Icon { get; }
        public string Gesture { get; }
        public Func<bool> CanRun { get; }
        public Action Run { get; }
    }

    public enum OmnibarPlaceKind
    {
        Place,
        Drive,
        Pinned,
        Network
    }

    /// <summary>A named place: a special folder, a drive, a pinned folder, a network location, This PC.</summary>
    public sealed class OmnibarPlace
    {
        public OmnibarPlace(string name, string target, Symbol icon, OmnibarPlaceKind kind, OmnibarActionKind action, string subtitle = null)
        {
            Name = name;
            Target = target;
            Icon = icon;
            Kind = kind;
            Action = action;
            Subtitle = subtitle;
        }

        public string Name { get; }
        public string Target { get; }
        public Symbol Icon { get; }
        public OmnibarPlaceKind Kind { get; }
        public OmnibarActionKind Action { get; }
        public string Subtitle { get; }
    }

    /// <summary>What the suggestion engine reads from the app. Implemented by the view model; faked in tests.</summary>
    public interface IOmnibarContext
    {
        /// <summary>The active pane's folder; null on the Home dashboard and This PC.</summary>
        string CurrentFolder { get; }

        string HomeFolder { get; }

        bool ShowHidden { get; }

        /// <summary>A folder's entries (cached, network timeouts, ZIP folders); null when it can't be listed.</summary>
        Task<IReadOnlyList<DirectoryIndexEntry>> ListAsync(string folder, CancellationToken cancellationToken);

        /// <summary>Recently and frequently visited folders, most relevant first.</summary>
        IReadOnlyList<string> RecentFolders { get; }

        IReadOnlyList<OmnibarPlace> Places { get; }

        IReadOnlyList<OmnibarCommand> Commands { get; }
    }
}
