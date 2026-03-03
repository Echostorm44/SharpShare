using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SharpShare.Storage;
using SharpShare.Themes;
using SharpShare.ViewModels;
using SharpShare.Views;

namespace SharpShare;

public partial class App : Application
{
    public static AppState State { get; } = new();

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Initialize logger (auto-initializes on first log, no Initialize method needed)
        RollingFileLogger.Log(LogLevel.Info, "SharpShare starting");

        // Load settings
        var settings = SettingsManager.Load();
        State.SharedFolderPath = settings.SharedFolderPath;
        State.IsDarkMode = settings.IsDarkMode;

        // Apply theme
        ThemeManager.ApplyTheme(State.IsDarkMode);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
            desktop.ShutdownRequested += OnShutdownRequested;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        RollingFileLogger.Log(LogLevel.Info, "SharpShare shutting down");
        RollingFileLogger.Shutdown();
    }
}
