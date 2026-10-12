// Velotext: tests for the library model, hotkey gestures, settings and search.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using Velotext.Core.Model;
using Velotext.Core.RichText;
using Velotext.Core.Search;
using Velotext.Core.Settings;

namespace Velotext.Core.Tests.Model;

/// <summary>Model helpers and small services.</summary>
public sealed class ModelTests
{
    [Fact]
    public void Starter_library_has_a_working_welcome_phrase()
    {
        var library = PhraseLibrary.CreateStarter(Build.Now);

        var folder = Assert.IsType<Folder>(Assert.Single(library.Items));
        var phrase = Assert.IsType<Phrase>(Assert.Single(folder.Children));
        Assert.Equal("VTDEMO", Assert.IsType<AutotextTrigger>(Assert.Single(phrase.Triggers)).Text);
        Assert.True(phrase.Content.HasFormatting);
        Assert.Same(phrase, library.FindPhrase(phrase.Id));
        Assert.Null(library.FindPhrase(Guid.NewGuid()));
        Assert.Single(library.EnumerateFolders());
        Assert.Empty(PhraseLibrary.Empty.Items);
    }

    [Fact]
    public void Enumeration_is_depth_first_in_display_order()
    {
        var p1 = Build.Phrase("1");
        var p2 = Build.Phrase("2");
        var p3 = Build.Phrase("3");
        var inner = new Folder { Id = Guid.NewGuid(), Name = "inner", Children = [p2] };
        var outer = new Folder { Id = Guid.NewGuid(), Name = "outer", Children = [inner, p3] };
        var library = Build.Library(p1, outer);

        Assert.Equal(["1", "2", "3"], library.EnumeratePhrases().Select(p => p.Description));
        Assert.Equal(["outer", "inner"], library.EnumerateFolders().Select(f => f.Name));
    }

    [Theory]
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x31, "Ctrl+Alt+1", null)]
    [InlineData(HotkeyModifiers.Shift | HotkeyModifiers.Windows, 0x41, "Shift+Win+A", null)]
    [InlineData(HotkeyModifiers.None, 0x74, "F5", null)]
    [InlineData(HotkeyModifiers.Shift, 0x87, "Shift+F24", null)]
    [InlineData(HotkeyModifiers.Control, 0x61, "Ctrl+Num 1", null)]
    [InlineData(HotkeyModifiers.Alt, 0xBA, "Alt+;", null)]
    [InlineData(HotkeyModifiers.Control, 0x07, "Ctrl+Key 0x07", null)]
    [InlineData(HotkeyModifiers.Shift, 0x41, "Shift+A", "Add Ctrl, Alt or Win. You can use F1 to F24 without them.")]
    [InlineData(HotkeyModifiers.Control, 0x11, "Ctrl+Key 0x11", "Add a key that is not Ctrl, Alt, Shift or Win.")]
    [InlineData(HotkeyModifiers.Control, 0, "Ctrl+Key 0x00", "Choose a key.")]
    public void Hotkey_gesture_formats_and_validates(HotkeyModifiers modifiers, int key, string display, string? error)
    {
        var gesture = new HotkeyGesture(modifiers, key);

        Assert.Equal(display, gesture.ToString());
        Assert.Equal(error, gesture.ValidationError);
    }

    [Fact]
    public void Settings_normalize_replaces_out_of_range_values()
    {
        var settings = new AppSettings
        {
            RichTextFontName = "  ",
            RichTextFontSize = double.NaN,
            PasteTimeoutMilliseconds = 5,
            Theme = (AppTheme)42,
            PauseHotkey = new HotkeyGesture(HotkeyModifiers.None, 0x41),
            NewTriggerDefaults = null!,
        }.Normalize();

        Assert.Equal("Aptos", settings.RichTextFontName);
        Assert.Equal(11, settings.RichTextFontSize);
        Assert.Equal(AppSettings.MinPasteTimeoutMilliseconds, settings.PasteTimeoutMilliseconds);
        Assert.Equal(AppTheme.Dark, settings.Theme);
        Assert.Null(settings.PauseHotkey);
        Assert.NotNull(settings.NewTriggerDefaults);

        var valid = new AppSettings { RichTextFontSize = 200, PauseHotkey = new HotkeyGesture(HotkeyModifiers.Control, 0x50) }.Normalize();
        Assert.Equal(72, valid.RichTextFontSize);
        Assert.NotNull(valid.PauseHotkey);
    }

    [Fact]
    public void Defaults_match_the_agreed_product_defaults()
    {
        var trigger = new AutotextTrigger { Text = "x" };
        var settings = new AppSettings();

        Assert.True(trigger.CaseSensitive);
        Assert.True(trigger.Immediate);
        Assert.True(trigger.WordStart);
        Assert.True(settings.StartWithWindows);
        Assert.True(settings.UndoWithBackspace);
        Assert.Equal(AppTheme.Dark, settings.Theme);
        Assert.Equal(OutputTarget.ActiveApplication, new Phrase { Id = Guid.NewGuid(), Description = "d" }.Output);
    }

    [Theory]
    [InlineData("reponse", true)]
    [InlineData("CAFE offert", true)]
    [InlineData("TWOX", true)]
    [InlineData("Ctrl+Alt+1", true)]
    [InlineData("twox absent", false)]
    [InlineData("", true)]
    public void Search_ignores_case_and_accents_and_needs_every_word(string query, bool expected)
    {
        var phrase = Build.Phrase("Réponse rapide", Build.Auto("TWOX"), Build.Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x31)) with
        {
            Content = RichDocument.FromPlainText("Un café offert avec la commande"),
        };

        Assert.Equal(expected, PhraseSearch.Matches(phrase, PhraseSearch.Tokenize(query)));
    }

    [Theory]
    [InlineData(HotkeyModifiers.Control, 0x56, true)]
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x5A, true)]
    [InlineData(HotkeyModifiers.Control, 0x0D, true)]
    [InlineData(HotkeyModifiers.Shift, 0x2D, true)]
    [InlineData(HotkeyModifiers.Alt, 0x73, true)]
    [InlineData(HotkeyModifiers.Windows, 0x4C, true)]
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x56, false)]
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x51, false)]
    [InlineData(HotkeyModifiers.None, 0x70, false)]
    public void Standard_shortcuts_are_refused(HotkeyModifiers modifiers, int key, bool standard)
    {
        var gesture = new HotkeyGesture(modifiers, key);

        Assert.Equal(standard, gesture.IsStandardShortcut);
        Assert.Equal(standard, gesture.ValidationError is not null);
    }

    [Fact]
    public void Standard_shortcut_message_names_the_shortcut() =>
        Assert.Equal("Ctrl+V is a standard shortcut in most apps. Choose a different hotkey.", new HotkeyGesture(HotkeyModifiers.Control, 0x56).ValidationError);

    [Fact]
    public void Unusable_window_placements_are_dropped()
    {
        var good = new WindowPlacement { Left = -1200, Top = 10, Width = 1000, Height = 700, Maximized = true };

        Assert.Equal(good, new AppSettings { MainWindow = good }.Normalize().MainWindow);
        Assert.Null(new AppSettings { MainWindow = good with { Width = 100 } }.Normalize().MainWindow);
        Assert.Null(new AppSettings { MainWindow = good with { Height = double.NaN } }.Normalize().MainWindow);
        Assert.Null(new AppSettings { MainWindow = good with { Left = double.PositiveInfinity } }.Normalize().MainWindow);
        Assert.Null(new AppSettings { MainWindow = good with { Width = 50_000 } }.Normalize().MainWindow);
        Assert.Null(new AppSettings().Normalize().MainWindow);
    }
}
