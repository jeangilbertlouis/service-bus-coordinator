using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ServiceBusCoordinator.App.ViewModels;

namespace ServiceBusCoordinator.App.Views;

public partial class MainWindow : Window
{
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

        // Select the item under the pointer on right-click, so the tree's context menu acts on it.
        NavTree.AddHandler(PointerPressedEvent, OnNavTreePointerPressed, RoutingStrategies.Tunnel);
    }

    private void OnNavTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(NavTree).Properties.IsRightButtonPressed)
        {
            return;
        }

        var item = (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true);
        if (item?.DataContext is TreeNodeViewModel node && DataContext is MainWindowViewModel vm && vm.SelectedNode != node)
        {
            vm.SelectedNode = node;
        }
    }
}
