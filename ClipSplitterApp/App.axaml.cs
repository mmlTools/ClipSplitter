using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ClipSplitterApp.Views;

namespace ClipSplitterApp;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Videos passed on the command line ("Open with", or dropped on the exe) are loaded at startup.
            desktop.MainWindow = new MainWindow(desktop.Args);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
