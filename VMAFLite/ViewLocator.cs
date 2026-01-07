using Avalonia.Controls;
using Avalonia.Controls.Templates;
using VMAFLite.ViewModels;
using VMAFLite.Views;

namespace VMAFLite;

public class ViewLocator : IDataTemplate
{
    public Control? Build(object? data)
    {
        if (data is null) return null;
        if (data is MainWindowViewModel) return new MainWindow();
        return new TextBlock { Text = "Not Found: " + data.GetType().Name };
    }

    public bool Match(object? data) => data is ViewModelBase;
}