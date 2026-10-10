// Velotext: the line or outline that shows where a dragged phrase or folder will land in the tree.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Velotext.App.ViewModels;

namespace Velotext.App.Views;

/// <summary>
/// Draws the drop position on a tree row: a line above or below it for Before and After, and an
/// outline around it for Inside (into a folder).
/// </summary>
internal sealed class DropIndicatorAdorner : Adorner
{
    private readonly Pen _pen;
    private DropPosition _position;

    /// <summary>Creates the indicator.</summary>
    /// <param name="adornedElement">The row header.</param>
    /// <param name="position">Where the item lands.</param>
    /// <param name="brush">The accent brush.</param>
    public DropIndicatorAdorner(UIElement adornedElement, DropPosition position, Brush brush)
        : base(adornedElement)
    {
        _position = position;
        _pen = new Pen(brush, 2);
        _pen.Freeze();
        IsHitTestVisible = false;
    }

    /// <summary>Where the item lands; changing it redraws the indicator.</summary>
    public DropPosition Position
    {
        get => _position;
        set
        {
            if (_position != value)
            {
                _position = value;
                InvalidateVisual();
            }
        }
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var size = AdornedElement.RenderSize;
        switch (_position)
        {
            case DropPosition.Before:
                drawingContext.DrawLine(_pen, new Point(0, 1), new Point(size.Width, 1));
                break;
            case DropPosition.After:
                drawingContext.DrawLine(_pen, new Point(0, size.Height - 1), new Point(size.Width, size.Height - 1));
                break;
            default:
                drawingContext.DrawRoundedRectangle(null, _pen, new Rect(1, 1, Math.Max(0, size.Width - 2), Math.Max(0, size.Height - 2)), 4, 4);
                break;
        }
    }
}
