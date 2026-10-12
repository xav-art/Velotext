// Velotext: the main window's view model (tree, editor selection, search, commands, status).
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Velotext.App.Services;
using Velotext.Core.Model;
using Velotext.Core.Search;
using Velotext.Core.Storage;

namespace Velotext.App.ViewModels;

/// <summary>Where a dragged node lands relative to the target.</summary>
public enum DropPosition
{
    /// <summary>Above the target, in the same folder.</summary>
    Before = 0,

    /// <summary>Below the target, in the same folder.</summary>
    After = 1,

    /// <summary>Inside the target folder, at the end.</summary>
    Inside = 2,
}

/// <summary>Pauses and resumes expansion.</summary>
public interface IPauseControl
{
    /// <summary>Raised when the paused state changes, on any thread.</summary>
    event EventHandler<bool>? PausedChanged;

    /// <summary>True while expansion is paused.</summary>
    bool IsPaused { get; }

    /// <summary>Pauses or resumes expansion.</summary>
    /// <param name="paused">True to pause.</param>
    void SetPaused(bool paused);
}

/// <summary>
/// The main window's state. The tree of view models is the editing copy of the library: every edit marks
/// it dirty, and a short moment later the whole tree is converted to an immutable library and handed to
/// <see cref="LibraryService"/>, which validates, publishes and saves it.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private const int ShortcutCount = 10;

    private readonly LibraryService _library;
    private readonly SettingsService _settings;
    private readonly UsageService _usage;
    private readonly IPauseControl _pause;
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private readonly EditContext _context;
    private readonly Debouncer _commit;
    private readonly Dictionary<Guid, PhraseViewModel> _phrases = [];
    private const string SaveFailedBanner = "Velotext cannot save your phrases. Check that your disk has free space and that the library file is not read-only. Velotext tries again after your next change.";

    private bool _dirty;
    private bool _building;

    /// <summary>The selected tree node.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DuplicateCommand), nameof(DeleteCommand), nameof(MoveUpCommand), nameof(MoveDownCommand), nameof(RenameCommand), nameof(MoveToFolderCommand), nameof(ExportCommand))]
    private TreeNodeViewModel? _selectedNode;

    /// <summary>The phrase shown in the editor, or <c>null</c>.</summary>
    [ObservableProperty]
    private PhraseViewModel? _editorPhrase;

    /// <summary>The search box text.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoMatchesText))]
    private string _searchText = string.Empty;

    /// <summary>True when a search hides every phrase.</summary>
    [ObservableProperty]
    private bool _hasNoMatches;

    /// <summary>True while expansion is paused.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpansionStatus), nameof(PauseButtonText))]
    private bool _isPaused;

    /// <summary>Whether the library is saved.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveStatus), nameof(IsSaveFailed))]
    private SaveState _saveState;

    /// <summary>Number of phrases in the library.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhraseCountText), nameof(IsLibraryEmpty), nameof(EmptyEditorTitle), nameof(EmptyEditorMessage))]
    private int _phraseCount;

    /// <summary>Number of trigger problems.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProblemCountText), nameof(ProblemButtonName))]
    private int _problemCount;

    /// <summary>How often the phrase in the editor was inserted in the last 30 days, as a sentence.</summary>
    [ObservableProperty]
    private string _editorUsageText = string.Empty;

    /// <summary>A message shown at the top of the window, or <c>null</c>.</summary>
    [ObservableProperty]
    private string? _banner;

    /// <summary>True when the banner reports an error (red) rather than a warning (amber).</summary>
    [ObservableProperty]
    private bool _isBannerError;

    /// <summary>Font family of the phrase editor (the output font from settings).</summary>
    [ObservableProperty]
    private string _editorFontName;

    /// <summary>Font size in points of the phrase editor.</summary>
    [ObservableProperty]
    private double _editorFontSize;

    /// <summary>Creates the view model.</summary>
    /// <param name="library">The library service.</param>
    /// <param name="settings">The settings service.</param>
    /// <param name="usage">The usage service.</param>
    /// <param name="pause">Pause control.</param>
    /// <param name="dialogs">Dialogs.</param>
    /// <param name="ui">UI dispatcher.</param>
    /// <param name="time">Clock.</param>
    public MainViewModel(LibraryService library, SettingsService settings, UsageService usage, IPauseControl pause, IDialogService dialogs, IUiDispatcher ui, TimeProvider time)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _usage = usage ?? throw new ArgumentNullException(nameof(usage));
        _pause = pause ?? throw new ArgumentNullException(nameof(pause));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _context = new EditContext(MarkDirty, () => _settings.Current.NewTriggerDefaults, time);
        _commit = new Debouncer(() => _ui.Post(CommitNow), TimeSpan.FromMilliseconds(300));

        TreeRoots = [Recent, MostUsed];
        TreeRoots.CollectionChanged += OnTreeRootsChanged;
        BuildTree(_library.Snapshot.Library);
        ApplyIssues(_library.Snapshot);
        RefreshUsage();
        _isPaused = _pause.IsPaused;
        _saveState = _library.SaveState;
        _editorFontName = _settings.Current.RichTextFontName;
        _editorFontSize = _settings.Current.RichTextFontSize;
        Banner = _library.LoadStatus switch
        {
            LoadStatus.Recovered => "The library file was damaged. Velotext loaded your most recent backup.",
            LoadStatus.Failed => "The library file was damaged and no backup was found. Velotext kept the damaged file and started a new library.",
            _ => null,
        };

        _library.Changed += OnLibraryChanged;
        _library.IssuesChanged += OnIssuesChanged;
        _library.SaveStateChanged += OnSaveStateChanged;
        _usage.Changed += OnUsageChanged;
        _pause.PausedChanged += OnPausedChanged;
        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>Raised when the editor should focus the phrase name (after create or rename).</summary>
    public event EventHandler<PhraseViewModel>? FocusPhraseNameRequested;

    /// <summary>Raised when the tree should move keyboard focus to a node (after delete, move or import).</summary>
    public event EventHandler<TreeNodeViewModel>? FocusNodeRequested;

    /// <summary>Raised when the settings window should open.</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>Top-level tree items: "Recently used" and "Most used" first, then the library.</summary>
    public ObservableCollection<TreeNodeViewModel> TreeRoots { get; }

    /// <summary>The "Recently used" folder.</summary>
    public ShortcutFolderViewModel Recent { get; } = ShortcutFolderViewModel.CreateRecent();

    /// <summary>The "Most used" folder (last 30 days).</summary>
    public ShortcutFolderViewModel MostUsed { get; } = ShortcutFolderViewModel.CreateMostUsed();

    /// <summary>True when the library holds no phrases.</summary>
    public bool IsLibraryEmpty => PhraseCount == 0;

    /// <summary>Title of the editor area when no phrase is selected.</summary>
    public string EmptyEditorTitle => IsLibraryEmpty ? "You have no phrases yet" : "No phrase selected";

    /// <summary>Message of the editor area when no phrase is selected.</summary>
    public string EmptyEditorMessage => IsLibraryEmpty
        ? "Click New phrase. Then type a trigger for it, for example ;thx."
        : "To edit a phrase, select it in the list.";

    /// <summary>Message shown when a search finds nothing.</summary>
    public string NoMatchesText => $"No phrases match \"{SearchText.Trim()}\". Try other words, or press Esc to clear the search.";

    /// <summary>Accessible name of the status bar problem button (it starts with the visible text).</summary>
    public string ProblemButtonName => ProblemCountText + ", show next";

    /// <summary>True when the last save did not work.</summary>
    public bool IsSaveFailed => SaveState == SaveState.Failed;

    /// <summary>Status bar text for the expansion state.</summary>
    public string ExpansionStatus => IsPaused ? "Expansion paused" : "Expansion on";

    /// <summary>Label of the pause button.</summary>
    public string PauseButtonText => IsPaused ? "Resume expansion" : "Pause expansion";

    /// <summary>Status bar text for the phrase count.</summary>
    public string PhraseCountText => PhraseCount == 1 ? "1 phrase" : string.Create(CultureInfo.InvariantCulture, $"{PhraseCount} phrases");

    /// <summary>Status bar text for problems, empty when there are none.</summary>
    public string ProblemCountText => ProblemCount switch
    {
        0 => string.Empty,
        1 => "1 trigger problem",
        _ => string.Create(CultureInfo.InvariantCulture, $"{ProblemCount} trigger problems"),
    };

    /// <summary>Status bar text for the save state.</summary>
    public string SaveStatus => SaveState switch
    {
        SaveState.Pending => "Saving",
        SaveState.Failed => "Not saved",
        _ => "All changes saved",
    };

    /// <summary>All phrases currently in the tree, by id.</summary>
    internal IReadOnlyDictionary<Guid, PhraseViewModel> Phrases => _phrases;

    /// <summary>Converts the tree to an immutable library.</summary>
    public PhraseLibrary BuildLibrary() => new()
    {
        Items = [.. TreeRoots.Where(n => n is not ShortcutFolderViewModel).Select(n => n.ToModel())],
    };

    /// <summary>Hands pending edits to the library service now.</summary>
    public void CommitNow()
    {
        if (!_dirty)
        {
            return;
        }

        _dirty = false;
        _library.Update(BuildLibrary());
        PhraseCount = _phrases.Count;
    }

    /// <summary>Replaces the whole library, for example with a backup.</summary>
    /// <param name="library">The new library.</param>
    public void ReplaceLibrary(PhraseLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        BuildTree(LibrarySanitizer.Repair(library, _time.GetUtcNow()));
        _dirty = true;
        CommitNow();
        RefreshUsage();
    }

    /// <summary>Moves a node by drag and drop.</summary>
    /// <param name="node">The dragged node.</param>
    /// <param name="target">The node it was dropped on.</param>
    /// <param name="position">Where it lands.</param>
    /// <returns>True when the move happened.</returns>
    public bool Move(TreeNodeViewModel node, TreeNodeViewModel target, DropPosition position)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(target);
        if (!CanMove(node, target, position))
        {
            return false;
        }

        CollectionOf(node).Remove(node);
        if (position == DropPosition.Inside && target is FolderViewModel folder)
        {
            node.Parent = folder;
            folder.Children.Add(node);
            folder.IsExpanded = true;
        }
        else
        {
            var siblings = CollectionOf(target);
            var index = siblings.IndexOf(target) + (position == DropPosition.After ? 1 : 0);
            node.Parent = target.Parent;
            siblings.Insert(Math.Max(MinimumIndex(siblings), index), node);
        }

        Select(node);
        FocusNodeRequested?.Invoke(this, node);
        return true;
    }

    /// <summary>The folders a node can move into, as display paths, with <c>null</c> for the top level.</summary>
    /// <param name="node">The node to move.</param>
    public IReadOnlyList<(string Path, FolderViewModel? Folder)> MoveDestinations(TreeNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var result = new List<(string, FolderViewModel?)> { ("Top level of the library", null) };

        void Walk(IEnumerable<TreeNodeViewModel> nodes, string prefix)
        {
            foreach (var folder in nodes.OfType<FolderViewModel>())
            {
                // A folder cannot move into itself or one of its subfolders.
                if (ReferenceEquals(folder, node))
                {
                    continue;
                }

                var path = prefix + folder.DisplayName;
                result.Add((path, folder));
                Walk(folder.Children, path + " / ");
            }
        }

        Walk(TreeRoots.Where(n => n is not ShortcutFolderViewModel), string.Empty);
        return result;
    }

    /// <summary>Moves a node to the end of a folder, or of the top level when <paramref name="folder"/> is <c>null</c>.</summary>
    /// <param name="node">The node to move.</param>
    /// <param name="folder">The destination folder.</param>
    public void MoveInto(TreeNodeViewModel node, FolderViewModel? folder)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.IsReadOnly || (folder is not null && !CanMove(node, folder, DropPosition.Inside)))
        {
            return;
        }

        CollectionOf(node).Remove(node);
        if (folder is null)
        {
            TreeRoots.Add(node);
        }
        else
        {
            node.Parent = folder;
            folder.Children.Add(node);
            folder.IsExpanded = true;
        }

        Select(node);
        FocusNodeRequested?.Invoke(this, node);
    }

    /// <summary>Returns true when <paramref name="node"/> may be dropped on <paramref name="target"/>.</summary>
    /// <param name="node">The dragged node.</param>
    /// <param name="target">The drop target.</param>
    /// <param name="position">Where it would land.</param>
    public static bool CanMove(TreeNodeViewModel node, TreeNodeViewModel target, DropPosition position)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(target);
        if (node.IsReadOnly || target.IsReadOnly || ReferenceEquals(node, target))
        {
            return false;
        }

        if (position == DropPosition.Inside && target is not FolderViewModel)
        {
            return false;
        }

        // A folder cannot go into itself or one of its subfolders.
        for (var ancestor = position == DropPosition.Inside ? target as FolderViewModel : target.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ReferenceEquals(ancestor, node))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Releases event subscriptions and commits pending edits.</summary>
    public void Dispose()
    {
        CommitNow();
        _commit.Dispose();
        _library.Changed -= OnLibraryChanged;
        _library.IssuesChanged -= OnIssuesChanged;
        _library.SaveStateChanged -= OnSaveStateChanged;
        _usage.Changed -= OnUsageChanged;
        _pause.PausedChanged -= OnPausedChanged;
        _settings.Changed -= OnSettingsChanged;
    }

    /// <summary>Creates a phrase next to the selection (or inside the selected folder) and selects it.</summary>
    [RelayCommand]
    private void NewPhrase()
    {
        ClearSearch();
        var now = _time.GetUtcNow();
        var defaults = _settings.Current.NewTriggerDefaults;
        var model = new Phrase
        {
            Id = Guid.NewGuid(),
            Description = "New phrase",
            Triggers = [new AutotextTrigger { Text = string.Empty, CaseSensitive = defaults.CaseSensitive, Immediate = defaults.Immediate, WordStart = defaults.WordStart }],
            CreatedUtc = now,
            ModifiedUtc = now,
        };
        var node = Insert(model);
        FocusPhraseNameRequested?.Invoke(this, (PhraseViewModel)node);
    }

    /// <summary>Creates a folder inside the selected folder, or at the top level, and starts renaming it.</summary>
    [RelayCommand]
    private void NewFolder()
    {
        ClearSearch();
        var folder = (FolderViewModel)CreateNode(new Folder { Id = Guid.NewGuid(), Name = "New folder" }, null);
        if (SelectedNode is FolderViewModel { IsReadOnly: false } parent)
        {
            folder.Parent = parent;
            parent.Children.Add(folder);
            parent.IsExpanded = true;
        }
        else
        {
            TreeRoots.Add(folder);
        }

        Select(folder);
        folder.IsRenaming = true;
    }

    /// <summary>Copies the selected phrase (without its triggers, which would clash) and selects the copy.</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedPhrase))]
    private void Duplicate()
    {
        if (SelectedPhrase() is not { } phrase)
        {
            return;
        }

        ClearSearch();
        var now = _time.GetUtcNow();
        var copy = (Phrase)phrase.ToModel() with
        {
            Id = Guid.NewGuid(),
            Description = phrase.DisplayName + " (copy)",
            Triggers = [],
            CreatedUtc = now,
            ModifiedUtc = now,
        };
        var node = CreateNode(copy, phrase.Parent);
        var siblings = CollectionOf(phrase);
        siblings.Insert(siblings.IndexOf(phrase) + 1, node);
        Select(node);
        FocusPhraseNameRequested?.Invoke(this, (PhraseViewModel)node);
    }

    /// <summary>Deletes the selected phrase or folder after confirmation.</summary>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete()
    {
        var node = SelectedNode is ShortcutViewModel shortcut ? shortcut.Target : SelectedNode;
        if (node is null || node.IsReadOnly)
        {
            return;
        }

        var confirmed = node switch
        {
            FolderViewModel folder => _dialogs.Confirm(
                "Delete this folder?",
                folder.PhraseCount switch
                {
                    0 => $"Velotext deletes the empty folder \"{folder.DisplayName}\". You cannot undo this.",
                    1 => $"Velotext deletes \"{folder.DisplayName}\" and the 1 phrase in it. You cannot undo this.",
                    var n => string.Create(CultureInfo.InvariantCulture, $"Velotext deletes \"{folder.DisplayName}\" and the {n} phrases in it. You cannot undo this."),
                },
                "Delete folder",
                destructive: true),
            _ => _dialogs.Confirm("Delete this phrase?", $"Velotext deletes \"{node.DisplayName}\" and its triggers. You cannot undo this.", "Delete phrase", destructive: true),
        };

        if (!confirmed)
        {
            return;
        }

        var siblings = CollectionOf(node);
        var index = siblings.IndexOf(node);
        siblings.Remove(node);
        Unregister(node);
        RefreshUsage();

        TreeNodeViewModel? next = index < siblings.Count ? siblings[index] : index - 1 >= MinimumIndex(siblings) ? siblings[index - 1] : node.Parent;
        if (next is not null && next is not ShortcutFolderViewModel)
        {
            Select(next);
            FocusNodeRequested?.Invoke(this, next);
        }
        else
        {
            SelectedNode = null;
        }
    }

    /// <summary>Asks for a folder and moves the selected phrase or folder into it.</summary>
    [RelayCommand(CanExecute = nameof(CanRearrange))]
    private void MoveToFolder()
    {
        if (SelectedNode is not { IsReadOnly: false } node)
        {
            return;
        }

        var destinations = MoveDestinations(node);
        var choice = _dialogs.Choose(
            node is FolderViewModel ? "Move folder to" : "Move phrase to",
            $"Choose where to move \"{node.DisplayName}\".",
            [.. destinations.Select(d => d.Path)],
            "Move");
        if (choice is { } index && index >= 0 && index < destinations.Count)
        {
            ClearSearch();
            MoveInto(node, destinations[index].Folder);
        }
    }

    /// <summary>Moves the selected node up within its folder.</summary>
    [RelayCommand(CanExecute = nameof(CanRearrange))]
    private void MoveUp() => Shift(-1);

    /// <summary>Moves the selected node down within its folder.</summary>
    [RelayCommand(CanExecute = nameof(CanRearrange))]
    private void MoveDown() => Shift(+1);

    /// <summary>Starts renaming: inline for folders, the name field for phrases.</summary>
    [RelayCommand(CanExecute = nameof(CanRename))]
    private void Rename()
    {
        switch (SelectedNode)
        {
            case FolderViewModel { IsReadOnly: false } folder:
                folder.IsRenaming = true;
                break;
            case PhraseViewModel phrase:
                FocusPhraseNameRequested?.Invoke(this, phrase);
                break;
            case ShortcutViewModel shortcut:
                FocusPhraseNameRequested?.Invoke(this, shortcut.Target);
                break;
        }
    }

    /// <summary>Imports a Velotext phrase file into a new top-level folder.</summary>
    [RelayCommand]
    private void Import()
    {
        var path = _dialogs.PickFileToOpen($"Velotext phrases (*{PhraseExchange.FileExtension})|*{PhraseExchange.FileExtension}");
        if (path is null)
        {
            return;
        }

        IReadOnlyList<LibraryNode> nodes;
        try
        {
            using var stream = File.OpenRead(path);
            nodes = PhraseExchange.Import(stream, _time.GetUtcNow());
        }
        catch (InvalidPhraseFileException ex)
        {
            _dialogs.ShowError("The phrases were not imported", ex.Message);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError("The phrases were not imported", "Velotext cannot read the file. Check that it is not open in another app.");
            return;
        }

        if (nodes.Count == 0)
        {
            _dialogs.ShowError("The phrases were not imported", "The file holds no phrases. Choose another file.");
            return;
        }

        // Imported triggers and hotkeys work at once, so show everything the file adds before it is added.
        var preview = ImportPreview.Describe(nodes);
        var message = preview.MisleadingLinks.Count > 0
            ? "Some links in this file open a different site than their text shows. Import only files from people you trust."
            : "Triggers and hotkeys work as soon as you import. Import only files from people you trust.";
        if (!_dialogs.Confirm("Import these phrases?", message, "Import phrases", destructive: false, DescribeImport(preview)))
        {
            return;
        }

        ClearSearch();
        var model = nodes is [Folder single]
            ? single
            : new Folder { Id = Guid.NewGuid(), Name = "Imported " + Path.GetFileNameWithoutExtension(path), Children = nodes };
        var node = CreateNode(model, null);
        TreeRoots.Add(node);
        node.IsExpanded = true;
        Select(node);
        FocusNodeRequested?.Invoke(this, node);
    }

    /// <summary>Text for the import preview: counts, triggers, hotkeys, link sites and misleading links.</summary>
    /// <param name="preview">What the file adds.</param>
    internal static string DescribeImport(ImportPreview preview)
    {
        static string Count(int n, string one, string many) =>
            n == 1 ? "1 " + one : string.Create(CultureInfo.InvariantCulture, $"{n} {many}");

        static string List(IEnumerable<string> items)
        {
            const int Shown = 40;
            var all = items.ToList();
            var text = string.Join(", ", all.Take(Shown));
            return all.Count > Shown ? text + string.Create(CultureInfo.InvariantCulture, $" and {all.Count - Shown} more") : text;
        }

        var lines = new List<string>
        {
            Count(preview.PhraseCount, "phrase", "phrases") + (preview.FolderCount > 0 ? " in " + Count(preview.FolderCount, "folder", "folders") : string.Empty) + ".",
        };
        if (preview.TypedTriggers.Count > 0)
        {
            lines.Add("Typed text triggers: " + List(preview.TypedTriggers));
        }

        if (preview.Hotkeys.Count > 0)
        {
            lines.Add("Hotkeys: " + List(preview.Hotkeys.Select(h => h.ToString())));
        }

        if (preview.LinkHosts.Count > 0)
        {
            lines.Add("Links open: " + List(preview.LinkHosts));
        }

        lines.AddRange(preview.MisleadingLinks.Select(link => $"Check this link: the text \"{link.Text}\" opens {link.Host}."));
        return string.Join("\n\n", lines);
    }

    /// <summary>Exports the selected folder or phrase, or the whole library when nothing is selected.</summary>
    [RelayCommand]
    private void Export()
    {
        var node = SelectedNode is ShortcutViewModel shortcut ? shortcut.Target : SelectedNode;
        var items = node is null or ShortcutFolderViewModel ? BuildLibrary().Items : [node.ToModel()];
        var name = node is null or ShortcutFolderViewModel ? "Velotext phrases" : node.DisplayName;
        var path = _dialogs.PickFileToSave($"Velotext phrases (*{PhraseExchange.FileExtension})|*{PhraseExchange.FileExtension}", SafeFileName(name) + PhraseExchange.FileExtension);
        if (path is null)
        {
            return;
        }

        try
        {
            using var stream = File.Create(path);
            PhraseExchange.Export(stream, items, _time.GetUtcNow());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError("The phrases were not exported", "Velotext cannot write to that location. Choose another folder.");
        }
    }

    /// <summary>Pauses or resumes expansion.</summary>
    [RelayCommand]
    private void TogglePause() => _pause.SetPaused(!_pause.IsPaused);

    /// <summary>Opens the settings window.</summary>
    [RelayCommand]
    private void OpenSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Selects the next phrase that has a trigger problem.</summary>
    [RelayCommand]
    private void ShowNextProblem()
    {
        var ordered = AllPhrases().ToList();
        var start = EditorPhrase is null ? -1 : ordered.IndexOf(EditorPhrase);
        var next = ordered.Skip(start + 1).Concat(ordered.Take(start + 1)).FirstOrDefault(p => p.HasProblems);
        if (next is not null)
        {
            SearchText = string.Empty;
            Select(next);
        }
    }

    /// <summary>Clears the search.</summary>
    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    private bool HasSelectedPhrase() => SelectedPhrase() is not null;

    private bool CanDelete() => SelectedNode is ShortcutViewModel or { IsReadOnly: false };

    private bool CanRearrange() => SelectedNode is { IsReadOnly: false };

    private bool CanRename() => SelectedNode is ShortcutViewModel or { IsReadOnly: false };

    /// <summary>Hides the banner.</summary>
    [RelayCommand]
    private void DismissBanner() => Banner = null;

    partial void OnSelectedNodeChanged(TreeNodeViewModel? value) =>
        EditorPhrase = value switch
        {
            PhraseViewModel phrase => phrase,
            ShortcutViewModel shortcut => shortcut.Target,
            _ => null,
        };

    partial void OnEditorPhraseChanged(PhraseViewModel? value) => UpdateEditorUsage();

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string([.. name.Select(c => invalid.Contains(c) ? '-' : c)]).Trim();
        return clean.Length == 0 ? "Velotext phrases" : clean;
    }

    private static int MinimumIndex(ObservableCollection<TreeNodeViewModel> siblings) =>
        siblings.TakeWhile(n => n is ShortcutFolderViewModel).Count();

    private void OnTreeRootsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        foreach (var node in e.NewItems?.OfType<TreeNodeViewModel>() ?? [])
        {
            node.Parent = null;
        }

        MarkDirty();
    }

    private void MarkDirty()
    {
        if (_building)
        {
            return;
        }

        _dirty = true;
        _commit.Request();
    }

    private void BuildTree(PhraseLibrary library)
    {
        _building = true;
        try
        {
            var shortcutFolders = MinimumIndex(TreeRoots);
            while (TreeRoots.Count > shortcutFolders)
            {
                TreeRoots.RemoveAt(TreeRoots.Count - 1);
            }

            _phrases.Clear();
            foreach (var item in library.Items)
            {
                TreeRoots.Add(CreateNode(item, null));
            }

            foreach (var folder in TreeRoots.OfType<FolderViewModel>())
            {
                folder.IsExpanded = true;
            }

            PhraseCount = _phrases.Count;
            SelectedNode = null;
        }
        finally
        {
            _building = false;
        }
    }

    private TreeNodeViewModel CreateNode(LibraryNode model, FolderViewModel? parent)
    {
        TreeNodeViewModel node = model switch
        {
            Folder folder => new FolderViewModel(folder, _context, CreateNode),
            Phrase phrase => Register(new PhraseViewModel(phrase, _context)),
            _ => throw new NotSupportedException(model.GetType().Name),
        };
        node.Parent = parent;
        return node;
    }

    private PhraseViewModel Register(PhraseViewModel phrase)
    {
        _phrases[phrase.Id] = phrase;
        return phrase;
    }

    private void Unregister(TreeNodeViewModel node)
    {
        switch (node)
        {
            case PhraseViewModel phrase:
                _phrases.Remove(phrase.Id);
                break;
            case FolderViewModel folder:
                foreach (var child in folder.Children)
                {
                    Unregister(child);
                }

                break;
        }
    }

    private TreeNodeViewModel Insert(LibraryNode model)
    {
        FolderViewModel? parent;
        ObservableCollection<TreeNodeViewModel> siblings;
        int index;
        switch (SelectedNode)
        {
            case FolderViewModel { IsReadOnly: false } folder:
                parent = folder;
                siblings = folder.Children;
                index = siblings.Count;
                folder.IsExpanded = true;
                break;
            case PhraseViewModel phrase:
                parent = phrase.Parent;
                siblings = CollectionOf(phrase);
                index = siblings.IndexOf(phrase) + 1;
                break;
            default:
                parent = TreeRoots.OfType<FolderViewModel>().FirstOrDefault();
                siblings = parent?.Children ?? TreeRoots;
                index = siblings.Count;
                if (parent is not null)
                {
                    parent.IsExpanded = true;
                }

                break;
        }

        var node = CreateNode(model, parent);
        siblings.Insert(index, node);
        Select(node);
        return node;
    }

    private ObservableCollection<TreeNodeViewModel> CollectionOf(TreeNodeViewModel node) => node.Parent?.Children ?? TreeRoots;

    private PhraseViewModel? SelectedPhrase() => SelectedNode switch
    {
        PhraseViewModel phrase => phrase,
        ShortcutViewModel shortcut => shortcut.Target,
        _ => null,
    };

    private void Shift(int direction)
    {
        var node = SelectedNode;
        if (node is null || node.IsReadOnly)
        {
            return;
        }

        var siblings = CollectionOf(node);
        var index = siblings.IndexOf(node);
        var target = index + direction;
        if (target < MinimumIndex(siblings) || target >= siblings.Count)
        {
            return;
        }

        siblings.Move(index, target);
        Select(node);
    }

    private void Select(TreeNodeViewModel node)
    {
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
        {
            parent.IsExpanded = true;
            parent.IsVisible = true;
        }

        if (SelectedNode is not null && !ReferenceEquals(SelectedNode, node))
        {
            SelectedNode.IsSelected = false;
        }

        node.IsVisible = true;
        node.IsSelected = true;
        SelectedNode = node;
    }

    private IEnumerable<PhraseViewModel> AllPhrases()
    {
        static IEnumerable<PhraseViewModel> Walk(IEnumerable<TreeNodeViewModel> nodes)
        {
            foreach (var node in nodes)
            {
                if (node is PhraseViewModel phrase)
                {
                    yield return phrase;
                }
                else if (node is FolderViewModel folder)
                {
                    foreach (var child in Walk(folder.Children))
                    {
                        yield return child;
                    }
                }
            }
        }

        return Walk(TreeRoots);
    }

    private void ApplyFilter()
    {
        var tokens = PhraseSearch.Tokenize(SearchText);
        var searching = tokens.Length > 0;

        bool Visit(TreeNodeViewModel node)
        {
            switch (node)
            {
                case PhraseViewModel phrase:
                    phrase.IsVisible = !searching || PhraseSearch.Matches((Phrase)phrase.ToModel(), tokens);
                    return phrase.IsVisible;
                case FolderViewModel folder:
                    var any = false;
                    foreach (var child in folder.Children)
                    {
                        any |= Visit(child);
                    }

                    folder.IsVisible = !searching || any;
                    if (searching && any)
                    {
                        folder.IsExpanded = true;
                    }

                    return folder.IsVisible;
                default:
                    return true;
            }
        }

        var anyVisible = false;
        foreach (var node in TreeRoots.Where(n => n is not ShortcutFolderViewModel))
        {
            anyVisible |= Visit(node);
        }

        Recent.IsVisible = !searching;
        MostUsed.IsVisible = !searching;
        HasNoMatches = searching && !anyVisible;
    }

    private void ApplyIssues(LibrarySnapshot snapshot)
    {
        var byPhrase = snapshot.Issues.ToLookup(i => i.PhraseId);
        foreach (var phrase in _phrases.Values)
        {
            phrase.ApplyIssues([.. byPhrase[phrase.Id]]);
        }

        ProblemCount = _phrases.Values.Sum(p => p.ProblemCount);
    }

    /// <summary>Rebuilds "Recently used", "Most used" and the editor's usage line from the usage data.</summary>
    private void RefreshUsage()
    {
        Fill(Recent, _usage.Tracker.MostRecent(ShortcutCount, _phrases.ContainsKey));
        Fill(MostUsed, _usage.MostUsed(ShortcutCount, _phrases.ContainsKey));
        UpdateEditorUsage();
    }

    /// <summary>Shows phrases in a shortcut folder, or its hint when there are none. An unchanged list is kept, so its selection stays.</summary>
    /// <param name="folder">The folder to fill.</param>
    /// <param name="ids">The phrases to show, in order.</param>
    private void Fill(ShortcutFolderViewModel folder, IReadOnlyList<Guid> ids)
    {
        folder.IsVisible = PhraseSearch.Tokenize(SearchText).Length == 0;
        var targets = ids.Select(id => _phrases[id]).ToList();
        var shown = folder.Items.OfType<ShortcutViewModel>().Select(entry => entry.Target).ToList();
        if (targets.Count == 0 ? folder.Items is [ShortcutHintViewModel] : shown.SequenceEqual(targets))
        {
            return;
        }

        foreach (var entry in folder.Items.OfType<ShortcutViewModel>())
        {
            entry.Dispose();
        }

        folder.Items.Clear();
        foreach (var target in targets)
        {
            folder.Items.Add(new ShortcutViewModel(target));
        }

        if (folder.Items.Count == 0)
        {
            folder.Items.Add(new ShortcutHintViewModel(folder.Hint));
        }
    }

    /// <summary>Sets the editor's usage line for the phrase in the editor.</summary>
    private void UpdateEditorUsage() =>
        EditorUsageText = EditorPhrase is null ? string.Empty : _usage.UsesInWindow(EditorPhrase.Id) switch
        {
            0 => "Not used in the last 30 days",
            1 => "Used once in the last 30 days",
            var n => string.Create(CultureInfo.InvariantCulture, $"Used {n} times in the last 30 days"),
        };

    private void OnLibraryChanged(object? sender, LibrarySnapshot snapshot) => _ui.Post(() => ApplyIssues(snapshot));

    private void OnIssuesChanged(object? sender, LibrarySnapshot snapshot) => _ui.Post(() => ApplyIssues(snapshot));

    private void OnSaveStateChanged(object? sender, SaveState state) => _ui.Post(() =>
    {
        SaveState = state;
        if (state == SaveState.Failed)
        {
            Banner = SaveFailedBanner;
            IsBannerError = true;
        }
        else if (state == SaveState.Saved && Banner == SaveFailedBanner)
        {
            Banner = null;
        }
    });

    private void OnUsageChanged(object? sender, EventArgs e) => _ui.Post(RefreshUsage);

    private void OnPausedChanged(object? sender, bool paused) => _ui.Post(() => IsPaused = _pause.IsPaused);

    private void OnSettingsChanged(object? sender, Core.Settings.AppSettings settings) => _ui.Post(() =>
    {
        EditorFontName = settings.RichTextFontName;
        EditorFontSize = settings.RichTextFontSize;
    });
}
