// Velotext: tests for the main view model (tree editing, commit and save, problems, search, recent and most used, import/export).
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
    public void Starts_with_the_starter_library_and_shortcut_folders_first()
    {
        using var vm = _env.CreateMainViewModel();

        Assert.Same(vm.Recent, vm.TreeRoots[0]);
        Assert.Same(vm.MostUsed, vm.TreeRoots[1]);
        Assert.Equal(ShortcutFolderViewModel.RecentId, vm.Recent.Id);
        Assert.Equal(ShortcutFolderViewModel.MostUsedId, vm.MostUsed.Id);
        Assert.Equal("Most used", vm.MostUsed.DisplayName);
        Assert.Equal("The 10 phrases that you inserted most in the last 30 days.", vm.MostUsed.Description);
        Assert.Equal(((char)0xEAFC).ToString(), vm.MostUsed.Glyph);
        Assert.True(vm.MostUsed.IsReadOnly);
        Assert.True(vm.MostUsed.IsExpanded);
        Assert.Equal("Phrases that you use most appear here.", Assert.IsType<ShortcutHintViewModel>(Assert.Single(vm.MostUsed.Items)).DisplayName);
        Assert.Equal("My phrases", FirstFolder(vm).Name);
        Assert.Equal(1, vm.PhraseCount);
        Assert.Equal("1 phrase", vm.PhraseCountText);
        Assert.Null(vm.EditorPhrase);
        Assert.Equal("No phrase selected", vm.EmptyEditorTitle);
        Assert.True(vm.Recent.IsVisible);
        Assert.True(vm.Recent.IsExpanded);
        var hint = Assert.IsType<ShortcutHintViewModel>(Assert.Single(vm.Recent.Items));
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
    public void Move_up_down_and_drag_drop_keep_shortcut_folders_first_and_refuse_cycles()
    {
        using var vm = _env.CreateMainViewModel();
        var first = FirstFolder(vm);
        vm.SelectedNode = vm.TreeRoots[0];
        vm.NewFolderCommand.Execute(null);
        var second = (FolderViewModel)vm.SelectedNode!;

        vm.MoveUpCommand.Execute(null);
        Assert.Same(second, vm.TreeRoots[2]);
        vm.MoveUpCommand.Execute(null);
        Assert.Same(second, vm.TreeRoots[2]);
        vm.MoveDownCommand.Execute(null);
        Assert.Same(second, vm.TreeRoots[3]);

        Assert.True(vm.Move(second, first, DropPosition.Inside));
        Assert.Same(first, second.Parent);
        Assert.False(MainViewModel.CanMove(first, second, DropPosition.Inside));
        Assert.False(MainViewModel.CanMove(first, vm.TreeRoots[0], DropPosition.Before));
        Assert.False(MainViewModel.CanMove(first, vm.MostUsed, DropPosition.After));
        Assert.False(MainViewModel.CanMove(first, first, DropPosition.After));

        var phrase = first.Children.OfType<PhraseViewModel>().First();
        Assert.False(MainViewModel.CanMove(second, phrase, DropPosition.Inside));
        Assert.True(vm.Move(phrase, first, DropPosition.Before));
        Assert.Same(phrase, vm.TreeRoots[2]);
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

        var entry = Assert.IsType<ShortcutViewModel>(Assert.Single(vm.Recent.Items));
        Assert.True(vm.Recent.IsVisible);
        Assert.Equal(phrase.DisplayName, entry.DisplayName);
        Assert.Equal(phrase.TriggerSummary, entry.TriggerSummary);
        vm.SelectedNode = entry;
        Assert.Same(phrase, vm.EditorPhrase);
        Assert.Throws<NotSupportedException>(() => entry.ToModel());
        Assert.Throws<NotSupportedException>(() => vm.Recent.ToModel());
        Assert.Same(phrase, Assert.IsType<ShortcutViewModel>(Assert.Single(vm.MostUsed.Items)).Target);
    }

    [Fact]
    public void Most_used_folder_ranks_phrases_and_keeps_the_selection_when_the_order_holds()
    {
        using var vm = _env.CreateMainViewModel();
        var first = FirstFolder(vm).Children.OfType<PhraseViewModel>().First();
        vm.NewPhraseCommand.Execute(null);
        var second = vm.EditorPhrase!;

        _env.Usage.RecordUse(first.Id);
        _env.Usage.RecordUse(second.Id);
        _env.Usage.RecordUse(second.Id);

        Assert.Equal([second, first], vm.MostUsed.Items.OfType<ShortcutViewModel>().Select(s => s.Target));
        Assert.Equal([second, first], vm.Recent.Items.OfType<ShortcutViewModel>().Select(s => s.Target));
        var top = (ShortcutViewModel)vm.MostUsed.Items[0];
        vm.SelectedNode = top;
        Assert.True(vm.DeleteCommand.CanExecute(null));
        Assert.True(vm.RenameCommand.CanExecute(null));
        Assert.False(vm.MoveUpCommand.CanExecute(null));

        // Another use that keeps the order rebuilds nothing, so the selected shortcut stays.
        _env.Usage.RecordUse(second.Id);
        Assert.Same(top, vm.MostUsed.Items[0]);
        Assert.Same(second, vm.EditorPhrase);

        // Deleting a phrase removes it from both folders.
        vm.SelectedNode = second;
        vm.DeleteCommand.Execute(null);
        Assert.Same(first, Assert.IsType<ShortcutViewModel>(Assert.Single(vm.MostUsed.Items)).Target);
        Assert.Same(first, Assert.IsType<ShortcutViewModel>(Assert.Single(vm.Recent.Items)).Target);
    }

    [Fact]
    public void Editor_shows_how_often_the_phrase_was_used_in_30_days()
    {
        using var vm = _env.CreateMainViewModel();
        var phrase = FirstFolder(vm).Children.OfType<PhraseViewModel>().First();
        Assert.Equal(string.Empty, vm.EditorUsageText);

        vm.SelectedNode = phrase;
        Assert.Equal("Not used in the last 30 days", vm.EditorUsageText);

        _env.Usage.RecordUse(phrase.Id);
        Assert.Equal("Used once in the last 30 days", vm.EditorUsageText);

        _env.Usage.RecordUse(phrase.Id);
        Assert.Equal("Used 2 times in the last 30 days", vm.EditorUsageText);

        vm.SelectedNode = FirstFolder(vm);
        Assert.Equal(string.Empty, vm.EditorUsageText);
    }

    [Fact]
    public void Most_used_rows_show_their_count_and_update_it_in_place()
    {
        using var vm = _env.CreateMainViewModel();
        var phrase = FirstFolder(vm).Children.OfType<PhraseViewModel>().First();

        _env.Usage.RecordUse(phrase.Id, 30);
        var row = Assert.IsType<ShortcutViewModel>(Assert.Single(vm.MostUsed.Items));
        Assert.Equal(1, row.Uses);
        Assert.Equal("1", row.UsesText);
        Assert.Equal("Used once in the last 30 days", row.UsesToolTip);
        Assert.Equal(phrase.DisplayName + ", Used once in the last 30 days", row.AccessibleName);

        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        _env.Usage.RecordUse(phrase.Id, 30);
        Assert.Same(row, vm.MostUsed.Items[0]);
        Assert.Equal("Used 2 times in the last 30 days", row.UsesToolTip);
        Assert.Contains(nameof(ShortcutViewModel.UsesText), changed);

        // "Recently used" shows no count.
        var recent = Assert.IsType<ShortcutViewModel>(Assert.Single(vm.Recent.Items));
        Assert.Null(recent.Uses);
        Assert.Equal(string.Empty, recent.UsesText);
        Assert.Null(recent.UsesToolTip);
        Assert.Equal(phrase.DisplayName, recent.AccessibleName);

        phrase.Description = "Renamed";
        Assert.Equal("Renamed, Used 2 times in the last 30 days", row.AccessibleName);
    }

    [Fact]
    public void Selecting_most_used_shows_the_dashboard_and_a_row_opens_its_phrase()
    {
        using var vm = _env.CreateMainViewModel();
        var phrase = FirstFolder(vm).Children.OfType<PhraseViewModel>().First();
        Assert.True(vm.IsEmptyEditorVisible);
        Assert.False(vm.IsDashboardVisible);

        vm.SelectedNode = vm.MostUsed;
        Assert.True(vm.IsDashboardVisible);
        Assert.False(vm.IsEmptyEditorVisible);
        Assert.False(vm.Dashboard.HasUsage);
        Assert.Equal("Insertions per day: none in the last 30 days.", vm.Dashboard.ChartSummary);
        Assert.Equal(30, vm.Dashboard.Days.Count);

        _env.Usage.RecordUse(phrase.Id, 150);
        _env.Usage.RecordUse(phrase.Id, 250);

        var dashboard = vm.Dashboard;
        Assert.True(dashboard.HasUsage);
        Assert.Equal("2", dashboard.UsesText);
        Assert.Equal("insertions", dashboard.UsesLabel);
        Assert.Equal("400", dashboard.CharactersText);
        Assert.Equal("2 min", dashboard.TimeText);
        Assert.EndsWith(DateTimeOffset.UtcNow.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture), dashboard.RangeText, StringComparison.Ordinal);
        Assert.Equal(UsageDashboardViewModel.ChartHeight, dashboard.Days[^1].Height);
        Assert.Equal(0, dashboard.Days[0].Height);
        Assert.EndsWith(": 2 insertions, 400 characters saved", dashboard.Days[^1].ToolTip, StringComparison.Ordinal);
        Assert.Contains("with 2 insertions.", dashboard.ChartSummary, StringComparison.Ordinal);
        Assert.Equal("Time saved is an estimate at 200 characters a minute.", UsageDashboardViewModel.TimeNote);
        var row = Assert.Single(dashboard.Rows);
        Assert.Same(phrase, row.Phrase);
        Assert.Equal(phrase.DisplayName, row.Name);
        Assert.Equal(phrase.TriggerSummary, row.Trigger);
        Assert.Equal("2", row.Uses);
        Assert.Equal("400", row.CharactersSaved);
        Assert.Equal(phrase.DisplayName + ": 2 insertions, 400 characters saved. Open the phrase.", row.AccessibleName);

        TreeNodeViewModel? focused = null;
        vm.FocusNodeRequested += (_, node) => focused = node;
        vm.OpenPhraseCommand.Execute(row.Phrase);
        Assert.Same(phrase, vm.SelectedNode);
        Assert.Same(phrase, focused);
        Assert.False(vm.IsDashboardVisible);

        // Nothing happens for a phrase that is no longer in the library, or for none.
        vm.OpenPhraseCommand.Execute(null);
        var gone = new Phrase { Id = Guid.NewGuid(), Description = "Gone", CreatedUtc = DateTimeOffset.UtcNow, ModifiedUtc = DateTimeOffset.UtcNow };
        vm.OpenPhraseCommand.Execute(new PhraseViewModel(gone, new EditContext(() => { }, () => new Core.Settings.TriggerDefaults(), TimeProvider.System)));
        Assert.Same(phrase, vm.SelectedNode);
    }

    [Theory]
    [InlineData(0, "0 min")]
    [InlineData(20, "Under 1 min")]
    [InlineData(150, "3 min")]
    [InlineData(3600, "1 h")]
    [InlineData(4830, "1 h 21 min")]
    public void Dashboard_formats_time_saved(int seconds, string expected) =>
        Assert.Equal(expected, UsageDashboardViewModel.FormatTime(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Usage_service_estimates_characters_saved_for_older_files()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero));
        var id = Guid.NewGuid();
        File.WriteAllText(_env.Paths.UsageFile, $$"""
            { "schemaVersion": 2, "phrases": { "{{id}}": { "lastUsedUtc": "2026-10-12T08:00:00+00:00", "days": { "2026-10-12": 3 } } } }
            """);
        using var usage = new UsageService(_env.Paths, clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<UsageService>.Instance);

        usage.Load(phraseId => phraseId == id ? 50 : 0);
        usage.RecordUse(id, 20);

        var report = usage.Report(_ => true);
        Assert.Equal(4, report.TotalUses);
        Assert.Equal(170, report.TotalCharactersSaved);
        usage.Dispose();
        Assert.Contains("\"2026-10-12\": 170", File.ReadAllText(_env.Paths.UsageFile), StringComparison.Ordinal);
    }

    [Fact]
    public void Usage_service_migrates_old_files_and_deletes_expired_days()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero));
        var id = Guid.NewGuid();
        File.WriteAllText(_env.Paths.UsageFile, $$"""
            { "schemaVersion": 1, "phrases": { "{{id}}": { "lastUsedUtc": "2026-10-11T15:00:00+00:00", "useCount": 5 } } }
            """);
        var usage = new UsageService(_env.Paths, clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<UsageService>.Instance);
        var changes = 0;
        usage.Changed += (_, _) => changes++;

        usage.Load();
        Assert.Equal(5, usage.UsesInWindow(id));
        Assert.Equal([id], usage.MostUsed(10, _ => true));

        // Nothing expired yet: no change and no event.
        usage.TrimExpired();
        Assert.Equal(0, changes);

        // The old file is rewritten at once in the new format, without the all-time total.
        usage.Dispose();
        var json = File.ReadAllText(_env.Paths.UsageFile);
        Assert.Contains("\"2026-10-11\": 5", json, StringComparison.Ordinal);
        Assert.DoesNotContain("useCount", json, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromHours(1), UsageService.TrimInterval);

        // 31 days later the hourly check deletes the day and tells the window.
        using var later = new UsageService(_env.Paths, clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<UsageService>.Instance);
        later.Load();
        later.Changed += (_, _) => changes++;
        clock.Now = clock.Now.AddDays(31);
        later.TrimExpired();
        Assert.Equal(1, changes);
        Assert.Equal(0, later.UsesInWindow(id));
        later.Dispose();
        Assert.DoesNotContain("2026-10-11\":", File.ReadAllText(_env.Paths.UsageFile), StringComparison.Ordinal);
        Assert.Contains("lastUsedUtc", File.ReadAllText(_env.Paths.UsageFile), StringComparison.Ordinal);
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
        Assert.Equal(3, vm.TreeRoots.Count);
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
        Assert.False(vm.MostUsed.IsVisible);
        Assert.Equal(", show next", vm.ProblemButtonName);

        vm.ClearSearchCommand.Execute(null);
        Assert.True(vm.Recent.IsVisible);
        Assert.True(vm.MostUsed.IsVisible);
    }

    [Fact]
    public void Locked_library_file_stops_loading_instead_of_starting_empty()
    {
        _env.Library.Flush();
        Assert.True(File.Exists(_env.Paths.LibraryFile));
        using var service = new LibraryService(_env.Paths, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryService>.Instance);

        using (new FileStream(_env.Paths.LibraryFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = Assert.Throws<LibraryUnavailableException>(service.Load);
            Assert.Equal(_env.Paths.LibraryFile, error.Message);
        }

        var inner = new InvalidOperationException("inner");
        Assert.False(string.IsNullOrEmpty(new LibraryUnavailableException().Message));
        Assert.Same(inner, new LibraryUnavailableException("m", inner).InnerException);
    }

    [Fact]
    public void Locked_settings_are_never_overwritten_with_defaults()
    {
        _env.Settings.Update(s => s with { RichTextFontName = "Calibri" });
        var saved = File.ReadAllText(_env.Paths.SettingsFile);
        var settings = new SettingsService(_env.Paths, Microsoft.Extensions.Logging.Abstractions.NullLogger<SettingsService>.Instance);

        using (new FileStream(_env.Paths.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            settings.Load();
            settings.Update(s => s with { TrayHintShown = true });
        }

        // The change stays in memory while the file is locked; the user's file is untouched.
        Assert.True(settings.Current.TrayHintShown);
        Assert.Equal(saved, File.ReadAllText(_env.Paths.SettingsFile));

        // Once the file can be read, the next change builds on the user's real settings.
        settings.Update(s => s with { UndoWithBackspace = false });
        Assert.Equal("Calibri", settings.Current.RichTextFontName);
        Assert.Contains("Calibri", File.ReadAllText(_env.Paths.SettingsFile), StringComparison.Ordinal);
    }

    [Fact]
    public void Locked_usage_history_is_never_overwritten()
    {
        File.WriteAllText(_env.Paths.UsageFile, "{\"entries\":[]}");
        var before = File.ReadAllText(_env.Paths.UsageFile);
        using var usage = new UsageService(_env.Paths, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<UsageService>.Instance);

        using (new FileStream(_env.Paths.UsageFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            usage.Load();
        }

        usage.RecordUse(Guid.NewGuid());
        usage.Dispose();

        Assert.Equal(before, File.ReadAllText(_env.Paths.UsageFile));
    }
}
