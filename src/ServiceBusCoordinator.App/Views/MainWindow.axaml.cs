using Avalonia.Controls;

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
    }
}
