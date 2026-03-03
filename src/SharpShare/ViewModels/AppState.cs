using SharpShare.Models;
using SharpShare.Transfer;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SharpShare.ViewModels;

/// <summary>
/// The single source of truth for the application's state. Bound directly to the UI using INotifyPropertyChanged. No
/// MVVM framework — just simple property notifications.
/// </summary>
public sealed class AppState : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private AppScreen currentScreen = AppScreen.Setup;
    private string statusMessage = "";
    private string connectionAddress = "";
    private string passphrase = "";
    private string upnpStatus = "";
    private bool isUpnpSuccess;
    private string peerName = "";
    private string sharedFolderPath = "";
    private bool isDarkMode;
    private bool isConnected;

    public AppScreen CurrentScreen
    {
        get => currentScreen;
        set
        {
            if (currentScreen != value)
            {
                currentScreen = value;
                OnPropertyChanged();
            }
        }
    }

    public string StatusMessage
    {
        get => statusMessage;
        set
        {
            if (statusMessage != value)
            {
                statusMessage = value;
                OnPropertyChanged();
            }
        }
    }

    public string ConnectionAddress
    {
        get => connectionAddress;
        set
        {
            if (connectionAddress != value)
            {
                connectionAddress = value;
                OnPropertyChanged();
            }
        }
    }

    public string Passphrase
    {
        get => passphrase;
        set
        {
            if (passphrase != value)
            {
                passphrase = value;
                OnPropertyChanged();
            }
        }
    }

    public string UpnpStatus
    {
        get => upnpStatus;
        set
        {
            if (upnpStatus != value)
            {
                upnpStatus = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsUpnpSuccess
    {
        get => isUpnpSuccess;
        set
        {
            if (isUpnpSuccess != value)
            {
                isUpnpSuccess = value;
                OnPropertyChanged();
            }
        }
    }

    public string PeerName
    {
        get => peerName;
        set
        {
            if (peerName != value)
            {
                peerName = value;
                OnPropertyChanged();
            }
        }
    }

    public string SharedFolderPath
    {
        get => sharedFolderPath;
        set
        {
            if (sharedFolderPath != value)
            {
                sharedFolderPath = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsDarkMode
    {
        get => isDarkMode;
        set
        {
            if (isDarkMode != value)
            {
                isDarkMode = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsConnected
    {
        get => isConnected;
        set
        {
            if (isConnected != value)
            {
                isConnected = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Remote peer's file list.
    /// </summary>
    public ObservableCollection<RemoteFileItem> RemoteFiles { get; } = new();

    /// <summary>
    /// Active and completed transfers for the transfer panel.
    /// </summary>
    public ObservableCollection<TransferItem> Transfers { get; } = new();

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public enum AppScreen
{
    Setup,
    WaitingForConnection,
    Connected
}

/// <summary>
/// A file entry from the remote peer, displayed in the file browser.
/// </summary>
public sealed class RemoteFileItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public required string FileName { get; init; }
    public required string RelativePath { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTime LastModifiedUtc { get; init; }
    public required bool IsDirectory { get; init; }

    private bool isSelected;
    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (isSelected != value)
            {
                isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }

    /// <summary>
    /// Shows relative path with folder context (e.g., "Movies/BigMovie.mkv").
    /// </summary>
    public string DisplayPath => RelativePath.Replace('\\', '/');

    public string FormattedSize => FormatFileSize(SizeBytes);

    internal static string FormatFileSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:F1} KB";
        }

        if (bytes < 1024L * 1024 * 1024)
        {
            return $"{bytes / (1024.0 * 1024):F1} MB";
        }

        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}

/// <summary>
/// UI model for an active or completed transfer displayed in the transfer panel.
/// </summary>
public sealed class TransferItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public required uint TransferId { get; set; }
    public required string FileName { get; init; }
    public required string RelativePath { get; init; }
    public required long TotalBytes { get; init; }
    public required TransferDirection Direction { get; init; }
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;

    private double progressPercent;
    private string speedText = "";
    private string etaText = "";
    private string statusText = "Queued";
    private TransferStatus status = TransferStatus.Queued;

    public double ProgressPercent
    {
        get => progressPercent;
        set
        {
            if (Math.Abs(progressPercent - value) > 0.001)
            {
                progressPercent = value;
                Notify();
            }
        }
    }

    public string SpeedText
    {
        get => speedText;
        set
        {
            if (speedText != value)
            {
                speedText = value;
                Notify();
            }
        }
    }

    public string EtaText
    {
        get => etaText;
        set
        {
            if (etaText != value)
            {
                etaText = value;
                Notify();
            }
        }
    }

    public string StatusText
    {
        get => statusText;
        set
        {
            if (statusText != value)
            {
                statusText = value;
                Notify();
            }
        }
    }

    public TransferStatus Status
    {
        get => status;
        set
        {
            if (status != value)
            {
                status = value;
                Notify();
                Notify(nameof(IsActive));
                Notify(nameof(IsQueued));
                Notify(nameof(IsCancellable));
            }
        }
    }

    public bool IsActive => status == TransferStatus.Active;
    public bool IsQueued => status == TransferStatus.Queued;
    public bool IsCancellable => status is TransferStatus.Queued or TransferStatus.Active;

    /// <summary>
    /// Sort order: Active=0, Queued=1, everything else=2.
    /// </summary>
    internal int SortOrder => status switch
    {
        TransferStatus.Active => 0,
        TransferStatus.Queued => 1,
        _ => 2,
    };

    public string FormattedTotalSize => RemoteFileItem.FormatFileSize(TotalBytes);

    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // Rolling speed calculation state (internal, not bound to UI)
    internal readonly long[] SpeedSamples = new long[10];
    internal int SpeedSampleIndex;
    internal long LastBytesTransferred;
    internal DateTime LastSpeedCheck = DateTime.UtcNow;
}
