using Avalonia.Controls;
using SharpShare.Storage;

namespace SharpShare.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();

        var settings = SettingsManager.Load();
        PortInput.Value = settings.ListeningPort;
        UpnpToggle.IsChecked = settings.EnableUpnp;
        MaxSpeedInput.Value = settings.MaxTransferSpeedMBps;

        SaveButton.Click += OnSave;
        CancelButton.Click += (_, _) => Close();
    }

    private void OnSave(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var settings = SettingsManager.Load();
        settings.ListeningPort = (int)(PortInput.Value ?? 9500);
        settings.EnableUpnp = UpnpToggle.IsChecked ?? true;
        settings.MaxTransferSpeedMBps = (int)(MaxSpeedInput.Value ?? 0);
        SettingsManager.Save(settings);

        RollingFileLogger.Log(LogLevel.Info,
            $"Settings saved: port={settings.ListeningPort}, upnp={settings.EnableUpnp}");
        Close();
    }
}
