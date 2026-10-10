// Velotext: main window behaviour (tree selection, keyboard, drag and drop, rename, close to tray).
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Velotext.App.ViewModels;

namespace Velotext.App.Views;

/// <summary>The main window. Closing it hides it; Velotext keeps running in the tray.</summary>
public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private Point _dragStart;
    private TreeNodeViewModel? _dragCandidate;

    /// <summary>Creates the window.</summary>
    /// <param name="viewModel">The main view model.</param>
    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = viewModel;
        viewModel.FocusPhraseNameRequested += OnFocusPhraseName;
        ContentEditor.LinkRequested += current => MessageDialog.Prompt(
            this,
            "Link address",
            "Enter a web address, mailto:name@example.com or tel:+4512345678. Leave the field empty to remove the link.",
            current,
            "Save link");
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Find, (_, _) => FocusSearch()));
    }

    /// <summary>Raised when the user closes the window; the app decides whether to hide or exit.</summary>
    public event EventHandler<CancelEventArgs>? CloseRequested;

    /// <summary>Shows the window and brings it to the front.</summary>
    public void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    /// <inheritdoc />
    protected override void OnClosing(CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        CloseRequested?.Invoke(this, e);
        base.OnClosing(e);
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            FocusSearch();
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    private static T? FindAncestor<T>(DependencyObject? element)
        where T : DependencyObject
    {
        while (element is not null and not T)
        {
            element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }

        return element as T;
    }

    private void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnFocusPhraseName(object? sender, PhraseViewModel phrase)
    {
        // Wait until the editor shows the new phrase, then select its name for typing.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        });
    }

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        _viewModel.SelectedNode = e.NewValue as TreeNodeViewModel;

    private void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox)
        {
            return;
        }

        var handled = true;
        switch (e.Key)
        {
            case Key.Delete:
                _viewModel.DeleteCommand.Execute(null);
                break;
            case Key.F2:
                _viewModel.RenameCommand.Execute(null);
                break;
            case Key.System when e.SystemKey == Key.Up && Keyboard.Modifiers == ModifierKeys.Alt:
                _viewModel.MoveUpCommand.Execute(null);
                break;
            case Key.System when e.SystemKey == Key.Down && Keyboard.Modifiers == ModifierKeys.Alt:
                _viewModel.MoveDownCommand.Execute(null);
                break;
            case Key.Enter when _viewModel.EditorPhrase is not null:
                NameBox.Focus();
                break;
            default:
                handled = false;
                break;
        }

        e.Handled = handled;
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                _viewModel.ClearSearchCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Down or Key.Enter:
                Tree.Focus();
                e.Handled = true;
                break;
        }
    }

    private void OnRenameBoxVisible(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox { IsVisible: true } box)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                box.Focus();
                box.SelectAll();
            });
        }
    }

    private void OnRenameBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Escape && sender is TextBox { DataContext: FolderViewModel folder })
        {
            folder.IsRenaming = false;
            Tree.Focus();
            e.Handled = true;
        }
    }

    private void OnRenameBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: FolderViewModel folder })
        {
            folder.IsRenaming = false;
        }
    }

    private void OnTreeMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(Tree);
        _dragCandidate = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext as TreeNodeViewModel;
    }

    private void OnTreeMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate is null || _dragCandidate.IsReadOnly)
        {
            return;
        }

        var delta = e.GetPosition(Tree) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var node = _dragCandidate;
        _dragCandidate = null;
        DragDrop.DoDragDrop(Tree, new DataObject(typeof(TreeNodeViewModel), node), DragDropEffects.Move);
    }

    private static (TreeNodeViewModel Target, DropPosition Position)? DropTarget(DragEventArgs e)
    {
        var item = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not TreeNodeViewModel target)
        {
            return null;
        }

        var header = (FrameworkElement?)item.Template.FindName("PART_Header", item) ?? item;
        var y = e.GetPosition(header).Y;
        var height = Math.Max(1, header.ActualHeight);
        var position = target is FolderViewModel
            ? (y < height * 0.25 ? DropPosition.Before : y > height * 0.75 ? DropPosition.After : DropPosition.Inside)
            : (y < height / 2 ? DropPosition.Before : DropPosition.After);
        return (target, position);
    }

    private void OnTreeDragOver(object sender, DragEventArgs e)
    {
        var node = e.Data.GetData(typeof(TreeNodeViewModel)) as TreeNodeViewModel;
        var drop = DropTarget(e);
        e.Effects = node is not null && drop is { } d && MainViewModel.CanMove(node, d.Target, d.Position) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnTreeDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(TreeNodeViewModel)) is TreeNodeViewModel node && DropTarget(e) is { } drop)
        {
            _viewModel.Move(node, drop.Target, drop.Position);
        }

        e.Handled = true;
    }
}
