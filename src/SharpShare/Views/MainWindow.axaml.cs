using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using SharpShare.Models;
using SharpShare.Network;
using SharpShare.Network.Nat;
using SharpShare.Storage;
using SharpShare.Themes;
using SharpShare.Transfer;
using SharpShare.ViewModels;
using System.ComponentModel;
using System.Globalization;

namespace SharpShare.Views;

public partial class MainWindow : Window
{
    private readonly AppState state = App.State;
    private PeerListener? listener;
    private PeerSession? activeSession;
    private NetworkSetup? networkSetup;
    private FileTransferEngine? transferEngine;
    private TransferQueue? transferQueue;
    private SharedFolderWatcher? folderWatcher;
    private readonly Dictionary<uint, ReceiveContext> activeReceives = new();
    private readonly Dictionary<uint, TransferProgressState> activeProgressStates = new();
    private DispatcherTimer? progressTimer;
    private CancellationTokenSource? sessionCts;
    private const int MaxConcurrentDownloads = 3;
    private int activeDownloadCount;
    private readonly Queue<(RemoteFileItem File, TransferItem Item)> pendingDownloads = new();

    // Value converters for compiled bindings
    public static readonly IValueConverter FileIconConverter = new FileIconValueConverter();
    public static readonly IValueConverter TransferIconConverter = new TransferIconValueConverter();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = state;

        // Wire up button events
        BrowseFolderButton.Click += OnBrowseFolder;
        HostButton.Click += OnHostSession;
        JoinButton.Click += OnJoinSession;
        CancelHostButton.Click += OnCancelHost;
        CopyAddressButton.Click += OnCopyAddress;
        CopyPassphraseButton.Click += OnCopyPassphrase;
        DisconnectButton.Click += OnDisconnect;
        RefreshButton.Click += OnRefreshFiles;
        DownloadButton.Click += OnDownloadSelected;
        ClearCompletedButton.Click += OnClearCompleted;
        SelectAllCheckbox.IsCheckedChanged += OnSelectAllToggled;

        // Menu items
        MenuSettings.Click += OnOpenSettings;
        MenuHistory.Click += OnOpenHistory;
        MenuThemeToggle.Click += OnToggleTheme;
        MenuExit.Click += (_, _) => Close();

        // Apply saved state
        UpdateSharedFolderDisplay();
        UpdateThemeMenuItem();

        // Bind transfer list
        TransferList.ItemsSource = state.Transfers;

        // Listen for state changes
        state.PropertyChanged += OnStateChanged;

        // Start progress timer (4Hz)
        progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        progressTimer.Tick += OnProgressTimerTick;
        progressTimer.Start();
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppState.CurrentScreen))
        {
            UpdateScreenVisibility();
        }
    }

    private void UpdateScreenVisibility()
    {
        Dispatcher.UIThread.Post(() =>
        {
            SetupScreen.IsVisible = state.CurrentScreen == AppScreen.Setup;
            WaitingScreen.IsVisible = state.CurrentScreen == AppScreen.WaitingForConnection;
            ConnectedScreen.IsVisible = state.CurrentScreen == AppScreen.Connected;
        });
    }

    // --- Setup Screen ---

    private async void OnBrowseFolder(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var storageProvider = StorageProvider;
        var result = await storageProvider.OpenFolderPickerAsync(
            new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = "Select Shared Folder",
                AllowMultiple = false,
            });

        if (result.Count > 0)
        {
            string path = result[0].Path.LocalPath;
            state.SharedFolderPath = path;

            var settings = SettingsManager.Load();
            settings.SharedFolderPath = path;
            SettingsManager.Save(settings);

            UpdateSharedFolderDisplay();
            RollingFileLogger.Log(LogLevel.Info, $"Shared folder set to: {path}");
        }
    }

    private void UpdateSharedFolderDisplay()
    {
        SharedFolderText.Text = string.IsNullOrEmpty(state.SharedFolderPath)
            ? "No folder selected"
            : state.SharedFolderPath;
    }

    private async void OnHostSession(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(state.SharedFolderPath))
        {
            // TODO: show error            
            return;
        }

        sessionCts = new CancellationTokenSource();
        var settings = SettingsManager.Load();

        // Generate passphrase
        string passphrase = Authenticator.GeneratePassphrase();
        state.Passphrase = passphrase;
        PassphraseText.Text = passphrase;

        state.CurrentScreen = AppScreen.WaitingForConnection;
        state.StatusMessage = "Starting listener...";

        // Network setup (UPnP + STUN)
        networkSetup = new NetworkSetup();
        try
        {
            var netResult = await networkSetup.SetupAsync(settings.ListeningPort, sessionCts.Token);
            state.ConnectionAddress = netResult.ConnectionAddress;
            AddressText.Text = netResult.ConnectionAddress;

            if (netResult.PortForwardingSucceeded && !netResult.IsDoubleNat)
            {
                UpnpStatusIcon.Text = "✅";
                UpnpStatusText.Text = netResult.NetworkStatusSummary;
                state.IsUpnpSuccess = true;
            }
            else if (netResult.PortForwardingSucceeded && netResult.IsDoubleNat)
            {
                UpnpStatusIcon.Text = "⚠️";
                UpnpStatusText.Text = netResult.NetworkStatusSummary;
                state.IsUpnpSuccess = false;
            }
            else if (netResult.PublicIp != null)
            {
                UpnpStatusIcon.Text = "ℹ️";
                UpnpStatusText.Text = netResult.NetworkStatusSummary;
                state.IsUpnpSuccess = false;
            }
            else
            {
                UpnpStatusIcon.Text = "⚠️";
                UpnpStatusText.Text = netResult.NetworkStatusSummary;
                state.IsUpnpSuccess = false;
            }
        }
        catch (Exception ex)
        {
            // Non-fatal — continue with just the listener
            AddressText.Text = $"localhost:{settings.ListeningPort}";
            UpnpStatusIcon.Text = "⚠️";
            UpnpStatusText.Text = $"Network setup failed: {ex.Message}";
            RollingFileLogger.LogError("Network setup failed", ex);
        }

        // Start listener
        listener = new PeerListener();
        listener.PeerAuthenticated += OnPeerAuthenticated;
        listener.ListenerError += error =>
            RollingFileLogger.Log(LogLevel.Error, $"Listener error: {error}");

        try
        {
            listener.Start(settings.ListeningPort, passphrase);
            state.StatusMessage = "Waiting for connection...";
        }
        catch (Exception ex)
        {
            state.StatusMessage = $"Failed to start listener: {ex.Message}";
            RollingFileLogger.LogError("Failed to start listener", ex);
        }
    }

    private async void OnPeerAuthenticated(PeerSession session)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            sessionCts = new CancellationTokenSource();
            activeSession = session;
            SetupConnectedSession();
            state.CurrentScreen = AppScreen.Connected;
            state.IsConnected = true;
            ConnectedToText.Text = "Connected to peer";

            RollingFileLogger.Log(LogLevel.Info, "Peer connected and authenticated");

            // Request the joiner's file list
            try
            {
                await activeSession.SendFileListRequestAsync(sessionCts.Token);
            }
            catch (Exception ex)
            {
                RollingFileLogger.LogError("Failed to request file list from peer", ex);
            }
        });
    }

    private async void OnJoinSession(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(state.SharedFolderPath))
        {
            return;
        }

        // Show join dialog
        var dialog = new JoinDialog();
        var result = await dialog.ShowDialog<JoinDialogResult?>(this);
        if (result == null)
        {
            return;
        }

        sessionCts = new CancellationTokenSource();
        state.StatusMessage = "Connecting...";

        try
        {
            var session = await PeerConnector.ConnectAsync(
                result.Host, result.Port, result.Passphrase, sessionCts.Token);

            if (session == null)
            {
                state.StatusMessage = "Connection failed — check address and passphrase";
                return;
            }

            activeSession = session;
            SetupConnectedSession();
            state.CurrentScreen = AppScreen.Connected;
            state.IsConnected = true;
            ConnectedToText.Text = $"Connected to {result.Host}:{result.Port}";

            // Request file list
            await activeSession.SendFileListRequestAsync(sessionCts.Token);
        }
        catch (Exception ex)
        {
            state.StatusMessage = $"Connection failed: {ex.Message}";
            RollingFileLogger.LogError("Join failed", ex);
        }
    }

    private void SetupConnectedSession()
    {
        if (activeSession == null)
        {
            return;
        }

        activeSession.StartMessageLoops();

        transferEngine = new FileTransferEngine(activeSession, state.SharedFolderPath);
        transferQueue = new TransferQueue();

        // Watch shared folder
        folderWatcher = new SharedFolderWatcher(state.SharedFolderPath);
        folderWatcher.FilesChanged += OnLocalFilesChanged;

        // Wire session events
        activeSession.FileListReceived += OnRemoteFileListReceived;
        activeSession.FileListRequested += OnFileListRequested;
        activeSession.FileDownloadRequested += OnFileDownloadRequested;
        activeSession.FileChunkReceived += OnFileChunkReceived;
        activeSession.FileTransferCompleted += OnFileTransferCompleted;
        activeSession.FileTransferErrorReceived += OnFileTransferError;
        activeSession.FileTransferCancelled += OnFileTransferCancelled;
        activeSession.Disconnected += () => OnPeerDisconnected("Peer disconnected");
        activeSession.ErrorOccurred += error =>
        {
            RollingFileLogger.Log(LogLevel.Error, $"Session error: {error}");
            OnPeerDisconnected(error);
        };
    }

    // --- File list exchange ---

    private void OnRemoteFileListReceived(FileListResponseMessage response)
    {
        Dispatcher.UIThread.Post(() =>
        {
            state.RemoteFiles.Clear();
            foreach (var entry in response.Files.OrderBy(e => e.RelativePath))
            {
                // Skip files that already exist locally with the same size
                if (!string.IsNullOrEmpty(state.SharedFolderPath))
                {
                    string localPath = Path.Combine(state.SharedFolderPath, entry.RelativePath.Replace('/', '\\'));
                    if (File.Exists(localPath) && new FileInfo(localPath).Length == entry.SizeBytes)
                    {
                        continue;
                    }
                }

                state.RemoteFiles.Add(new RemoteFileItem
                {
                    FileName = Path.GetFileName(entry.RelativePath),
                    RelativePath = entry.RelativePath,
                    SizeBytes = entry.SizeBytes,
                    LastModifiedUtc = DateTime.FromFileTimeUtc(entry.LastModifiedUtcTicks),
                    IsDirectory = false,
                    IsSelected = true,
                });
            }
            FileList.ItemsSource = state.RemoteFiles;
            UpdateSelectAllCheckbox();
        });
    }

    private async void OnFileListRequested()
    {
        if (activeSession == null || folderWatcher == null)
        {
            return;
        }

        var files = folderWatcher.EnumerateFiles();
        var entries = files.Select(f => new FileListEntry(
            f.SizeBytes, f.LastModifiedUtc.ToFileTimeUtc(), f.RelativePath)).ToArray();
        await activeSession.SendFileListResponseAsync(entries, sessionCts?.Token ?? default);
    }

    private void OnLocalFilesChanged()
    {
        // Re-send file list to peer when our files change
        OnFileListRequested();
    }

    private void OnSelectAllToggled(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.IsChecked.HasValue)
        {
            bool selectAll = cb.IsChecked.Value;
            foreach (var file in state.RemoteFiles)
            {
                file.IsSelected = selectAll;
            }
        }
    }

    private void UpdateSelectAllCheckbox()
    {
        SelectAllCheckbox.IsChecked = state.RemoteFiles.Count > 0
            && state.RemoteFiles.All(f => f.IsSelected);
    }

    // --- Transfer handling ---

    private async void OnFileDownloadRequested(FileDownloadRequestMessage request)
    {
        if (transferEngine == null || transferQueue == null)
        {
            return;
        }

        string fullPath = SharedFolderWatcher.GetFullPath(state.SharedFolderPath, request.RelativePath);
        if (!File.Exists(fullPath))
        {
            await activeSession!.SendFileTransferErrorAsync(
                request.TransferId, TransferErrorCode.FileNotFound, "File not found");
            return;
        }

        long fileSize = new FileInfo(fullPath).Length;
        var progressState = transferQueue.TrackUpload(request.TransferId, request.RelativePath, fileSize);
        activeProgressStates[request.TransferId] = progressState;

        // Add to UI
        Dispatcher.UIThread.Post(() =>
        {
            state.Transfers.Add(new TransferItem
            {
                TransferId = request.TransferId,
                FileName = Path.GetFileName(request.RelativePath),
                RelativePath = request.RelativePath,
                TotalBytes = fileSize,
                Direction = TransferDirection.Upload,
            });
        });

        _ = Task.Run(async () =>
        {
            await transferEngine.SendFileAsync(request, progressState, sessionCts?.Token ?? default);
            progressState.Status = TransferStatus.Complete;
            transferQueue.CompleteUpload(request.TransferId);
            activeProgressStates.Remove(request.TransferId);

            // Update UI for completed upload
            Dispatcher.UIThread.Post(() =>
        {
            var item = state.Transfers.FirstOrDefault(t => t.TransferId == request.TransferId);
            double durationSeconds = 0;
            double avgSpeed = 0;

            if (item != null)
            {
                item.Status = TransferStatus.Complete;
                item.StatusText = "Complete";
                item.ProgressPercent = 100;
                item.SpeedText = "";
                item.EtaText = "";

                durationSeconds = (DateTime.UtcNow - item.StartedAtUtc).TotalSeconds;
                if (durationSeconds > 0)
                {
                    avgSpeed = fileSize / durationSeconds;
                }
            }

            // Record in history
            TransferHistoryStore.Append(new TransferHistoryEntry
                {
                    TimestampUtc = DateTime.UtcNow,
                    FileName = Path.GetFileName(request.RelativePath),
                    Direction = "Upload",
                    FileSizeBytes = fileSize,
                    DurationSeconds = durationSeconds,
                    AverageSpeedBytesPerSec = avgSpeed,
                    Status = "Complete",
                    PeerAddress = activeSession?.RemoteAddress ?? "",
                });
        });
        });
    }

    private void OnFileChunkReceived(FileChunkMessage chunk)
    {
        if (activeReceives.TryGetValue(chunk.TransferId, out var ctx))
        {
            _ = ctx.ProcessChunkAsync(chunk);
        }
    }

    private void OnFileTransferCompleted(FileTransferCompleteMessage msg)
    {
        if (activeReceives.TryGetValue(msg.TransferId, out var ctx))
        {
            _ = Task.Run(async () =>
            {
                bool success = await ctx.FinalizeAsync(msg.XxHash128);
                activeReceives.Remove(msg.TransferId);
                activeProgressStates.Remove(msg.TransferId);
                ctx.Dispose();

                Dispatcher.UIThread.Post(() =>
            {
                var item = state.Transfers.FirstOrDefault(t => t.TransferId == msg.TransferId);
                double durationSeconds = 0;
                double avgSpeed = 0;

                if (item != null)
                {
                    item.Status = success ? TransferStatus.Complete : TransferStatus.Failed;
                    item.StatusText = success ? "Complete" : "Hash mismatch";
                    item.ProgressPercent = success ? 100 : item.ProgressPercent;
                    item.SpeedText = "";
                    item.EtaText = "";

                    durationSeconds = (DateTime.UtcNow - item.StartedAtUtc).TotalSeconds;
                    if (durationSeconds > 0)
                    {
                        avgSpeed = item.TotalBytes / durationSeconds;
                    }
                }

                // Record in history
                TransferHistoryStore.Append(new TransferHistoryEntry
                    {
                        TimestampUtc = DateTime.UtcNow,
                        FileName = item?.FileName ?? msg.TransferId.ToString(),
                        Direction = "Download",
                        FileSizeBytes = item?.TotalBytes ?? 0,
                        DurationSeconds = durationSeconds,
                        AverageSpeedBytesPerSec = avgSpeed,
                        Status = success ? "Complete" : "Hash mismatch",
                        PeerAddress = activeSession?.RemoteAddress ?? "",
                    });

                // Start next queued download
                activeDownloadCount--;
                StartPendingDownloads();
            });
            });
        }
    }

    private void OnFileTransferError(FileTransferErrorMessage msg)
    {
        if (activeReceives.TryGetValue(msg.TransferId, out var ctx))
        {
            ctx.Fail(msg.ErrorMessage);
            activeReceives.Remove(msg.TransferId);
            activeProgressStates.Remove(msg.TransferId);
        }

        Dispatcher.UIThread.Post(() =>
        {
            var item = state.Transfers.FirstOrDefault(t => t.TransferId == msg.TransferId);
            if (item != null)
            {
                item.Status = TransferStatus.Failed;
                item.StatusText = $"Error: {msg.ErrorMessage}";
                item.SpeedText = "";
                item.EtaText = "";

                if (item.Direction == TransferDirection.Download)
                {
                    activeDownloadCount--;
                    StartPendingDownloads();
                }
            }
        });
    }

    private void OnFileTransferCancelled(FileTransferCancelMessage msg)
    {
        if (activeReceives.TryGetValue(msg.TransferId, out var ctx))
        {
            ctx.Cancel();
            activeReceives.Remove(msg.TransferId);
            activeProgressStates.Remove(msg.TransferId);
        }

        Dispatcher.UIThread.Post(() =>
        {
            var item = state.Transfers.FirstOrDefault(t => t.TransferId == msg.TransferId);
            if (item != null)
            {
                item.Status = TransferStatus.Cancelled;
                item.StatusText = "Cancelled";
                item.SpeedText = "";
                item.EtaText = "";

                if (item.Direction == TransferDirection.Download)
                {
                    activeDownloadCount--;
                    StartPendingDownloads();
                }
            }
        });
    }

    private void OnPeerDisconnected(string reason)
    {
        Dispatcher.UIThread.Post(() =>
        {
            state.IsConnected = false;
            state.CurrentScreen = AppScreen.Setup;
            state.StatusMessage = $"Disconnected: {reason}";
            CleanupSession();
        });
    }

    // --- UI Actions ---

    private void OnDownloadSelected(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (activeSession == null || transferEngine == null || transferQueue == null)
        {
            return;
        }

        var selected = state.RemoteFiles.Where(f => f.IsSelected && !f.IsDirectory).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        foreach (var file in selected)
        {
            // Add to transfers panel immediately as Queued
            var queuedItem = new TransferItem
            {
                TransferId = 0, // assigned when actually started
                FileName = file.FileName,
                RelativePath = file.RelativePath,
                TotalBytes = file.SizeBytes,
                Direction = TransferDirection.Download,
            };
            state.Transfers.Add(queuedItem);
            pendingDownloads.Enqueue((file, queuedItem));
            state.RemoteFiles.Remove(file);
        }
        UpdateSelectAllCheckbox();
        StartPendingDownloads();
    }

    private async void StartPendingDownloads()
    {
        while (pendingDownloads.Count > 0 && activeDownloadCount < MaxConcurrentDownloads)
        {
            if (activeSession == null || transferEngine == null)
            {
                return;
            }

            var (file, queuedItem) = pendingDownloads.Dequeue();
            activeDownloadCount++;

            try
            {
                var (transferId, progressState) = await transferEngine.RequestDownloadAsync(
                    file.RelativePath, file.SizeBytes, sessionCts?.Token ?? default);

                activeProgressStates[transferId] = progressState;

                var receiveCtx = transferEngine.StartReceive(
                    transferId, file.RelativePath, file.SizeBytes, progressState);
                activeReceives[transferId] = receiveCtx;

                // Update the queued item with the real transfer ID
                queuedItem.TransferId = transferId;
                queuedItem.StartedAtUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                activeDownloadCount--;
                queuedItem.Status = TransferStatus.Failed;
                queuedItem.StatusText = "Failed to start";
                RollingFileLogger.LogError($"Failed to start download: {file.FileName}", ex);
            }
        }
    }

    private async void OnRefreshFiles(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (activeSession == null)
        {
            return;
        }

        await activeSession.SendFileListRequestAsync(sessionCts?.Token ?? default);
    }

    private void OnClearCompleted(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var completed = state.Transfers
            .Where(t => t.Status is TransferStatus.Complete or TransferStatus.Failed or TransferStatus.Cancelled)
            .ToList();
        foreach (var item in completed)
        {
            state.Transfers.Remove(item);
        }
    }

    private async void OnCancelTransferItem(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not TransferItem item)
        {
            return;
        }

        if (item.Status is TransferStatus.Complete or TransferStatus.Failed or TransferStatus.Cancelled)
        {
            return;
        }

        bool wasQueued = item.Status == TransferStatus.Queued;
        bool isDownload = item.Direction == TransferDirection.Download;
        uint transferId = item.TransferId;

        // Cancel active receive (download) — dispose and delete temp file
        if (isDownload && transferId != 0 && activeReceives.TryGetValue(transferId, out var ctx))
        {
            ctx.CancelAndDeleteTempFile();
            activeReceives.Remove(transferId);
        }

        if (transferId != 0)
        {
            activeProgressStates.Remove(transferId);
        }

        // Notify peer so they stop sending/receiving
        if (activeSession != null && transferId != 0)
        {
            try
            {
                await activeSession.SendFileTransferCancelAsync(transferId);
            }
            catch
            {
            }
        }

        // Remove from pending download queue if queued
        if (wasQueued && isDownload)
        {
            var remaining = new Queue<(RemoteFileItem File, TransferItem Item)>();
            while (pendingDownloads.Count > 0)
            {
                var pending = pendingDownloads.Dequeue();
                if (pending.Item != item)
                {
                    remaining.Enqueue(pending);
                }
            }
            while (remaining.Count > 0)
            {
                pendingDownloads.Enqueue(remaining.Dequeue());
            }
        }

        // Update UI
        item.Status = TransferStatus.Cancelled;
        item.StatusText = "Cancelled";
        item.SpeedText = "";
        item.EtaText = "";

        // Downloads: put back in "Their Files" and adjust concurrency counter
        if (isDownload)
        {
            if (!wasQueued)
            {
                activeDownloadCount--;
            }

            state.RemoteFiles.Add(new RemoteFileItem
            {
                FileName = item.FileName,
                RelativePath = item.RelativePath,
                SizeBytes = item.TotalBytes,
                LastModifiedUtc = DateTime.UtcNow,
                IsDirectory = false,
                IsSelected = false,
            });
            UpdateSelectAllCheckbox();
            StartPendingDownloads();
        }

        state.Transfers.Remove(item);
    }

    private async void OnCopyAddress(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Clipboard != null)
        {
            await Clipboard.SetTextAsync(state.ConnectionAddress);
        }
    }

    private async void OnCopyPassphrase(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Clipboard != null)
        {
            await Clipboard.SetTextAsync(state.Passphrase);
        }
    }

    private void OnCancelHost(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        CleanupSession();
        state.CurrentScreen = AppScreen.Setup;
    }

    private async void OnDisconnect(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (activeSession != null)
        {
            await activeSession.DisconnectAsync("User disconnected");
        }

        CleanupSession();
        state.CurrentScreen = AppScreen.Setup;
        state.IsConnected = false;
    }

    private void OnToggleTheme(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        state.IsDarkMode = !state.IsDarkMode;
        ThemeManager.ApplyTheme(state.IsDarkMode);
        UpdateThemeMenuItem();

        var settings = SettingsManager.Load();
        settings.IsDarkMode = state.IsDarkMode;
        SettingsManager.Save(settings);
    }

    private void UpdateThemeMenuItem()
    {
        MenuThemeToggle.Header = state.IsDarkMode ? "☀️ Light Mode" : "🌙 Dark Mode";
    }

    private async void OnOpenSettings(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var dialog = new SettingsWindow();
        await dialog.ShowDialog(this);
    }

    private async void OnOpenHistory(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var dialog = new TransferHistoryWindow();
        await dialog.ShowDialog(this);
    }

    // --- Progress Timer ---

    private void OnProgressTimerTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;

        // Update latency display
        if (activeSession != null)
        {
            LatencyText.Text = $"Ping: {activeSession.LatencyMs}ms";
        }

        foreach (var transfer in state.Transfers)
        {
            if (!activeProgressStates.TryGetValue(transfer.TransferId, out var progressState))
            {
                continue;
            }

            if (progressState.Status == TransferStatus.Active)
            {
                long currentBytes = Interlocked.Read(ref progressState.BytesTransferred);
                double percent = progressState.TotalBytes > 0
                    ? (double)currentBytes / progressState.TotalBytes * 100.0
                    : 0;

                transfer.ProgressPercent = percent;
                transfer.Status = TransferStatus.Active;

                // Rolling speed calculation
                double elapsed = (now - transfer.LastSpeedCheck).TotalSeconds;
                if (elapsed >= 1.0)
                {
                    long delta = currentBytes - transfer.LastBytesTransferred;
                    long speed = (long)(delta / elapsed);

                    transfer.SpeedSamples[transfer.SpeedSampleIndex % 10] = speed;
                    transfer.SpeedSampleIndex++;
                    transfer.LastBytesTransferred = currentBytes;
                    transfer.LastSpeedCheck = now;

                    int samples = Math.Min(transfer.SpeedSampleIndex, 10);
                    long avgSpeed = 0;
                    for (int i = 0;i < samples;i++)
                    {
                        avgSpeed += transfer.SpeedSamples[i];
                    }

                    avgSpeed /= samples;

                    transfer.SpeedText = FormatSpeed(avgSpeed);
                    if (avgSpeed > 0)
                    {
                        long remaining = progressState.TotalBytes - currentBytes;
                        int etaSeconds = (int)(remaining / avgSpeed);
                        transfer.EtaText = $"ETA: {FormatDuration(etaSeconds)}";
                    }
                }

                transfer.StatusText = $"{percent:F0}%";
            }
            else if (progressState.Status == TransferStatus.Complete)
            {
                transfer.Status = TransferStatus.Complete;
                transfer.StatusText = "Complete";
                transfer.ProgressPercent = 100;
                transfer.SpeedText = "";
                transfer.EtaText = "";
            }
            else if (progressState.Status == TransferStatus.Failed)
            {
                transfer.Status = TransferStatus.Failed;
                transfer.StatusText = progressState.ErrorMessage ?? "Failed";
                transfer.SpeedText = "";
                transfer.EtaText = "";
            }
        }

        ResortTransfers();
    }

    /// <summary>
    /// Keeps the transfer list sorted: Active first, then Queued, then completed/failed/cancelled. Uses minimal moves —
    /// only repositions items that are out of order.
    /// </summary>
    private void ResortTransfers()
    {
        var transfers = state.Transfers;
        for (int i = 1;i < transfers.Count;i++)
        {
            if (transfers[i].SortOrder < transfers[i - 1].SortOrder)
            {
                // Find correct position
                int target = i - 1;
                while (target > 0 && transfers[i].SortOrder < transfers[target - 1].SortOrder)
                {
                    target--;
                }

                transfers.Move(i, target);
            }
        }
    }

    private static string FormatSpeed(long bytesPerSecond)
    {
        if (bytesPerSecond < 1024)
        {
            return $"{bytesPerSecond} B/s";
        }

        if (bytesPerSecond < 1024 * 1024)
        {
            return $"{bytesPerSecond / 1024.0:F1} KB/s";
        }

        if (bytesPerSecond < 1024L * 1024 * 1024)
        {
            return $"{bytesPerSecond / (1024.0 * 1024):F1} MB/s";
        }

        return $"{bytesPerSecond / (1024.0 * 1024 * 1024):F2} GB/s";
    }

    private static string FormatDuration(int totalSeconds)
    {
        if (totalSeconds < 60)
        {
            return $"{totalSeconds}s";
        }

        if (totalSeconds < 3600)
        {
            return $"{totalSeconds / 60}m {totalSeconds % 60}s";
        }

        return $"{totalSeconds / 3600}h {totalSeconds % 3600 / 60}m";
    }

    // --- Cleanup ---

    private async void CleanupSession()
    {
        sessionCts?.Cancel();
        sessionCts?.Dispose();
        sessionCts = null;

        listener?.Stop();
        listener = null;

        if (networkSetup != null)
        {
            try
            {
                await networkSetup.CleanupAsync();
            }
            catch
            {
            }
            networkSetup.Dispose();
            networkSetup = null;
        }

        activeSession = null;
        transferEngine = null;
        transferQueue = null;
        folderWatcher?.Dispose();
        folderWatcher = null;

        foreach (var ctx in activeReceives.Values)
        {
            ctx.Dispose();
        }

        activeReceives.Clear();
        activeProgressStates.Clear();
        pendingDownloads.Clear();
        activeDownloadCount = 0;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        progressTimer?.Stop();
        CleanupSession();
        base.OnClosing(e);
    }
}

// --- Value Converters ---

public class FileIconValueConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isDirectory = value is true;
        var app = Application.Current;
        if (app == null)
        {
            return null;
        }

        string key = isDirectory ? "FolderIcon" : "FileIcon";
        return app.TryFindResource(key, out var resource) ? resource : null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public class TransferIconValueConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var direction = value is TransferDirection d ? d : TransferDirection.Download;
        var app = Application.Current;
        if (app == null)
        {
            return null;
        }

        string key = direction == TransferDirection.Download ? "DownloadIcon" : "UploadIcon";
        return app.TryFindResource(key, out var resource) ? resource : null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
