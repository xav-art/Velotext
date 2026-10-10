// Velotext: tests for the main view model (tree editing, commit and save, problems, search, recent, import/export).
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using Velotext.App.Services;
using Velotext.App.ViewModels;
using Velotext.Core.Model;
using Velotext.Core.RichText;
using Velotext.Core.Storage;

namespace Velotext.App.Tests;

/// <summary>Behaviour of <see cref="MainViewModel"/> against real services in a temporary folder.</summary>
public sealed class MainViewModelTests : IDisposable
{
    private readonly TestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    private static FolderViewModel FirstFolder(MainViewModel vm) => vm.TreeRoots.OfType<FolderViewModel>().First();

    [Fact]
    public void Starts_with_the_starter_library_and_recent_folder_first()
    {
        using var vm = _env.CreateMainViewModel();

        Assert.IsType<RecentFolderViewModel>(vm.TreeRoots[0]);
        Assert.Equal("My phrases", FirstFolder(vm).Name);
        Assert.Equal(1, vm.PhraseCount);
        Assert.Equal("1 phrase", vm.PhraseCountText);
        Assert.Null(vm.EditorPhrase);
        Assert.Equal("No phrase selected", vm.EmptyEditorTitle);
        Assert.True(vm.Recent.IsVisible);
        Assert.True(vm.Recent.IsExpanded);
        var hint = Assert.IsType<RecentHintViewModel>(Assert.Single(vm.Recent.Items));
        Assert.Equal("Phrases that you insert appear here.", hint.DisplayName);
        Assert.True(hint.IsReadOnly);
        Assert.Equal(Guid.Empty, hint.Id);
        Assert.Throws<NotSupportedException>(() => hint.ToModel());
    }

    [Fact]
    public void New_phrase_is_added_with_an_empty_trigger_and_committed()
    {
        using var vm = _env.CreateMainViewModel();
        PhraseViewModel? focused = null;
        vm.FocusPhraseNameRequested += (_, p) => focused = p;

        vm.NewPhraseCommand.Execute(null);
        var phrase = vm.EditorPhrase!;
        phrase.Description = "Two options";
        phrase.Triggers[0].Text = "TWOX";
        phrase.Content = RichDocument.FromPlainText("We offer");
        vm.CommitNow();

        Assert.Same(phrase, focused);
        Assert.Equal(2, vm.PhraseCount);
        var stored = _env.Library.Snapshot.Library.FindPhrase(phrase.Id)!;
        Assert.Equal("Two options", stored.Description);
        Assert.Equal("TWOX", Assert.IsType<AutotextTrigger>(Assert.Single(stored.Triggers)).Text);
        Assert.Equal("We offer", PlainTextRenderer.Render(stored.Content));
        Assert.Equal(SaveState.Pending, vm.SaveState);

        _env.Library.Flush();
        Assert.Equal(SaveState.Saved, vm.SaveState);
        Assert.True(File.Exists(_env.Paths.LibraryFile));
    }

    [Fact]
    public void Empty_new_trigger_shows_a_hint_not_an_error_and_problems_are_counted()
    {
        using var vm = _env.CreateMainViewModel();

        vm.NewPhraseCommand.Execute(null);
        vm.CommitNow();
        var row = vm.EditorPhrase!.Triggers[0];

        Assert.True(row.HasProblem);
        Assert.False(row.IsError);
        Assert.Equal(0, vm.ProblemCount);

        row.Text = "VTDEMO";
        vm.CommitNow();

        Assert.True(row.IsError);
        Assert.Equal(1, vm.ProblemCount);
        Assert.Equal("1 trigger problem", vm.ProblemCountText);
        Assert.True(vm.EditorPhrase.HasProblems);
    }

    [Fact]
    public void Show_next_problem_selects_the_phrase_with_a_problem()
    {
        using var vm = _env.CreateMainViewModel();
        vm.NewPhraseCommand.Execute(null);
        var bad = vm.EditorPhrase!;
        bad.Triggers[0].Text = "VTDEMO";
        vm.CommitNow();
        vm.SelectedNode = FirstFolder(vm);

        vm.ShowNextProblemCommand.Execute(null);

        Assert.Same(bad, vm.EditorPhrase);
    }

    [Fact]
    public void New_folder_starts_renaming_and_new_phrase_goes_inside_selected_folder()
    {
        using var vm = _env.CreateMainViewModel();

        vm.NewFolderCommand.Execute(null);
        var folder = (FolderViewModel)vm.SelectedNode!;
        Assert.True(folder.IsRenaming);
        Assert.Same(vm.TreeRoots[^1], folder);

        vm.NewPhraseCommand.Execute(null);

        Assert.Same(folder, vm.EditorPhrase!.Parent);
        Assert.True(folder.IsExpanded);
    }

    [Fact]
    public void Duplicate_copies_content_without_triggers()
    {
        using var vm = _env.CreateMainViewModel();
        var original = FirstFolder(vm).Children.OfType<PhraseViewModel>().First();
        vm.SelectedNode = original;

        vm.DuplicateCommand.Execute(null);

        var copy = vm.EditorPhrase!;
        Assert.NotSame(original, copy);
        Assert.Equal(original.DisplayName + " (copy)", copy.Description);
        Assert.Empty(copy.Triggers);
        Assert.Equal(HtmlRenderer.RenderFragment(original.Content), HtmlRenderer.RenderFragment(copy.Content));
    }

    [Fact]
    public void Delete_asks_first_and_selects_a_neighbour()
    {
        using var vm = _env.CreateMainViewModel();
        var folder = FirstFolder(vm);
        var phrase = folder.Children.OfType<PhraseViewModel>().First();
        vm.SelectedNode = phrase;

        _env.Dialogs.ConfirmAnswer = false;
        vm.DeleteCommand.Execute(null);
        Assert.Single(folder.Children);

        _env.Dialogs.ConfirmAnswer = true;
        vm.DeleteCommand.Execute(null);
        vm.CommitNow();

        Assert.Empty(folder.Children);
        Assert.Same(folder, vm.SelectedNode);
        Assert.Equal(0, vm.PhraseCount);
        Assert.Equal("You have no phrases yet", vm.EmptyEditorTitle);
        Assert.Equal(["Delete this phrase?", "Delete this phrase?"], _env.Dialogs.Confirmations);

        vm.DeleteCommand.Execute(null);
        Assert.Equal("Delete this folder?", _env.Dialogs.Confirmations[^1]);
        Assert.DoesNotContain(folder, vm.TreeRoots);
    }

    [Fact]
    public void Move_up_down_and_drag_drop_keep_recent_folder_first_and_refuse_cycles()
    {
        using var vm = _env.CreateMainViewModel();
        var first = FirstFolder(vm);
        vm.SelectedNode = vm.TreeRoots[0];
        vm.NewFolderCommand.Execute(null);
        var second = (FolderViewModel)vm.SelectedNode!;

        vm.MoveUpCommand.Execute(null);
        Assert.Same(second, vm.TreeRoots[1]);
        vm.MoveUpCommand.Execute(null);
        Assert.Same(second, vm.TreeRoots[1]);
        vm.MoveDownCommand.Execute(null);
        Assert.Same(second, vm.TreeRoots[2]);

        Assert.True(vm.Move(second, first, DropPosition.Inside));
        Assert.Same(first, second.Parent);
        Assert.False(MainViewModel.CanMove(first, second, DropPosition.Inside));
        Assert.False(MainViewModel.CanMove(first, vm.TreeRoots[0], DropPosition.Before));
        Assert.False(MainViewModel.CanMove(first, first, DropPosition.After));

        var phrase = first.Children.OfType<PhraseViewModel>().First();
        Assert.False(MainViewModel.CanMove(second, phrase, DropPosition.Inside));
        Assert.True(vm.Move(phrase, first, DropPosition.Before));
        Assert.Same(phrase, vm.TreeRoots[1]);
        Assert.Null(phrase.Parent);
        Assert.False(vm.Move(first, second, DropPosition.Inside));
    }

    [Fact]
    public void Search_filters_phrases_and_reports_no_matches()
    {
        using var vm = _env.CreateMainViewModel();
        var folder = FirstFolder(vm);
        var phrase = folder.Children.OfType<PhraseViewModel>().First();

        vm.SearchText = "welcome";
        Assert.True(phrase.IsVisible);
        Assert.False(vm.HasNoMatches);

        vm.SearchText = "nothing like this";
        Assert.False(phrase.IsVisible);
        Assert.False(folder.IsVisible);
        Assert.True(vm.HasNoMatches);

        vm.ClearSearchCommand.Execute(null);
        Assert.True(phrase.IsVisible);
        Assert.True(folder.IsVisible);
    }

    [Fact]
    public void Recent_folder_lists_used_phrases_and_opens_the_target()
    {
        using var vm = _env.CreateMainViewModel();
        var phrase = FirstFolder(vm).Children.OfType<PhraseViewModel>().First();

        _env.Usage.RecordUse(phrase.Id);

        var entry = Assert.IsType<RecentEntryViewModel>(Assert.Single(vm.Recent.Items));
        Assert.True(vm.Recent.IsVisible);
        Assert.Equal(phrase.DisplayName, entry.DisplayName);
        Assert.Equal(phrase.TriggerSummary, entry.TriggerSummary);
        vm.SelectedNode = entry;
        Assert.Same(phrase, vm.EditorPhrase);
        Assert.Throws<NotSupportedException>(() => entry.ToModel());
        Assert.Throws<NotSupportedException>(() => vm.Recent.ToModel());
    }

    [Fact]
    public void Export_then_import_round_trips_and_bad_files_show_errors()
    {
        using var vm = _env.CreateMainViewModel();
        var file = Path.Combine(_env.Directory, "export.velotext");
        _env.Dialogs.SavePath = file;
        vm.SelectedNode = FirstFolder(vm);
        vm.ExportCommand.Execute(null);
        Assert.True(File.Exists(file));

        _env.Dialogs.OpenPath = file;
        vm.ImportCommand.Execute(null);
        var imported = (FolderViewModel)vm.SelectedNode!;
        Assert.Equal("My phrases", imported.Name);
        Assert.NotEqual(FirstFolder(vm).Id, imported.Id);
        vm.CommitNow();
        Assert.Equal(2, vm.PhraseCount);

        var bad = Path.Combine(_env.Directory, "bad.velotext");
        File.WriteAllText(bad, "not a phrase file");
        _env.Dialogs.OpenPath = bad;
        vm.ImportCommand.Execute(null);
        Assert.Equal("The phrases were not imported", Assert.Single(_env.Dialogs.Errors).Title);

        _env.Dialogs.OpenPath = null;
        vm.ImportCommand.Execute(null);
        Assert.Single(_env.Dialogs.Errors);
    }

    [Fact]
    public void Importing_loose_phrases_wraps_them_in_a_folder_named_after_the_file()
    {
        using var vm = _env.CreateMainViewModel();
        var file = Path.Combine(_env.Directory, "Team replies.velotext");
        using (var stream = File.Create(file))
        {
            PhraseExchange.Export(stream, [new Phrase { Id = Guid.NewGuid(), Description = "a" }, new Phrase { Id = Guid.NewGuid(), Description = "b" }], DateTimeOffset.UtcNow);
        }

        _env.Dialogs.OpenPath = file;
        vm.ImportCommand.Execute(null);

        var folder = (FolderViewModel)vm.SelectedNode!;
        Assert.Equal("Imported Team replies", folder.Name);
        Assert.Equal(2, folder.Children.Count);
    }

    [Fact]
    public void Pause_toggles_through_the_pause_control()
    {
        using var vm = _env.CreateMainViewModel();

        vm.TogglePauseCommand.Execute(null);

        Assert.True(vm.IsPaused);
        Assert.Equal("Expansion paused", vm.ExpansionStatus);
        Assert.Equal("Resume expansion", vm.PauseButtonText);
    }

    [Fact]
    public void Replace_library_rebuilds_the_tree()
    {
        using var vm = _env.CreateMainViewModel();
        var replacement = new PhraseLibrary { Items = [new Folder { Id = Guid.NewGuid(), Name = "Restored", Children = [new Phrase { Id = Guid.NewGuid(), Description = "x" }, new Phrase { Id = Guid.NewGuid(), Description = "y" }] }] };

        vm.ReplaceLibrary(replacement);

        Assert.Equal("Restored", FirstFolder(vm).Name);
        Assert.Equal(2, vm.PhraseCount);
        Assert.Equal(2, _env.Library.Snapshot.Phrases.Count);
    }

    [Fact]
    public void Trigger_rows_convert_to_model_and_report_state()
    {
        var changes = 0;
        var row = new TriggerViewModel(new AutotextTrigger { Text = "abc", CaseSensitive = false, Immediate = false, WordStart = false }, () => changes++);

        Assert.True(row.IsTypedText);
        Assert.Equal("abc", row.Summary);
        row.Kind = TriggerKind.Hotkey;
        Assert.True(row.IsHotkey);
        Assert.True(row.IsEmpty);
        row.Gesture = new HotkeyGesture(HotkeyModifiers.Control, 0x31);
        Assert.Equal("Ctrl+1", row.Summary);
        Assert.Equal(new HotkeyGesture(HotkeyModifiers.Control, 0x31), Assert.IsType<HotkeyTrigger>(row.ToModel()).Gesture);
        Assert.Equal(2, changes);

        var hotkeyRow = new TriggerViewModel(new HotkeyTrigger { Gesture = new HotkeyGesture(HotkeyModifiers.None, 0) }, () => { });
        Assert.Null(hotkeyRow.Gesture);
        Assert.Equal(0, ((HotkeyTrigger)hotkeyRow.ToModel()).Gesture.Key);
    }

    [Fact]
    public void Import_shows_what_the_file_adds_and_cancel_adds_nothing()
    {
        using var vm = _env.CreateMainViewModel();
        var file = Path.Combine(_env.Directory, "shared.velotext");
        var phrase = new Phrase
        {
            Id = Guid.NewGuid(),
            Description = "Returns",
            Triggers = [new AutotextTrigger { Text = ";ret" }, new HotkeyTrigger { Gesture = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x52) }],
            Content = new Core.RichText.RichDocument
            {
                Blocks = [new Core.RichText.Paragraph { Inlines = [new Core.RichText.Link { Url = "https://evil.example", Runs = [new Core.RichText.TextRun { Text = "www.example.com" }] }] }],
            },
        };
        using (var stream = File.Create(file))
        {
            PhraseExchange.Export(stream, [phrase], DateTimeOffset.UtcNow);
        }

        _env.Dialogs.OpenPath = file;
        _env.Dialogs.ConfirmAnswer = false;
        vm.ImportCommand.Execute(null);

        Assert.Equal("Import these phrases?", Assert.Single(_env.Dialogs.Confirmations));
        var details = Assert.Single(_env.Dialogs.ConfirmDetails)!;
        Assert.Contains("Typed text triggers: ;ret", details, StringComparison.Ordinal);
        Assert.Contains("Hotkeys: Ctrl+Alt+R", details, StringComparison.Ordinal);
        Assert.Contains("Links open: evil.example", details, StringComparison.Ordinal);
        Assert.Contains("Check this link: the text \"www.example.com\" opens evil.example.", details, StringComparison.Ordinal);
        Assert.Equal(2, vm.TreeRoots.Count);
    }

    [Fact]
    public void Import_description_counts_and_shortens_long_lists()
    {
        var preview = new ImportPreview(1, 0, [.. Enumerable.Range(0, 45).Select(i => "t" + i)], [], [], []);

        var text = MainViewModel.DescribeImport(preview);

        Assert.StartsWith("1 phrase.", text, StringComparison.Ordinal);
        Assert.EndsWith("t39 and 5 more", text, StringComparison.Ordinal);
        Assert.StartsWith("3 phrases in 2 folders.", MainViewModel.DescribeImport(new ImportPreview(3, 2, [], [], [], [])), StringComparison.Ordinal);
    }

    [Fact]
    public void Move_to_folder_offers_valid_destinations_and_moves_the_node()
    {
        using var vm = _env.CreateMainViewModel();
        var source = FirstFolder(vm);
        var phrase = source.Children.OfType<PhraseViewModel>().First();
        vm.SelectedNode = null;
        vm.NewFolderCommand.Execute(null);
        var target = (FolderViewModel)vm.SelectedNode!;
        target.EndRename(cancel: false);

        vm.SelectedNode = phrase;
        _env.Dialogs.ChooseAnswer = 2;
        vm.MoveToFolderCommand.Execute(null);

        Assert.Equal(["Top level of the library", "My phrases", "New folder"], Assert.Single(_env.Dialogs.Choices));
        Assert.Same(target, phrase.Parent);
        Assert.Same(phrase, vm.SelectedNode);

        // A folder is never offered as a destination inside itself.
        Assert.DoesNotContain(vm.MoveDestinations(target), d => ReferenceEquals(d.Folder, target));
        vm.MoveInto(target, null);
        Assert.Null(target.Parent);
        Assert.Contains(target, vm.TreeRoots);
    }

    [Fact]
    public void Commands_are_disabled_when_they_cannot_act()
    {
        using var vm = _env.CreateMainViewModel();
        vm.SelectedNode = null;
        Assert.False(vm.DuplicateCommand.CanExecute(null));
        Assert.False(vm.DeleteCommand.CanExecute(null));
        Assert.False(vm.MoveToFolderCommand.CanExecute(null));
        Assert.False(vm.RenameCommand.CanExecute(null));

        vm.SelectedNode = vm.Recent;
        Assert.False(vm.DeleteCommand.CanExecute(null));
        Assert.False(vm.MoveUpCommand.CanExecute(null));

        vm.SelectedNode = FirstFolder(vm).Children[0];
        Assert.True(vm.DuplicateCommand.CanExecute(null));
        Assert.True(vm.DeleteCommand.CanExecute(null));
        Assert.True(vm.MoveDownCommand.CanExecute(null));
    }

    [Fact]
    public void Escape_restores_the_folder_name_and_a_blank_name_becomes_untitled()
    {
        using var vm = _env.CreateMainViewModel();
        var folder = FirstFolder(vm);

        folder.IsRenaming = true;
        folder.Name = "Changed";
        folder.EndRename(cancel: true);
        Assert.Equal("My phrases", folder.Name);
        Assert.False(folder.IsRenaming);

        folder.IsRenaming = true;
        folder.Name = "  ";
        Assert.Equal("Untitled folder", folder.DisplayName);
        folder.EndRename(cancel: false);
        Assert.Equal("Untitled folder", folder.Name);
    }

    [Fact]
    public void Failed_save_shows_an_error_banner_that_clears_after_a_good_save()
    {
        using var vm = _env.CreateMainViewModel();
        var phrase = FirstFolder(vm).Children.OfType<PhraseViewModel>().First();
        using (new FileStream(_env.Paths.LibraryFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            phrase.Description = "Locked";
            vm.CommitNow();
            _env.Library.Flush();
        }

        Assert.True(vm.IsSaveFailed);
        Assert.True(vm.IsBannerError);
        Assert.StartsWith("Velotext cannot save your phrases.", vm.Banner, StringComparison.Ordinal);
        Assert.Equal("Not saved", vm.SaveStatus);

        phrase.Description = "Unlocked";
        vm.CommitNow();
        _env.Library.Flush();
        Assert.Null(vm.Banner);
        Assert.False(vm.IsSaveFailed);
    }

    [Fact]
    public void Status_texts_describe_search_and_problems()
    {
        using var vm = _env.CreateMainViewModel();
        vm.SearchText = " zzz ";

        Assert.Equal("No phrases match \"zzz\". Try other words, or press Esc to clear the search.", vm.NoMatchesText);
        Assert.False(vm.Recent.IsVisible);
        Assert.Equal(", show next", vm.ProblemButtonName);

        vm.ClearSearchCommand.Execute(null);
        Assert.True(vm.Recent.IsVisible);
    }
}
