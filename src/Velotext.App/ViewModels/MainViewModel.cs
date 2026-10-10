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
    private const int RecentCount = 10;

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
    private bool _dirty;
    private bool _building;

    /// <summary>The selected tree node.</summary>
    [ObservableProperty]
    private TreeNodeViewModel? _selectedNode;

    /// <summary>The phrase shown in the editor, or <c>null</c>.</summary>
    [ObservableProperty]
    private PhraseViewModel? _editorPhrase;

    /// <summary>The search box text.</summary>
    [ObservableProperty]
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
    [NotifyPropertyChangedFor(nameof(SaveStatus))]
    private SaveState _saveState;

    /// <summary>Number of phrases in the library.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhraseCountText), nameof(IsLibraryEmpty), nameof(EmptyEditorTitle), nameof(EmptyEditorMessage))]
    private int _phraseCount;

    /// <summary>Number of trigger problems.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProblemCountText))]
    private int _problemCount;

    /// <summary>A message shown at the top of the window, or <c>null</c>.</summary>
    [ObservableProperty]
    private string? _banner;

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

        TreeRoots = [Recent];
        TreeRoots.CollectionChanged += OnTreeRootsChanged;
        BuildTree(_library.Snapshot.Library);
        ApplyIssues(_library.Snapshot);
        RefreshRecent();
        _isPaused = _pause.IsPaused;
        _saveState = _library.SaveState;
        _editorFontName = _settings.Current.RichTextFontName;
        _editorFontSize = _settings.Current.RichTextFontSize;
        Banner = _library.LoadStatus switch
        {
            LoadStatus.Recovered => "The phrase file was damaged. Velotext loaded your most recent backup.",
            LoadStatus.Failed => "The phrase file was damaged and no backup was found. Velotext kept the damaged file and started a new library.",
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

    /// <summary>Raised when the settings window should open.</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>Top-level tree items: "Recently used" first, then the library.</summary>
    public ObservableCollection<TreeNodeViewModel> TreeRoots { get; }

    /// <summary>The "Recently used" folder.</summary>
    public RecentFolderViewModel Recent { get; } = new();

    /// <summary>True when the library holds no phrases.</summary>
    public bool IsLibraryEmpty => PhraseCount == 0;

    /// <summary>Title of the editor area when no phrase is selected.</summary>
    public string EmptyEditorTitle => IsLibraryEmpty ? "You have no phrases yet" : "No phrase selected";

    /// <summary>Message of the editor area when no phrase is selected.</summary>
    public string EmptyEditorMessage => IsLibraryEmpty
        ? "Create a phrase. Then give it a trigger, for example the text DEUX, and type that trigger in any app."
        : "Select a phrase in the list to edit it, or create a new one.";

    /// <summary>Status bar text for the expansion state.</summary>
    public string ExpansionStatus => IsPaused ? "Expansion paused" : "Expansion on";

    /// <summary>Label of the pause button.</summary>
    public string PauseButtonText => IsPaused ? "Resume" : "Pause";

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
        SaveState.Failed => "Not saved. Velotext tries again after your next change.",
        _ => "All changes saved",
    };

    /// <summary>All phrases currently in the tree, by id.</summary>
    internal IReadOnlyDictionary<Guid, PhraseViewModel> Phrases => _phrases;

    /// <summary>Converts the tree to an immutable library.</summary>
    public PhraseLibrary BuildLibrary() => new()
    {
        Items = [.. TreeRoots.Where(n => n is not RecentFolderViewModel).Select(n => n.ToModel())],
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
        RefreshRecent();
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
        return true;
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
    [RelayCommand]
    private void Duplicate()
    {
        if (SelectedPhrase() is not { } phrase)
        {
            return;
        }

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
    [RelayCommand]
    private void Delete()
    {
        var node = SelectedNode is RecentEntryViewModel recent ? recent.Target : SelectedNode;
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
                    0 => $"Velotext deletes the empty folder \"{folder.DisplayName}\".",
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
        RefreshRecent();

        TreeNodeViewModel? next = index < siblings.Count ? siblings[index] : index - 1 >= MinimumIndex(siblings) ? siblings[index - 1] : node.Parent;
        if (next is not null && next is not RecentFolderViewModel)
        {
            Select(next);
        }
        else
        {
            SelectedNode = null;
        }
    }

    /// <summary>Moves the selected node up within its folder.</summary>
    [RelayCommand]
    private void MoveUp() => Shift(-1);

    /// <summary>Moves the selected node down within its folder.</summary>
    [RelayCommand]
    private void MoveDown() => Shift(+1);

    /// <summary>Starts renaming: inline for folders, the name field for phrases.</summary>
    [RelayCommand]
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
            case RecentEntryViewModel recent:
                FocusPhraseNameRequested?.Invoke(this, recent.Target);
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
            _dialogs.ShowError("The phrases were not imported", "The file holds no phrases.");
            return;
        }

        var model = nodes is [Folder single]
            ? single
            : new Folder { Id = Guid.NewGuid(), Name = "Imported " + Path.GetFileNameWithoutExtension(path), Children = nodes };
        var node = CreateNode(model, null);
        TreeRoots.Add(node);
        node.IsExpanded = true;
        Select(node);
    }

    /// <summary>Exports the selected folder or phrase, or the whole library when nothing is selected.</summary>
    [RelayCommand]
    private void Export()
    {
        var node = SelectedNode is RecentEntryViewModel recent ? recent.Target : SelectedNode;
        var items = node is null or RecentFolderViewModel ? BuildLibrary().Items : [node.ToModel()];
        var name = node is null or RecentFolderViewModel ? "Velotext phrases" : node.DisplayName;
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

    /// <summary>Hides the banner.</summary>
    [RelayCommand]
    private void DismissBanner() => Banner = null;

    partial void OnSelectedNodeChanged(TreeNodeViewModel? value) =>
        EditorPhrase = value switch
        {
            PhraseViewModel phrase => phrase,
            RecentEntryViewModel recent => recent.Target,
            _ => null,
        };

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string([.. name.Select(c => invalid.Contains(c) ? '-' : c)]).Trim();
        return clean.Length == 0 ? "Velotext phrases" : clean;
    }

    private static int MinimumIndex(ObservableCollection<TreeNodeViewModel> siblings) =>
        siblings.Count > 0 && siblings[0] is RecentFolderViewModel ? 1 : 0;

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
            while (TreeRoots.Count > 1)
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
        RecentEntryViewModel recent => recent.Target,
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
        foreach (var node in TreeRoots.Where(n => n is not RecentFolderViewModel))
        {
            anyVisible |= Visit(node);
        }

        Recent.IsVisible = !searching && Recent.Items.Count > 0;
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

    private void RefreshRecent()
    {
        foreach (var entry in Recent.Items)
        {
            entry.Dispose();
        }

        Recent.Items.Clear();
        foreach (var id in _usage.Tracker.MostRecent(RecentCount, _phrases.ContainsKey))
        {
            Recent.Items.Add(new RecentEntryViewModel(_phrases[id]));
        }

        Recent.IsVisible = Recent.Items.Count > 0 && PhraseSearch.Tokenize(SearchText).Length == 0;
    }

    private void OnLibraryChanged(object? sender, LibrarySnapshot snapshot) => _ui.Post(() => ApplyIssues(snapshot));

    private void OnIssuesChanged(object? sender, LibrarySnapshot snapshot) => _ui.Post(() => ApplyIssues(snapshot));

    private void OnSaveStateChanged(object? sender, SaveState state) => _ui.Post(() => SaveState = state);

    private void OnUsageChanged(object? sender, EventArgs e) => _ui.Post(RefreshRecent);

    private void OnPausedChanged(object? sender, bool paused) => _ui.Post(() => IsPaused = paused);

    private void OnSettingsChanged(object? sender, Core.Settings.AppSettings settings) => _ui.Post(() =>
    {
        EditorFontName = settings.RichTextFontName;
        EditorFontSize = settings.RichTextFontSize;
    });
}
