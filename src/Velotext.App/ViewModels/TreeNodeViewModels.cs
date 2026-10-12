// Velotext: view models for the phrase tree (folders, phrases, the "Recently used" and "Most used" folders).
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Velotext.Core.Model;
using Velotext.Core.RichText;
using Velotext.Core.Settings;
using Velotext.Core.Validation;

namespace Velotext.App.ViewModels;

/// <summary>Shared context of the editable tree: reports edits and provides defaults and the clock.</summary>
/// <param name="markDirty">Called after any edit, so the library is committed and saved.</param>
/// <param name="defaults">Returns the options for new triggers.</param>
/// <param name="time">Clock for modification timestamps.</param>
public sealed class EditContext(Action markDirty, Func<TriggerDefaults> defaults, TimeProvider time)
{
    /// <summary>Reports an edit.</summary>
    public void MarkDirty() => markDirty();

    /// <summary>Options for new triggers.</summary>
    public TriggerDefaults Defaults => defaults();

    /// <summary>The current time.</summary>
    public DateTimeOffset Now => time.GetUtcNow();
}

/// <summary>A node in the phrase tree.</summary>
public abstract partial class TreeNodeViewModel : ObservableObject
{
    /// <summary>True when the node is selected in the tree.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>True when the node's children are shown.</summary>
    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>False when a search hides the node.</summary>
    [ObservableProperty]
    private bool _isVisible = true;

    /// <summary>The folder that holds this node, or <c>null</c> at the top level.</summary>
    public FolderViewModel? Parent { get; internal set; }

    /// <summary>Stable id of the underlying folder or phrase.</summary>
    public abstract Guid Id { get; }

    /// <summary>Text shown in the tree.</summary>
    public abstract string DisplayName { get; }

    /// <summary>Name read by screen readers; it can add state that the tree shows as an icon.</summary>
    public virtual string AccessibleName => DisplayName;

    /// <summary>True for nodes the user cannot move, rename or delete.</summary>
    public virtual bool IsReadOnly => false;

    /// <summary>Converts the node back to the library model.</summary>
    public abstract LibraryNode ToModel();
}

/// <summary>An editable folder.</summary>
public sealed partial class FolderViewModel : TreeNodeViewModel
{
    private readonly EditContext _context;

    /// <summary>The folder name.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName), nameof(AccessibleName))]
    private string _name;

    /// <summary>True while the name is being edited in the tree.</summary>
    [ObservableProperty]
    private bool _isRenaming;

    private string? _nameBeforeRename;

    /// <summary>Creates a folder view model and its children.</summary>
    /// <param name="model">The folder.</param>
    /// <param name="context">Edit context.</param>
    /// <param name="create">Creates child view models.</param>
    public FolderViewModel(Folder model, EditContext context, Func<LibraryNode, FolderViewModel, TreeNodeViewModel> create)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(create);
        _context = context ?? throw new ArgumentNullException(nameof(context));
        Id = model.Id;
        _name = model.Name;
        Children = [.. model.Children.Select(child => create(child, this))];
        Children.CollectionChanged += OnChildrenChanged;
    }

    /// <inheritdoc />
    public override Guid Id { get; }

    /// <inheritdoc />
    public override string DisplayName => string.IsNullOrWhiteSpace(Name) ? "Untitled folder" : Name;

    /// <summary>Ends renaming. Cancel puts the old name back; a blank name becomes "Untitled folder".</summary>
    /// <param name="cancel">True when the user pressed Esc.</param>
    public void EndRename(bool cancel)
    {
        if (cancel && _nameBeforeRename is not null)
        {
            Name = _nameBeforeRename;
        }
        else if (string.IsNullOrWhiteSpace(Name))
        {
            Name = "Untitled folder";
        }

        IsRenaming = false;
    }

    /// <summary>Child folders and phrases.</summary>
    public ObservableCollection<TreeNodeViewModel> Children { get; }

    /// <summary>Number of phrases in this folder and its subfolders.</summary>
    public int PhraseCount => Children.Sum(c => c switch
    {
        PhraseViewModel => 1,
        FolderViewModel folder => folder.PhraseCount,
        _ => 0,
    });

    /// <inheritdoc />
    public override LibraryNode ToModel() => new Folder
    {
        Id = Id,
        Name = string.IsNullOrWhiteSpace(Name) ? "Untitled folder" : Name.Trim(),
        Children = [.. Children.Select(c => c.ToModel())],
    };

    partial void OnNameChanged(string value) => _context.MarkDirty();

    partial void OnIsRenamingChanged(bool value) => _nameBeforeRename = value ? Name : null;

    private void OnChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var node in e.NewItems?.OfType<TreeNodeViewModel>() ?? [])
        {
            node.Parent = this;
        }

        _context.MarkDirty();
    }
}

/// <summary>The kind of a trigger row.</summary>
public enum TriggerKind
{
    /// <summary>Typed text (autotext).</summary>
    TypedText = 0,

    /// <summary>A global hotkey.</summary>
    Hotkey = 1,
}

/// <summary>One editable trigger row of a phrase.</summary>
public sealed partial class TriggerViewModel : ObservableObject
{
    private readonly Action _changed;

    /// <summary>Typed text or hotkey.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTypedText), nameof(IsHotkey))]
    private TriggerKind _kind;

    /// <summary>The trigger text for typed-text triggers.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(IsEmpty))]
    private string _text = string.Empty;

    /// <summary>Match case option.</summary>
    [ObservableProperty]
    private bool _caseSensitive;

    /// <summary>Expand immediately option.</summary>
    [ObservableProperty]
    private bool _immediate;

    /// <summary>Start of word option.</summary>
    [ObservableProperty]
    private bool _wordStart;

    /// <summary>The key combination for hotkey triggers.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(IsEmpty))]
    private HotkeyGesture? _gesture;

    /// <summary>The current problem with this trigger, or <c>null</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem), nameof(IsError))]
    private string? _problem;

    /// <summary>Severity of <see cref="Problem"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsError))]
    private IssueSeverity _problemSeverity;

    /// <summary>Creates a trigger row from the model.</summary>
    /// <param name="trigger">The trigger.</param>
    /// <param name="changed">Called after each edit.</param>
    public TriggerViewModel(Trigger trigger, Action changed)
    {
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
        switch (trigger)
        {
            case AutotextTrigger autotext:
                _kind = TriggerKind.TypedText;
                _text = autotext.Text;
                _caseSensitive = autotext.CaseSensitive;
                _immediate = autotext.Immediate;
                _wordStart = autotext.WordStart;
                break;
            case HotkeyTrigger hotkey:
                _kind = TriggerKind.Hotkey;
                _gesture = hotkey.Gesture.Key == 0 ? null : hotkey.Gesture;
                _caseSensitive = _immediate = _wordStart = true;
                break;
        }
    }

    /// <summary>True for typed-text triggers.</summary>
    public bool IsTypedText => Kind == TriggerKind.TypedText;

    /// <summary>True for hotkey triggers.</summary>
    public bool IsHotkey => Kind == TriggerKind.Hotkey;

    /// <summary>True when the trigger has no text or key yet.</summary>
    public bool IsEmpty => IsTypedText ? string.IsNullOrEmpty(Text) : Gesture is null;

    /// <summary>True when a problem is shown.</summary>
    public bool HasProblem => Problem is not null;

    /// <summary>True when the problem stops the trigger from working.</summary>
    public bool IsError => HasProblem && ProblemSeverity == IssueSeverity.Error;

    /// <summary>Short text for the tree, for example the trigger text or "Ctrl+Alt+1".</summary>
    public string Summary => IsTypedText ? Text : Gesture?.ToString() ?? string.Empty;

    /// <summary>Converts the row back to the model.</summary>
    public Trigger ToModel() => Kind == TriggerKind.TypedText
        ? new AutotextTrigger { Text = Text, CaseSensitive = CaseSensitive, Immediate = Immediate, WordStart = WordStart }
        : new HotkeyTrigger { Gesture = Gesture ?? new HotkeyGesture(HotkeyModifiers.None, 0) };

    /// <inheritdoc />
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Kind) or nameof(Text) or nameof(CaseSensitive) or nameof(Immediate) or nameof(WordStart) or nameof(Gesture))
        {
            _changed();
        }
    }
}

/// <summary>An editable phrase.</summary>
public sealed partial class PhraseViewModel : TreeNodeViewModel
{
    private readonly EditContext _context;
    private bool _suppressChanges;

    /// <summary>The phrase name shown in the tree.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName), nameof(AccessibleName))]
    private string _description;

    /// <summary>The formatted text.</summary>
    [ObservableProperty]
    private RichDocument _content;

    /// <summary>Where the phrase goes.</summary>
    [ObservableProperty]
    private OutputTarget _output;

    /// <summary>Number of trigger problems, for the tree marker.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems), nameof(AccessibleName))]
    private int _problemCount;

    /// <summary>True when one of the problems stops a trigger from working.</summary>
    [ObservableProperty]
    private bool _hasErrors;

    /// <summary>Creates a phrase view model.</summary>
    /// <param name="model">The phrase.</param>
    /// <param name="context">Edit context.</param>
    public PhraseViewModel(Phrase model, EditContext context)
    {
        ArgumentNullException.ThrowIfNull(model);
        _context = context ?? throw new ArgumentNullException(nameof(context));
        Id = model.Id;
        _description = model.Description;
        _content = model.Content;
        _output = model.Output;
        CreatedUtc = model.CreatedUtc;
        ModifiedUtc = model.ModifiedUtc;
        Triggers = [.. model.Triggers.Select(t => new TriggerViewModel(t, OnTriggerChanged))];
        Triggers.CollectionChanged += (_, _) => OnTriggerChanged();
    }

    /// <inheritdoc />
    public override Guid Id { get; }

    /// <inheritdoc />
    public override string DisplayName => string.IsNullOrWhiteSpace(Description) ? "Untitled phrase" : Description;

    /// <inheritdoc />
    public override string AccessibleName => HasProblems ? DisplayName + ", has a trigger problem" : DisplayName;

    /// <summary>The trigger rows.</summary>
    public ObservableCollection<TriggerViewModel> Triggers { get; }

    /// <summary>When the phrase was created.</summary>
    public DateTimeOffset CreatedUtc { get; }

    /// <summary>When the phrase was last edited.</summary>
    public DateTimeOffset ModifiedUtc { get; private set; }

    /// <summary>The first trigger, shown faintly next to the name in the tree.</summary>
    public string TriggerSummary => Triggers.FirstOrDefault(t => !t.IsEmpty)?.Summary ?? string.Empty;

    /// <summary>True when any trigger has a problem.</summary>
    public bool HasProblems => ProblemCount > 0;

    /// <summary>Adds an empty typed-text trigger with the default options.</summary>
    [RelayCommand]
    private void AddTypedTrigger()
    {
        var defaults = _context.Defaults;
        Triggers.Add(new TriggerViewModel(
            new AutotextTrigger { Text = string.Empty, CaseSensitive = defaults.CaseSensitive, Immediate = defaults.Immediate, WordStart = defaults.WordStart },
            OnTriggerChanged));
    }

    /// <summary>Adds an empty hotkey trigger.</summary>
    [RelayCommand]
    private void AddHotkeyTrigger() =>
        Triggers.Add(new TriggerViewModel(new HotkeyTrigger { Gesture = new HotkeyGesture(HotkeyModifiers.None, 0) }, OnTriggerChanged));

    /// <summary>Deletes a trigger row.</summary>
    /// <param name="trigger">The row.</param>
    [RelayCommand]
    private void RemoveTrigger(TriggerViewModel? trigger)
    {
        if (trigger is not null)
        {
            Triggers.Remove(trigger);
        }
    }

    /// <summary>Applies validation problems to the trigger rows.</summary>
    /// <param name="issues">Problems of this phrase, by trigger position.</param>
    public void ApplyIssues(IReadOnlyList<TriggerIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        for (var position = 0; position < Triggers.Count; position++)
        {
            var row = Triggers[position];
            var issue = issues.FirstOrDefault(i => i.TriggerPosition == position);

            // An empty new row is a hint, not an error: the user has not typed anything yet.
            row.ProblemSeverity = issue is not null && row.IsEmpty ? IssueSeverity.Warning : issue?.Severity ?? IssueSeverity.Warning;
            row.Problem = issue is not null && row.IsEmpty
                ? (row.IsTypedText ? "Type the text that inserts this phrase." : "Press the key combination to use.")
                : issue?.Message;
        }

        ProblemCount = issues.Count(i => i.TriggerPosition < Triggers.Count && !Triggers[i.TriggerPosition].IsEmpty);
        HasErrors = issues.Any(i => i.Severity == IssueSeverity.Error && i.TriggerPosition < Triggers.Count && !Triggers[i.TriggerPosition].IsEmpty);
    }

    /// <summary>Replaces the content without marking the phrase as edited (used by undo of the editor).</summary>
    /// <param name="content">The content.</param>
    internal void LoadContent(RichDocument content)
    {
        _suppressChanges = true;
        try
        {
            Content = content;
        }
        finally
        {
            _suppressChanges = false;
        }
    }

    /// <inheritdoc />
    public override LibraryNode ToModel() => new Phrase
    {
        Id = Id,
        Description = string.IsNullOrWhiteSpace(Description) ? "Untitled phrase" : Description.Trim(),
        Content = Content,
        Triggers = [.. Triggers.Select(t => t.ToModel())],
        Output = Output,
        CreatedUtc = CreatedUtc,
        ModifiedUtc = ModifiedUtc,
    };

    partial void OnDescriptionChanged(string value) => Touch();

    partial void OnContentChanged(RichDocument value)
    {
        if (!_suppressChanges)
        {
            Touch();
        }
    }

    partial void OnOutputChanged(OutputTarget value) => Touch();

    private void OnTriggerChanged()
    {
        OnPropertyChanged(nameof(TriggerSummary));
        Touch();
    }

    private void Touch()
    {
        ModifiedUtc = _context.Now;
        _context.MarkDirty();
    }
}

/// <summary>A read-only folder of shortcuts at the top of the tree: "Recently used" or "Most used".</summary>
public sealed class ShortcutFolderViewModel : TreeNodeViewModel
{
    /// <summary>Fixed id of "Recently used"; the folder is not part of the library.</summary>
    public static readonly Guid RecentId = new("1f0e9b0a-7d6c-4c55-9a9d-3b7a1e2c4d5f");

    /// <summary>Fixed id of "Most used"; the folder is not part of the library.</summary>
    public static readonly Guid MostUsedId = new("8c3d6e21-52a4-4f0b-b7d1-6a9e0c4f2b38");

    /// <summary>Creates the folder, expanded so its phrases are one click away.</summary>
    /// <param name="id">The fixed id.</param>
    /// <param name="name">Text shown in the tree.</param>
    /// <param name="glyph">Icon-font character shown before the name.</param>
    /// <param name="description">Tooltip that says which phrases the folder holds.</param>
    /// <param name="hint">Text of the row shown while the folder has no phrases.</param>
    public ShortcutFolderViewModel(Guid id, string name, string glyph, string description, string hint)
    {
        Id = id;
        DisplayName = name;
        Glyph = glyph;
        Description = description;
        Hint = hint;
        IsExpanded = true;
    }

    /// <summary>The "Recently used" folder: the most recently inserted phrases, newest first.</summary>
    public static ShortcutFolderViewModel CreateRecent() =>
        new(RecentId, "Recently used", ((char)0xE81C).ToString(), "The 10 phrases that you inserted last.", "Phrases that you insert appear here.");

    /// <summary>The "Most used" folder: the phrases inserted most in the last 30 days.</summary>
    public static ShortcutFolderViewModel CreateMostUsed() =>
        new(MostUsedId, "Most used", ((char)0xEAFC).ToString(), "The 10 phrases that you inserted most in the last 30 days.", "Phrases that you use most appear here.");

    /// <inheritdoc />
    public override Guid Id { get; }

    /// <inheritdoc />
    public override string DisplayName { get; }

    /// <summary>Icon-font character shown before the name.</summary>
    public string Glyph { get; }

    /// <summary>Tooltip that says which phrases the folder holds.</summary>
    public string Description { get; }

    /// <summary>Text of the row shown while the folder has no phrases.</summary>
    public string Hint { get; }

    /// <inheritdoc />
    public override bool IsReadOnly => true;

    /// <summary>Shortcuts to phrases, or one hint row when there are none.</summary>
    public ObservableCollection<TreeNodeViewModel> Items { get; } = [];

    /// <inheritdoc />
    public override LibraryNode ToModel() => throw new NotSupportedException("A shortcut folder is not part of the library.");
}

/// <summary>The hint row shown in a shortcut folder while it has no phrases.</summary>
/// <param name="text">The hint text.</param>
public sealed class ShortcutHintViewModel(string text) : TreeNodeViewModel
{
    /// <inheritdoc />
    public override Guid Id => Guid.Empty;

    /// <inheritdoc />
    public override string DisplayName => text;

    /// <inheritdoc />
    public override bool IsReadOnly => true;

    /// <inheritdoc />
    public override LibraryNode ToModel() => throw new NotSupportedException("The hint is not part of the library.");
}

/// <summary>A shortcut in "Recently used" or "Most used" to a phrase in the library.</summary>
public sealed class ShortcutViewModel : TreeNodeViewModel, IDisposable
{
    private int? _uses;

    /// <summary>Creates the shortcut.</summary>
    /// <param name="target">The phrase it points to.</param>
    public ShortcutViewModel(PhraseViewModel target)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        target.PropertyChanged += OnTargetChanged;
    }

    /// <summary>Stops following the phrase (called when the folder is rebuilt).</summary>
    public void Dispose() => Target.PropertyChanged -= OnTargetChanged;

    private void OnTargetChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PhraseViewModel.DisplayName) or nameof(PhraseViewModel.TriggerSummary))
        {
            OnPropertyChanged(e.PropertyName);
            OnPropertyChanged(nameof(AccessibleName));
        }
    }

    /// <summary>The phrase this shortcut opens.</summary>
    public PhraseViewModel Target { get; }

    /// <inheritdoc />
    public override Guid Id => Target.Id;

    /// <inheritdoc />
    public override string DisplayName => Target.DisplayName;

    /// <summary>The phrase's first trigger.</summary>
    public string TriggerSummary => Target.TriggerSummary;

    /// <summary>Insertions in the last 30 days, shown in "Most used"; <c>null</c> in "Recently used".</summary>
    public int? Uses
    {
        get => _uses;
        set
        {
            if (SetProperty(ref _uses, value))
            {
                OnPropertyChanged(nameof(UsesText));
                OnPropertyChanged(nameof(UsesToolTip));
                OnPropertyChanged(nameof(AccessibleName));
            }
        }
    }

    /// <summary>The number shown at the end of the row, or empty.</summary>
    public string UsesText => Uses?.ToString("N0", CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>Tooltip of the number, for example "Used 12 times in the last 30 days".</summary>
    public string? UsesToolTip => Uses switch
    {
        null => null,
        1 => "Used once in the last 30 days",
        var n => string.Create(CultureInfo.InvariantCulture, $"Used {n:N0} times in the last 30 days"),
    };

    /// <inheritdoc />
    public override string AccessibleName => Uses is null ? DisplayName : $"{DisplayName}, {UsesToolTip}";

    /// <inheritdoc />
    public override bool IsReadOnly => true;

    /// <inheritdoc />
    public override LibraryNode ToModel() => throw new NotSupportedException("Shortcuts are not part of the library.");
}
