using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ServiceBusCoordinator.App.ViewModels;

namespace ServiceBusCoordinator.App.Views;

public partial class MainWindow : Window
{
    /// <summary>How far the pointer must move with the button held before a press becomes a drag.</summary>
    private const double DragThreshold = 6;

    private TreeNodeViewModel? _dragCandidate;
    private Point _pressPoint;
    private bool _dragging;
    private DropTarget? _dropTarget;

    public MainWindow()
    {
        InitializeComponent();
        var version = typeof(MainWindow).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion.Split('+')[0];
        if (!string.IsNullOrEmpty(version))
        {
            Title = $"Service Bus Coordinator {version}";
        }

        // Tunnel so these run before the tree's own selection handling.
        NavTree.AddHandler(PointerPressedEvent, OnNavTreePointerPressed, RoutingStrategies.Tunnel);
        NavTree.AddHandler(PointerMovedEvent, OnNavTreePointerMoved, RoutingStrategies.Tunnel);
        NavTree.AddHandler(PointerReleasedEvent, OnNavTreePointerReleased, RoutingStrategies.Tunnel);
        NavTree.AddHandler(PointerCaptureLostEvent, (_, _) => EndDrag(), RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private void OnNavTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(NavTree);
        var item = (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true);
        var node = item?.DataContext as TreeNodeViewModel;

        if (point.Properties.IsRightButtonPressed)
        {
            // Select the item under the pointer, so the tree's context menu acts on it.
            if (node is not null && ViewModel is { } vm && vm.SelectedNode != node)
            {
                vm.SelectedNode = node;
            }

            return;
        }

        // A left press on a connection or folder (not on its expand arrow) may become a drag.
        var onExpander = (e.Source as Visual)?.FindAncestorOfType<ToggleButton>(includeSelf: true) is not null;
        _dragCandidate = point.Properties.IsLeftButtonPressed && !onExpander
            && node is NamespaceNodeViewModel or ConnectionFolderNodeViewModel ? node : null;
        _pressPoint = point.Position;
    }

    private void OnNavTreePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragCandidate is null || ViewModel is not { } vm)
        {
            return;
        }

        var point = e.GetCurrentPoint(NavTree);
        if (!point.Properties.IsLeftButtonPressed)
        {
            EndDrag();
            return;
        }

        if (!_dragging)
        {
            var delta = point.Position - _pressPoint;
            if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
            {
                return;
            }

            _dragging = true;
            e.Pointer.Capture(NavTree);
            NavTree.Cursor = new Cursor(StandardCursorType.DragMove);
        }

        AutoScroll(point.Position);
        _dropTarget = FindDropTarget(vm, point.Position);
        ShowDropIndicator(_dropTarget);
        e.Handled = true;
    }

    private void OnNavTreePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging && _dragCandidate is { } dragged && _dropTarget is { } target && ViewModel is { } vm)
        {
            e.Handled = true;
            EndDrag();
            vm.Move(dragged, target);
            return;
        }

        if (_dragging)
        {
            e.Handled = true;
        }

        EndDrag();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_dragging && e.Key == Key.Escape)
        {
            EndDrag();
            e.Handled = true;
        }
    }

    private void EndDrag()
    {
        var wasDragging = _dragging;
        _dragCandidate = null;
        _dropTarget = null;
        _dragging = false;
        ShowDropIndicator(null);
        if (wasDragging)
        {
            // Pointer capture ends by itself when the button is released.
            NavTree.Cursor = null;
        }
    }

    /// <summary>Finds the tree row under a point and asks the view model where a drop there would land.</summary>
    private DropTarget? FindDropTarget(MainWindowViewModel vm, Point position)
    {
        var hit = NavTree.InputHitTest(position) as Visual;
        var item = hit?.FindAncestorOfType<TreeViewItem>(includeSelf: true);
        if (item?.DataContext is not TreeNodeViewModel over)
        {
            // Below the last row: drop at the end of the top level.
            var last = vm.RootNodes.LastOrDefault();
            return last is null || _dragCandidate is null ? null : vm.ResolveDrop(_dragCandidate, last, 1);
        }

        var header = HeaderBounds(item);
        var fraction = header is { Height: > 0 } h ? (position.Y - h.Y) / h.Height : 0.5;
        return vm.ResolveDrop(_dragCandidate!, over, Math.Clamp(fraction, 0, 1));
    }

    /// <summary>The row of a tree item (its header), in the tree's coordinates, excluding any expanded children.</summary>
    private Rect? HeaderBounds(TreeViewItem item)
    {
        var header = item.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "PART_Header")
            ?? item.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "PART_LayoutRoot");
        Visual target = header ?? item;
        var topLeft = target.TranslatePoint(default, NavTree);
        return topLeft is { } p ? new Rect(p, target.Bounds.Size) : null;
    }

    private void ShowDropIndicator(DropTarget? target)
    {
        DropLine.IsVisible = DropBox.IsVisible = false;
        if (target is null || NavTree.TreeContainerFromItem(target.Node) is not TreeViewItem item
            || HeaderBounds(item) is not { } header)
        {
            return;
        }

        // Tree coordinates to overlay coordinates (the overlay covers the tree's panel, the tree has a margin).
        var origin = NavTree.TranslatePoint(default, DropOverlay) ?? default;
        var left = origin.X + header.X;
        var width = Math.Max(0, NavTree.Bounds.Width - header.X - 8);

        if (target.Placement == DropPlacement.Into)
        {
            Canvas.SetLeft(DropBox, left);
            Canvas.SetTop(DropBox, origin.Y + header.Y);
            DropBox.Width = width;
            DropBox.Height = header.Height;
            DropBox.IsVisible = true;
            return;
        }

        double y;
        if (target.Placement == DropPlacement.Before)
        {
            y = header.Y;
        }
        else
        {
            // After an expanded node means after everything shown under it.
            var itemTop = item.TranslatePoint(default, NavTree) ?? header.TopLeft;
            y = itemTop.Y + item.Bounds.Height;
        }

        Canvas.SetLeft(DropLine, left);
        Canvas.SetTop(DropLine, origin.Y + y - 1);
        DropLine.Width = width;
        DropLine.IsVisible = true;
    }

    /// <summary>Scrolls the tree while dragging near its top or bottom edge.</summary>
    private void AutoScroll(Point position)
    {
        if (NavTree.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is not { } scroller)
        {
            return;
        }

        const double edge = 28;
        var step = position.Y < edge ? -12 : position.Y > NavTree.Bounds.Height - edge ? 12 : 0;
        if (step != 0)
        {
            scroller.Offset = scroller.Offset.WithY(Math.Clamp(scroller.Offset.Y + step, 0, Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height)));
        }
    }
}
