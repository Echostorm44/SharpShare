using Avalonia;

namespace SharpShare;

public static class Program
{
    private static Mutex? singleInstanceMutex;

    [STAThread]
    public static void Main(string[] args)
    {
        singleInstanceMutex = new Mutex(true, "SharpShare-SingleInstance-9500", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            // Another instance is already running
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            singleInstanceMutex.ReleaseMutex();
            singleInstanceMutex.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
