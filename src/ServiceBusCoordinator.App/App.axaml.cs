using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ServiceBusCoordinator.App.Services;
using ServiceBusCoordinator.App.ViewModels;
using ServiceBusCoordinator.App.Views;

namespace ServiceBusCoordinator.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            var viewModel = new MainWindowViewModel(new DialogService(window));
            window.DataContext = viewModel;
            window.Opened += async (_, _) => await viewModel.InitializeAsync();
            window.Closing += (_, _) => viewModel.Dispose();
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
