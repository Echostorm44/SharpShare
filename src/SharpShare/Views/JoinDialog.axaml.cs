using Avalonia.Controls;
using SharpShare.Network;

namespace SharpShare.Views;

public sealed class JoinDialogResult
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string Passphrase { get; init; }
}

public partial class JoinDialog : Window
{
    public JoinDialog()
    {
        InitializeComponent();

        ConnectButton.Click += OnConnect;
        CancelButton.Click += (_, _) => Close(null);
    }

    private void OnConnect(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        string address = AddressInput.Text?.Trim() ?? "";
        string passphrase = PassphraseInput.Text?.Trim() ?? "";

        if (string.IsNullOrEmpty(address) || string.IsNullOrEmpty(passphrase))
        {
            return;
        }

        try
        {
            var (host, port) = PeerConnector.ParseAddress(address);
            Close(new JoinDialogResult
            {
                Host = host,
                Port = port,
                Passphrase = passphrase,
            });
        }
        catch
        {
            // Invalid address format
            AddressInput.Focus();
        }
    }
}
