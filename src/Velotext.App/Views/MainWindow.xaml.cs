// Velotext: main window behaviour (tree selection, keyboard, drag and drop, rename, close to tray).
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
    private DropIndicatorAdorner? _dropIndicator;

    /// <summary>Creates the window.</summary>
    /// <param name="viewModel">The main view model.</param>
    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = viewModel;
        viewModel.FocusPhraseNameRequested += OnFocusPhraseName;
        viewModel.FocusNodeRequested += OnFocusNode;
        ContentEditor.LinkRequested += (current, problem) => MessageDialog.Prompt(
            this,
            "Link address",
            problem ?? "Type a web address, mailto:name@example.com or tel:+4512345678. To remove the link, clear the field.",
            current,
            "Save link");
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Find, (_, _) => FocusSearch()));
    }

    /// <summary>Raised when the user closes the window; the app decides whether to hide or exit.</summary>
    public event EventHandler<CancelEventArgs>? CloseRequested;

    /// <summary>
    /// Puts the window where it was last time. A place that no longer fits the screens (a monitor was
    /// removed) is ignored, and the window opens centred instead.
    /// </summary>
    /// <param name="placement">The saved placement, or <c>null</c>.</param>
    public void ApplyPlacement(Core.Settings.WindowPlacement? placement)
    {
        if (placement is not { IsValid: true } || !IsOnScreen(placement))
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = placement.Left;
        Top = placement.Top;
        Width = placement.Width;
        Height = placement.Height;
        if (placement.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    /// <summary>Returns the current place and size; for a maximized window, its normal place and size.</summary>
    public Core.Settings.WindowPlacement CapturePlacement()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height) : RestoreBounds;
        return new Core.Settings.WindowPlacement
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = WindowState == WindowState.Maximized,
        };
    }

    /// <summary>True when enough of the title bar area lies on the screens to grab the window.</summary>
    internal static bool IsOnScreen(Core.Settings.WindowPlacement placement)
    {
        var screens = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var titleBar = new Rect(placement.Left, placement.Top, placement.Width, 40);
        var visible = Rect.Intersect(screens, titleBar);
        return !visible.IsEmpty && visible.Width >= 120 && visible.Height >= 20;
    }

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
    protected override void OnClosed(EventArgs e)
    {
        // The window can close while Velotext keeps running (it is released when hidden for a while), so it
        // saves the last typing and lets go of the view model, which is dropped with it.
        ContentEditor.Flush();
        _viewModel.FocusPhraseNameRequested -= OnFocusPhraseName;
        _viewModel.FocusNodeRequested -= OnFocusNode;
        base.OnClosed(e);
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

    private static TreeViewItem? FindContainer(ItemsControl parent, object item)
    {
        if (parent.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem direct)
        {
            return direct;
        }

        foreach (var child in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(child) is TreeViewItem { IsExpanded: true } container
                && FindContainer(container, item) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private void OnFocusNode(object? sender, TreeNodeViewModel node)
    {
        // Wait until the tree has created the containers, then put keyboard focus on the node.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (FindContainer(Tree, node) is { } item)
            {
                item.BringIntoView();
                item.Focus();
            }
            else
            {
                Tree.Focus();
            }
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
            // Esc puts the old name back; Enter keeps the new one.
            folder.EndRename(cancel: e.Key == Key.Escape);
            OnFocusNode(this, folder);
            e.Handled = true;
        }
    }

    private void OnRenameBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: FolderViewModel { IsRenaming: true } folder })
        {
            folder.EndRename(cancel: false);
        }
    }

    private void OnTreeRightMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Right-click selects the item first, so the context menu acts on what the user clicked.
        if (FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject) is { } item)
        {
            item.IsSelected = true;
            item.Focus();
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
        var allowed = node is not null && drop is { } d && MainViewModel.CanMove(node, d.Target, d.Position);
        e.Effects = allowed ? DragDropEffects.Move : DragDropEffects.None;
        if (allowed && FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject) is { } item)
        {
            ShowDropIndicator(item, drop!.Value.Position);
        }
        else
        {
            HideDropIndicator();
        }

        e.Handled = true;
    }

    private void OnTreeDragLeave(object sender, DragEventArgs e)
    {
        // DragLeave also fires when the pointer moves between rows; hide only when it leaves the tree.
        var position = e.GetPosition(Tree);
        if (position.X < 0 || position.Y < 0 || position.X >= Tree.ActualWidth || position.Y >= Tree.ActualHeight)
        {
            HideDropIndicator();
        }
    }

    private void OnTreeDrop(object sender, DragEventArgs e)
    {
        HideDropIndicator();
        if (e.Data.GetData(typeof(TreeNodeViewModel)) is TreeNodeViewModel node && DropTarget(e) is { } drop)
        {
            _viewModel.Move(node, drop.Target, drop.Position);
        }

        e.Handled = true;
    }

    private void ShowDropIndicator(TreeViewItem item, DropPosition position)
    {
        var header = (FrameworkElement?)item.Template.FindName("PART_Header", item) ?? item;
        if (_dropIndicator is { } current && ReferenceEquals(current.AdornedElement, header))
        {
            current.Position = position;
            return;
        }

        HideDropIndicator();
        if (AdornerLayer.GetAdornerLayer(header) is { } layer)
        {
            _dropIndicator = new DropIndicatorAdorner(header, position, TryFindResource("AccentFillColorDefaultBrush") as Brush ?? SystemColors.HighlightBrush);
            layer.Add(_dropIndicator);
        }
    }

    private void HideDropIndicator()
    {
        if (_dropIndicator is { } indicator)
        {
            AdornerLayer.GetAdornerLayer(indicator.AdornedElement)?.Remove(indicator);
            _dropIndicator = null;
        }
    }
}
