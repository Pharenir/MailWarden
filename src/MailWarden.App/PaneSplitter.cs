using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace MailWarden.App;

/// <summary>
/// Thin bar between two panes that reports how far it is dragged; the window applies the size. Written here because the
/// toolkit's splitter would be a new package.
/// </summary>
public sealed partial class PaneSplitter : Grid
{
    private double dragStartPosition;
    private bool isDragging;
    private bool isHorizontalBar;

    /// <summary>Creates the bar with a transparent hit area.</summary>
    public PaneSplitter()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += OnPointerCaptureLost;
        UpdateCursor();
    }

    /// <summary>Raised while dragging with the distance from the start of the drag, in pixels.</summary>
    public event EventHandler<double>? Dragged;

    /// <summary>Raised when the drag ends.</summary>
    public event EventHandler? DragCompleted;

    /// <summary>True when the bar lies between panes above each other and moves vertically.</summary>
    public bool IsHorizontalBar
    {
        get => isHorizontalBar;
        set
        {
            isHorizontalBar = value;
            UpdateCursor();
        }
    }

    private void UpdateCursor()
    {
        ProtectedCursor = InputSystemCursor.Create(
            isHorizontalBar ? InputSystemCursorShape.SizeNorthSouth : InputSystemCursorShape.SizeWestEast);
    }

    // Measured against the window content: the bar itself moves during the drag.
    private double ReadPosition(PointerRoutedEventArgs args)
    {
        Windows.Foundation.Point position = args.GetCurrentPoint(XamlRoot.Content).Position;
        return isHorizontalBar ? position.Y : position.X;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        isDragging = CapturePointer(args.Pointer);
        dragStartPosition = ReadPosition(args);
        args.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (isDragging)
        {
            Dragged?.Invoke(this, ReadPosition(args) - dragStartPosition);
            args.Handled = true;
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        ReleasePointerCapture(args.Pointer);
        args.Handled = true;
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        if (isDragging)
        {
            isDragging = false;
            DragCompleted?.Invoke(this, EventArgs.Empty);
        }
    }
}
