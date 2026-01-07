using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using VMAFLite.ViewModels;
using VMAFLite.Views;

namespace VMAFLite;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainWindowViewModel();
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            desktop.Exit += (s, e) => viewModel.KillFFmpeg();
        }
        base.OnFrameworkInitializationCompleted();
    }
}