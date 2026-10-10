// Velotext: behaviour of the phrase text editor (load and save rich text, clean pastes, formatting buttons).
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Velotext.App.ViewModels;
using Velotext.Core.RichText;
using Inline = System.Windows.Documents.Inline;

namespace Velotext.App.Editing;

/// <summary>
/// Edits the content of one phrase. Edits are written back to the phrase 300 ms after typing stops, and
/// at once when the phrase changes or the editor loses focus, so nothing is lost. Everything pasted in
/// (from Word, Outlook, a browser) is reduced to the formatting Velotext keeps before it lands.
/// </summary>
public sealed partial class RichTextEditor : UserControl
{
    /// <summary>The phrase being edited.</summary>
    public static readonly DependencyProperty PhraseProperty = DependencyProperty.Register(
        nameof(Phrase), typeof(PhraseViewModel), typeof(RichTextEditor), new PropertyMetadata(null, OnPhraseChanged));

    /// <summary>Font family shown in the editor (the output font).</summary>
    public static readonly DependencyProperty EditorFontFamilyProperty = DependencyProperty.Register(
        nameof(EditorFontFamily), typeof(string), typeof(RichTextEditor), new PropertyMetadata("Aptos", OnFontChanged));

    /// <summary>Font size in points shown in the editor.</summary>
    public static readonly DependencyProperty EditorFontSizeProperty = DependencyProperty.Register(
        nameof(EditorFontSize), typeof(double), typeof(RichTextEditor), new PropertyMetadata(11.0, OnFontChanged));

    private readonly DispatcherTimer _pushTimer;
    private bool _loading;
    private bool _pushing;
    private bool _dirty;

    /// <summary>Creates the editor.</summary>
    public RichTextEditor()
    {
        InitializeComponent();
        _pushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _pushTimer.Tick += (_, _) => Push();
        DataObject.AddPastingHandler(Editor, OnPasting);
        Editor.Document = FlowDocumentMapper.ToFlowDocument(RichDocument.Empty, new FontFamily(EditorFontFamily), EditorFontSize);
    }

    /// <summary>Raised when the user asks for a link; the handler returns the address, empty to remove, or <c>null</c> to cancel.</summary>
    public event Func<string?, string?>? LinkRequested;

    /// <summary>The phrase being edited.</summary>
    public PhraseViewModel? Phrase
    {
        get => (PhraseViewModel?)GetValue(PhraseProperty);
        set => SetValue(PhraseProperty, value);
    }

    /// <summary>Font family shown in the editor.</summary>
    public string EditorFontFamily
    {
        get => (string)GetValue(EditorFontFamilyProperty);
        set => SetValue(EditorFontFamilyProperty, value);
    }

    /// <summary>Font size in points shown in the editor.</summary>
    public double EditorFontSize
    {
        get => (double)GetValue(EditorFontSizeProperty);
        set => SetValue(EditorFontSizeProperty, value);
    }

    /// <summary>Moves keyboard focus into the text.</summary>
    public void FocusText() => Editor.Focus();

    /// <summary>Converts any rich clipboard content to clean rich text, or returns <c>null</c> for plain text.</summary>
    /// <param name="data">The clipboard or drag data.</param>
    internal static RichDocument? ReadRich(IDataObject data)
    {
        if (data.GetDataPresent(DataFormats.Rtf) && data.GetData(DataFormats.Rtf) is string rtf)
        {
            var flow = new FlowDocument();
            var range = new TextRange(flow.ContentStart, flow.ContentEnd);
            using var stream = new MemoryStream(Encoding.Latin1.GetBytes(rtf));
            try
            {
                range.Load(stream, DataFormats.Rtf);
                return FlowDocumentMapper.FromFlowDocument(flow);
            }
            catch (ArgumentException)
            {
                // Unreadable RTF: fall through to the other formats.
            }
        }

        if (data.GetDataPresent(DataFormats.Html) && data.GetData(DataFormats.Html) is string html)
        {
            return HtmlImporter.Parse(HtmlImporter.ExtractFragment(html));
        }

        return null;
    }

    private static void OnPhraseChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (RichTextEditor)d;
        if (e.OldValue is PhraseViewModel old)
        {
            editor.Push(old);
            old.PropertyChanged -= editor.OnPhrasePropertyChanged;
        }

        if (e.NewValue is PhraseViewModel phrase)
        {
            phrase.PropertyChanged += editor.OnPhrasePropertyChanged;
        }

        editor.Load();
    }

    private static void OnFontChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (RichTextEditor)d;
        editor.Push();
        editor.Load();
    }

    private void OnPhrasePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PhraseViewModel.Content) && !_pushing)
        {
            Load();
        }
    }

    private void Load()
    {
        _loading = true;
        try
        {
            _pushTimer.Stop();
            _dirty = false;
            var content = Phrase?.Content ?? RichDocument.Empty;
            Editor.Document = FlowDocumentMapper.ToFlowDocument(content, new FontFamily(EditorFontFamily), EditorFontSize);
            Editor.IsEnabled = Phrase is not null;
        }
        finally
        {
            _loading = false;
        }
    }

    private void Push() => Push(Phrase);

    private void Push(PhraseViewModel? target)
    {
        _pushTimer.Stop();
        if (!_dirty || target is null)
        {
            return;
        }

        _dirty = false;
        var content = FlowDocumentMapper.FromFlowDocument(Editor.Document);
        if (HtmlRenderer.RenderFragment(content) == HtmlRenderer.RenderFragment(target.Content))
        {
            return;
        }

        _pushing = true;
        try
        {
            target.Content = content;
        }
        finally
        {
            _pushing = false;
        }
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _dirty = true;
        _pushTimer.Stop();
        _pushTimer.Start();
    }

    private void OnLostFocus(object sender, KeyboardFocusChangedEventArgs e) => Push();

    private void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        var rich = ReadRich(e.DataObject);
        if (rich is null)
        {
            return;
        }

        var replacement = new DataObject();
        replacement.SetData(DataFormats.UnicodeText, PlainTextRenderer.Render(rich));
        if (rich.HasFormatting)
        {
            // Hand WPF its own format, built from the clean model, so lists and links arrive intact.
            var flow = FlowDocumentMapper.ToFlowDocument(rich, new FontFamily(EditorFontFamily), EditorFontSize);
            var range = new TextRange(flow.ContentStart, flow.ContentEnd);
            using var stream = new MemoryStream();
            range.Save(stream, DataFormats.Xaml);
            replacement.SetData(DataFormats.Xaml, Encoding.UTF8.GetString(stream.ToArray()));
            e.FormatToApply = DataFormats.Xaml;
        }
        else
        {
            e.FormatToApply = DataFormats.UnicodeText;
        }

        e.DataObject = replacement;
    }

    private void OnSelectionChanged(object sender, RoutedEventArgs e)
    {
        var selection = Editor.Selection;
        BoldButton.IsChecked = selection.GetPropertyValue(TextElement.FontWeightProperty) is FontWeight weight && weight.ToOpenTypeWeight() >= 600;
        ItalicButton.IsChecked = selection.GetPropertyValue(TextElement.FontStyleProperty) is FontStyle style && style == FontStyles.Italic;
        var decorations = selection.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
        UnderlineButton.IsChecked = decorations?.Any(d => d.Location == TextDecorationLocation.Underline) ?? false;
        StrikeButton.IsChecked = decorations?.Any(d => d.Location == TextDecorationLocation.Strikethrough) ?? false;
    }

    private void Run(RoutedUICommand command)
    {
        command.Execute(null, Editor);
        Editor.Focus();
    }

    private void OnBold(object sender, RoutedEventArgs e) => Run(EditingCommands.ToggleBold);

    private void OnItalic(object sender, RoutedEventArgs e) => Run(EditingCommands.ToggleItalic);

    private void OnUnderline(object sender, RoutedEventArgs e) => Run(EditingCommands.ToggleUnderline);

    private void OnBullets(object sender, RoutedEventArgs e) => Run(EditingCommands.ToggleBullets);

    private void OnNumbering(object sender, RoutedEventArgs e) => Run(EditingCommands.ToggleNumbering);

    private void OnStrikethrough(object sender, RoutedEventArgs e)
    {
        var selection = Editor.Selection;
        var current = selection.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
        var hasStrike = current?.Any(d => d.Location == TextDecorationLocation.Strikethrough) ?? false;
        var next = new TextDecorationCollection(current?.Where(d => d.Location != TextDecorationLocation.Strikethrough) ?? []);
        if (!hasStrike)
        {
            next.Add(TextDecorations.Strikethrough);
        }

        selection.ApplyPropertyValue(Inline.TextDecorationsProperty, next);
        Editor.Focus();
    }

    private void OnClearFormatting(object sender, RoutedEventArgs e)
    {
        Editor.Selection.ClearAllProperties();
        Editor.Focus();
    }

    private void OnLink(object sender, RoutedEventArgs e)
    {
        var selection = Editor.Selection;
        var existing = FindHyperlink(selection.Start) ?? FindHyperlink(selection.End);
        var url = LinkRequested?.Invoke(existing?.NavigateUri?.ToString());
        if (url is null)
        {
            return;
        }

        if (url.Length == 0)
        {
            if (existing is not null)
            {
                RemoveHyperlink(existing);
            }

            Editor.Focus();
            return;
        }

        var normalized = LinkPolicy.Normalize(url);
        if (normalized is null)
        {
            return;
        }

        if (existing is not null)
        {
            existing.NavigateUri = new Uri(normalized);
        }
        else if (selection.IsEmpty)
        {
            var link = new Hyperlink(new Run(url), selection.Start.GetInsertionPosition(LogicalDirection.Forward)) { NavigateUri = new Uri(normalized) };
            Editor.CaretPosition = link.ElementEnd.GetNextInsertionPosition(LogicalDirection.Forward) ?? link.ElementEnd;
        }
        else
        {
            _ = new Hyperlink(selection.Start, selection.End) { NavigateUri = new Uri(normalized) };
        }

        Editor.Focus();
    }

    private static Hyperlink? FindHyperlink(TextPointer position)
    {
        for (var element = position.Parent as DependencyObject; element is not null and not FlowDocument; element = LogicalTreeHelper.GetParent(element))
        {
            if (element is Hyperlink hyperlink)
            {
                return hyperlink;
            }
        }

        return null;
    }

    private static void RemoveHyperlink(Hyperlink hyperlink)
    {
        var inlines = hyperlink.Inlines.ToList();
        var parent = hyperlink.SiblingInlines;
        foreach (var inline in inlines)
        {
            hyperlink.Inlines.Remove(inline);
            parent.InsertBefore(hyperlink, inline);
        }

        parent.Remove(hyperlink);
    }
}
