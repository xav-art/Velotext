// Velotext: tests for JSON storage (round trips, atomic saves, backups, recovery), sanitising and import/export.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Text;
using Velotext.Core.Model;
using Velotext.Core.RichText;
using Velotext.Core.Settings;
using Velotext.Core.Storage;
using Velotext.Core.Usage;

namespace Velotext.Core.Tests.Storage;

/// <summary>Storage, sanitising and exchange behaviour, using a temporary folder per test.</summary>
public sealed class StorageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "velotext-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();

    public StorageTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private JsonFileStore<PhraseLibrary> LibraryStore() =>
        new(Path.Combine(_dir, "library.json"), VelotextJsonContext.Default.PhraseLibrary, Path.Combine(_dir, "backups"), _time);

    private static PhraseLibrary RichLibrary()
    {
        var phrase = Build.Phrase("Two options", Build.Auto("TWOX", caseSensitive: false, immediate: false, wordStart: false), Build.Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x70)) with
        {
            Output = OutputTarget.Clipboard,
            Content = new RichDocument
            {
                Blocks =
                [
                    new Paragraph { Inlines = [new TextRun { Text = "Bold", Style = TextStyle.Bold | TextStyle.Italic }, LineBreak.Instance, new Link { Url = "https://a.b", Runs = [new TextRun { Text = "l" }] }] },
                    new ListBlock { Kind = ListKind.Numbered, Items = [ListItem.FromText("one")] },
                ],
            },
        };
        return Build.Library(new Folder { Id = Guid.NewGuid(), Name = "Folder", Children = [phrase] }, Build.Phrase("Top"));
    }

    [Fact]
    public void Library_round_trips_through_json_with_every_feature()
    {
        var store = LibraryStore();
        var library = RichLibrary();

        store.Save(library);
        var result = store.Load();

        Assert.Equal(LoadStatus.Loaded, result.Status);
        var json = File.ReadAllText(store.Path);
        Assert.Contains("\"kind\": \"folder\"", json, StringComparison.Ordinal);
        Assert.Contains("\"type\": \"typed\"", json, StringComparison.Ordinal);
        Assert.Contains("\"output\": \"Clipboard\"", json, StringComparison.Ordinal);
        Assert.Contains("\"name\": \"Two options\"", json, StringComparison.Ordinal);
        Assert.Contains("\"caseSensitive\": false", json, StringComparison.Ordinal);

        var original = library.EnumeratePhrases().First();
        var loaded = result.Value!.EnumeratePhrases().First();
        Assert.Equal(original.Id, loaded.Id);
        Assert.Equal(OutputTarget.Clipboard, loaded.Output);
        var autotext = Assert.IsType<AutotextTrigger>(loaded.Triggers[0]);
        Assert.False(autotext.CaseSensitive);
        Assert.False(autotext.Immediate);
        Assert.False(autotext.WordStart);
        Assert.Equal(new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x70), Assert.IsType<HotkeyTrigger>(loaded.Triggers[1]).Gesture);
        Assert.Equal(HtmlRenderer.RenderFragment(original.Content), HtmlRenderer.RenderFragment(loaded.Content));
    }

    [Fact]
    public void Missing_file_loads_as_missing_and_save_creates_folders()
    {
        var store = new JsonFileStore<AppSettings>(Path.Combine(_dir, "nested", "settings.json"), VelotextJsonContext.Default.AppSettings, backupDirectory: null, _time);

        Assert.Equal(LoadStatus.Missing, store.Load().Status);
        store.Save(new AppSettings { Theme = AppTheme.Light });

        Assert.Equal(AppTheme.Light, store.Load().Value!.Theme);
        Assert.Empty(store.ListBackups());
    }

    [Fact]
    public void Second_save_keeps_previous_version_as_bak_and_makes_one_backup_per_interval()
    {
        var store = LibraryStore();
        store.Save(Build.Library(Build.Phrase("v1")));
        store.Save(Build.Library(Build.Phrase("v2")));
        store.Save(Build.Library(Build.Phrase("v3")));

        Assert.Equal("v2", store.TryRead(store.Path + ".bak")!.EnumeratePhrases().Single().Description);
        Assert.Single(store.ListBackups());

        _time.Advance(TimeSpan.FromHours(25));
        store.Save(Build.Library(Build.Phrase("v4")));
        Assert.Equal(2, store.ListBackups().Count);
        Assert.False(File.Exists(store.Path + ".tmp"));
    }

    [Fact]
    public void Old_backups_are_pruned()
    {
        var store = new JsonFileStore<PhraseLibrary>(Path.Combine(_dir, "library.json"), VelotextJsonContext.Default.PhraseLibrary, Path.Combine(_dir, "backups"), _time)
        {
            BackupsToKeep = 3,
            BackupInterval = TimeSpan.Zero,
        };

        for (var i = 0; i < 6; i++)
        {
            store.Save(Build.Library(Build.Phrase("v" + i)));
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(3, store.ListBackups().Count);
    }

    [Fact]
    public void Damaged_file_is_set_aside_and_bak_is_loaded()
    {
        var store = LibraryStore();
        store.Save(Build.Library(Build.Phrase("good")));
        store.Save(Build.Library(Build.Phrase("newer")));
        File.WriteAllText(store.Path, "{ not json");

        var result = store.Load();

        Assert.Equal(LoadStatus.Recovered, result.Status);
        Assert.Equal("good", result.Value!.EnumeratePhrases().Single().Description);
        Assert.Single(Directory.GetFiles(_dir, "library.json.damaged-*"));
    }

    [Fact]
    public void Damaged_file_without_backups_fails_but_is_kept()
    {
        var store = new JsonFileStore<PhraseLibrary>(Path.Combine(_dir, "library.json"), VelotextJsonContext.Default.PhraseLibrary, backupDirectory: null, _time);
        File.WriteAllText(store.Path, "garbage");

        var result = store.Load();

        Assert.Equal(LoadStatus.Failed, result.Status);
        Assert.Null(result.Value);
        Assert.True(File.Exists(result.Detail));
    }

    [Fact]
    public void Orphaned_bak_is_recovered_when_main_file_is_missing()
    {
        var store = LibraryStore();
        store.Save(Build.Library(Build.Phrase("one")));
        store.Save(Build.Library(Build.Phrase("two")));
        File.Delete(store.Path);

        var result = store.Load();

        Assert.Equal(LoadStatus.Recovered, result.Status);
        Assert.Equal("one", result.Value!.EnumeratePhrases().Single().Description);
    }

    [Fact]
    public void Null_where_not_allowed_is_rejected_as_damage()
    {
        var store = LibraryStore();
        File.WriteAllText(store.Path, "{\"items\":[{\"kind\":\"phrase\",\"id\":\"" + Guid.NewGuid() + "\",\"name\":null}]}");

        Assert.Equal(LoadStatus.Failed, store.Load().Status);
    }

    [Fact]
    public void Hand_edited_file_with_comments_trailing_commas_and_late_discriminator_loads()
    {
        var store = LibraryStore();
        var id = Guid.NewGuid();
        File.WriteAllText(store.Path, "{ // my library\n \"items\": [ { \"id\": \"" + id + "\", \"name\": \"x\", \"kind\": \"phrase\", }, ], }");

        var result = store.Load();

        Assert.Equal(LoadStatus.Loaded, result.Status);
        Assert.Equal(id, result.Value!.EnumeratePhrases().Single().Id);
    }

    [Fact]
    public void Rolling_backup_is_used_when_file_and_bak_are_both_damaged()
    {
        var store = LibraryStore();
        store.Save(Build.Library(Build.Phrase("first")));
        store.Save(Build.Library(Build.Phrase("second")));
        File.WriteAllText(store.Path, "bad");
        File.WriteAllText(store.Path + ".bak", "bad too");

        var result = store.Load();

        Assert.Equal(LoadStatus.Recovered, result.Status);
        Assert.Equal("first", result.Value!.EnumeratePhrases().Single().Description);
        Assert.StartsWith(store.BackupDirectory!, result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Locked_file_is_never_treated_as_damaged()
    {
        var store = LibraryStore();
        store.Save(Build.Library(Build.Phrase("real")));

        LoadResult<PhraseLibrary> result;
        using (new FileStream(store.Path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = store.Load();
        }

        // Nothing was read or moved, so nothing can be saved over the real file by mistake.
        Assert.Equal(LoadStatus.Unavailable, result.Status);
        Assert.Null(result.Value);
        Assert.Equal(store.Path, result.Detail);
        Assert.Empty(Directory.GetFiles(_dir, "*.damaged-*"));
        Assert.Equal(LoadStatus.Loaded, store.Load().Status);
    }

    [Fact]
    public async Task Briefly_locked_file_loads_after_a_short_wait()
    {
        var store = LibraryStore();
        store.Save(Build.Library(Build.Phrase("real")));
        var holder = new FileStream(store.Path, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = Task.Run(
            async () =>
            {
                await Task.Delay(250);
                await holder.DisposeAsync();
            },
            TestContext.Current.CancellationToken);

        var result = store.Load();
        await release;

        Assert.Equal(LoadStatus.Loaded, result.Status);
        Assert.Equal("real", Assert.Single(result.Value!.EnumeratePhrases()).Description);
    }

    [Fact]
    public async Task Save_retries_while_target_is_briefly_locked_then_gives_up()
    {
        var store = LibraryStore();
        store.Save(Build.Library(Build.Phrase("v1")));

        using (new FileStream(store.Path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => store.Save(Build.Library(Build.Phrase("v2"))));
        }

        var unlock = new FileStream(store.Path, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = Task.Run(
            async () =>
            {
                await Task.Delay(120, TestContext.Current.CancellationToken);
                await unlock.DisposeAsync();
            },
            TestContext.Current.CancellationToken);
        store.Save(Build.Library(Build.Phrase("v3")));
        await release;

        Assert.Equal("v3", store.Load().Value!.EnumeratePhrases().Single().Description);
    }

    [Fact]
    public void Locked_old_backup_does_not_break_saving()
    {
        var store = new JsonFileStore<PhraseLibrary>(Path.Combine(_dir, "library.json"), VelotextJsonContext.Default.PhraseLibrary, Path.Combine(_dir, "backups"), _time)
        {
            BackupsToKeep = 1,
            BackupInterval = TimeSpan.Zero,
        };
        store.Save(Build.Library(Build.Phrase("v0")));
        store.Save(Build.Library(Build.Phrase("v1")));
        _time.Advance(TimeSpan.FromSeconds(1));
        var oldest = store.ListBackups().Single();
        File.SetAttributes(oldest, FileAttributes.ReadOnly);

        try
        {
            store.Save(Build.Library(Build.Phrase("v2")));

            Assert.True(File.Exists(oldest));
        }
        finally
        {
            File.SetAttributes(oldest, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Usage_data_round_trips()
    {
        var store = new JsonFileStore<UsageData>(Path.Combine(_dir, "usage.json"), VelotextJsonContext.Default.UsageData, null, _time);
        var tracker = new UsageTracker();
        var id = Guid.NewGuid();
        tracker.RecordUse(id, Build.Now);

        store.Save(tracker.Snapshot());

        var json = File.ReadAllText(Path.Combine(_dir, "usage.json"));
        Assert.Contains("\"2026-10-10\": 1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("useCount", json, StringComparison.Ordinal);
        var loaded = store.Load().Value!;
        Assert.Equal(UsageData.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal(1, loaded.Phrases[id].Days[new DateOnly(2026, 10, 10)]);
    }

    [Fact]
    public void Schema_1_usage_file_loads_with_its_total()
    {
        var path = Path.Combine(_dir, "usage.json");
        var id = Guid.NewGuid();
        File.WriteAllText(path, $$"""
            {
              "schemaVersion": 1,
              "phrases": {
                "{{id}}": { "lastUsedUtc": "2026-10-09T08:00:00+00:00", "useCount": 4 }
              }
            }
            """);
        var store = new JsonFileStore<UsageData>(path, VelotextJsonContext.Default.UsageData, null, _time);

        var result = store.Load();

        Assert.Equal(LoadStatus.Loaded, result.Status);
        Assert.Equal(1, result.Value!.SchemaVersion);
        Assert.Equal(4, result.Value.Phrases[id].LegacyUseCount);
        Assert.Empty(result.Value.Phrases[id].Days);
        Assert.Equal(4, new UsageTracker(result.Value).UsesInWindow(id, Build.Now));
    }

    [Fact]
    public void Usage_file_without_phrases_loads_empty()
    {
        var path = Path.Combine(_dir, "usage.json");
        File.WriteAllText(path, "{ \"schemaVersion\": 2 }");

        var result = new JsonFileStore<UsageData>(path, VelotextJsonContext.Default.UsageData, null, _time).Load();

        Assert.Equal(LoadStatus.Loaded, result.Status);
        Assert.Empty(result.Value!.Phrases);
        Assert.Empty(new UsageTracker(result.Value).MostRecent(5, _ => true));
    }

    [Fact]
    public void Store_checks_arguments()
    {
        Assert.Throws<ArgumentException>(() => new JsonFileStore<AppSettings>(" ", VelotextJsonContext.Default.AppSettings, null));
        Assert.Throws<ArgumentNullException>(() => new JsonFileStore<AppSettings>("x.json", null!, null));
        Assert.Throws<ArgumentNullException>(() => LibraryStore().Save(null!));
        Assert.Null(LibraryStore().TryRead(Path.Combine(_dir, "nope.json")));
    }

    [Fact]
    public void Repair_fixes_names_ids_triggers_and_timestamps()
    {
        var dupId = Guid.NewGuid();
        var library = new PhraseLibrary
        {
            Items =
            [
                new Folder { Id = Guid.Empty, Name = "  ", Children = [null!, new Phrase { Id = dupId, Description = "a\u0007b" }] },
                new Phrase
                {
                    Id = dupId,
                    Description = new string('d', 400),
                    Output = (OutputTarget)7,
                    Triggers = [Build.Auto("x\ty"), Build.Auto(new string('z', 80)), Build.Hotkey(HotkeyModifiers.Control, 0), null!],
                    ModifiedUtc = Build.Now.AddDays(1),
                },
            ],
        };

        var repaired = LibrarySanitizer.Repair(library, Build.Now);

        var folder = Assert.IsType<Folder>(repaired.Items[0]);
        Assert.Equal("Untitled folder", folder.Name);
        Assert.NotEqual(Guid.Empty, folder.Id);
        var first = Assert.IsType<Phrase>(Assert.Single(folder.Children));
        Assert.Equal("a b", first.Description);
        Assert.Equal(dupId, first.Id);
        Assert.Equal(Build.Now, first.CreatedUtc);

        var second = Assert.IsType<Phrase>(repaired.Items[1]);
        Assert.NotEqual(dupId, second.Id);
        Assert.Equal(LibrarySanitizer.MaxNameLength, second.Description.Length);
        Assert.Equal(OutputTarget.ActiveApplication, second.Output);
        Assert.Equal(2, second.Triggers.Count);
        Assert.Equal("xy", ((AutotextTrigger)second.Triggers[0]).Text);
        Assert.Equal(AutotextTrigger.MaxLength, ((AutotextTrigger)second.Triggers[1]).Text.Length);
        Assert.Equal(Build.Now.AddDays(1), second.ModifiedUtc);
        Assert.Equal("Untitled phrase", LibrarySanitizer.Repair(Build.Library(new Phrase { Id = Guid.NewGuid(), Description = " " }), Build.Now).EnumeratePhrases().Single().Description);
    }

    [Fact]
    public void Export_then_import_gives_new_ids_and_same_content()
    {
        var library = RichLibrary();
        using var stream = new MemoryStream();
        PhraseExchange.Export(stream, library.Items, Build.Now);
        stream.Position = 0;

        var imported = PhraseExchange.Import(stream, Build.Now);

        var original = library.EnumeratePhrases().ToList();
        var copies = new PhraseLibrary { Items = imported }.EnumeratePhrases().ToList();
        Assert.Equal(original.Select(p => p.Description), copies.Select(p => p.Description));
        Assert.DoesNotContain(copies, c => original.Any(o => o.Id == c.Id));
        Assert.Equal(PlainTextRenderer.Render(original[0].Content), PlainTextRenderer.Render(copies[0].Content));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"format\":\"something-else\",\"version\":1,\"items\":[]}")]
    [InlineData("{\"format\":\"velotext-phrases\",\"version\":99,\"items\":[]}")]
    [InlineData("{\"format\":\"velotext-phrases\",\"version\":1,\"items\":[{\"id\":\"8a9e3c1e-1111-4a4a-9b9b-000000000001\",\"name\":\"no kind\"}]}")]
    [InlineData("{\"format\":\"velotext-phrases\",\"version\":1,\"items\":[{\"kind\":\"robot\"}]}")]
    [InlineData("null")]
    public void Import_rejects_foreign_or_future_files(string content)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        Assert.Throws<InvalidPhraseFileException>(() => PhraseExchange.Import(stream, Build.Now));
    }

    [Fact]
    public void Import_rejects_oversized_files()
    {
        using var stream = new MemoryStream(new byte[PhraseExchange.MaxFileBytes + 1]);

        var error = Assert.Throws<InvalidPhraseFileException>(() => PhraseExchange.Import(stream, Build.Now));
        Assert.Contains("20 MB", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_enforces_limits()
    {
        LibraryNode deep = Build.Phrase("leaf");
        for (var i = 0; i < LibrarySanitizer.MaxFolderDepth + 1; i++)
        {
            deep = new Folder { Id = Guid.NewGuid(), Name = "f", Children = [deep] };
        }

        Assert.Throws<InvalidPhraseFileException>(() => LibrarySanitizer.PrepareImport([deep], Build.Now));

        var manyTriggers = Build.Phrase("t", [.. Enumerable.Range(0, LibrarySanitizer.MaxTriggersPerPhrase + 1).Select(i => (Trigger)Build.Auto("t" + i))]);
        Assert.Throws<InvalidPhraseFileException>(() => LibrarySanitizer.PrepareImport([manyTriggers], Build.Now));

        var huge = Build.Phrase("h") with { Content = RichDocument.FromPlainText(new string('x', LibrarySanitizer.MaxContentCharacters + 1)) };
        Assert.Throws<InvalidPhraseFileException>(() => LibrarySanitizer.PrepareImport([huge], Build.Now));

        var many = Enumerable.Range(0, LibrarySanitizer.MaxNodes + 1).Select(_ => (LibraryNode)new Folder { Id = Guid.NewGuid(), Name = "f" });
        Assert.Throws<InvalidPhraseFileException>(() => LibrarySanitizer.PrepareImport(many, Build.Now));
        Assert.Empty(LibrarySanitizer.PrepareImport(null, Build.Now));
    }

    [Fact]
    public void Exception_constructors_keep_messages()
    {
        Assert.False(string.IsNullOrEmpty(new InvalidPhraseFileException().Message));
        var inner = new InvalidOperationException("x");
        Assert.Same(inner, new InvalidPhraseFileException("m", inner).InnerException);
        Assert.Throws<ArgumentNullException>(() => PhraseExchange.Export(null!, [], Build.Now));
        Assert.Throws<ArgumentNullException>(() => PhraseExchange.Export(new MemoryStream(), null!, Build.Now));
        Assert.Throws<ArgumentNullException>(() => PhraseExchange.Import(null!, Build.Now));
        Assert.Throws<ArgumentNullException>(() => LibrarySanitizer.Repair(null!, Build.Now));
    }

    [Fact]
    public void Import_preview_lists_triggers_hotkeys_link_sites_and_misleading_links()
    {
        var link = new Link { Url = "https://evil.example/login", Runs = [new TextRun { Text = "www.example.com/returns" }] };
        var honest = new Link { Url = "https://www.example.com", Runs = [new TextRun { Text = "our site" }] };
        var phrase = Build.Phrase("Returns", Build.Auto(";ret"), Build.Auto(";ret"), Build.Auto(string.Empty), Build.Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x52)) with
        {
            Content = new RichDocument
            {
                Blocks =
                [
                    new Paragraph { Inlines = [link, new Link { Url = "javascript:x", Runs = [new TextRun { Text = "x" }] }] },
                    new ListBlock { Items = [new ListItem { Blocks = [new Paragraph { Inlines = [honest] }] }] },
                ],
            },
        };
        var folder = new Folder { Id = Guid.NewGuid(), Name = "Shared", Children = [phrase, Build.Phrase("Other", Build.Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x52))] };

        var preview = ImportPreview.Describe([folder]);

        Assert.Equal(2, preview.PhraseCount);
        Assert.Equal(1, preview.FolderCount);
        Assert.Equal([";ret"], preview.TypedTriggers);
        Assert.Equal("Ctrl+Alt+R", Assert.Single(preview.Hotkeys).ToString());
        Assert.Equal(["evil.example", "example.com"], preview.LinkHosts);
        Assert.Equal(new MisleadingLink("www.example.com/returns", "evil.example"), Assert.Single(preview.MisleadingLinks));
        Assert.Throws<ArgumentNullException>(() => ImportPreview.Describe(null!));
    }

    [Fact]
    public void Recovery_uses_the_newest_readable_copy()
    {
        var store = LibraryStore();
        store.Save(Build.Library(Build.Phrase("old")));
        store.Save(Build.Library(Build.Phrase("newest")));
        File.Copy(store.Path, store.Path + ".tmp", overwrite: true);
        File.WriteAllText(store.Path, "damaged");

        // A crash during a save left a complete .tmp that is newer than the .bak and the rolling backups.
        File.SetLastWriteTimeUtc(store.Path + ".tmp", DateTime.UtcNow.AddMinutes(1));
        File.SetLastWriteTimeUtc(store.Path + ".bak", DateTime.UtcNow.AddMinutes(-10));
        foreach (var backup in store.ListBackups())
        {
            File.SetLastWriteTimeUtc(backup, DateTime.UtcNow.AddMinutes(-5));
        }

        var result = store.Load();

        Assert.Equal(LoadStatus.Recovered, result.Status);
        Assert.Equal(store.Path + ".tmp", result.Detail);
        Assert.Equal("newest", Assert.Single(result.Value!.EnumeratePhrases()).Description);
    }

    [Fact]
    public void Missing_file_is_recovered_from_a_rolling_backup()
    {
        var store = LibraryStore();
        store.Save(Build.Library(Build.Phrase("kept")));
        Assert.True(store.CreateBackup());
        File.Delete(store.Path);
        File.Delete(store.Path + ".bak");

        var result = store.Load();

        Assert.Equal(LoadStatus.Recovered, result.Status);
        Assert.StartsWith(store.BackupDirectory!, result.Detail, StringComparison.Ordinal);
        Assert.Equal("kept", Assert.Single(result.Value!.EnumeratePhrases()).Description);
    }

    [Theory]
    [InlineData("abc", 2, "ab")]
    [InlineData("ab", 5, "ab")]
    [InlineData("a\uD83D\uDE00", 2, "a")]
    [InlineData("a\uD83D\uDE00b", 3, "a\uD83D\uDE00")]
    public void Truncating_never_splits_an_emoji(string text, int length, string expected) =>
        Assert.Equal(expected, LibrarySanitizer.Truncate(text, length));
}
