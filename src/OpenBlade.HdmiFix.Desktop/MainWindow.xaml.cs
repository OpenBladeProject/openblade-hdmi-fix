using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Interop;

namespace OpenBlade.HdmiFix.Desktop;

public partial class MainWindow : Window
{
    private readonly IPatcherBackend _backend;
    private readonly bool _previewMode;
    private readonly string _recordingRoot;
    private readonly Func<MonitorView, string, string, ConnectionRecording> _createRecording;
    private readonly TaskCompletionSource _initialScanCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _isBusy;
    private MonitorView? _selectedMonitor;
    private IReadOnlyList<MonitorView> _activeRecordingTargets = [];
    private bool _settingRecordingTargets;
    private bool _recordingTargetWasExplicitlyChosen;
    private ConnectionRecording? _recording;
    private CancellationTokenSource? _recordingCancellation;
    private string? _lastRecordingDirectory;
    private bool _closeAfterRecording;
    private bool _finishingRecording;
    private bool _recordingReady;

    public MainWindow(IPatcherBackend backend, bool previewMode = false)
        : this(backend, previewMode, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenBlade.HdmiFix", "Recordings"))
    {
    }

    internal MainWindow(IPatcherBackend backend, bool previewMode, string recordingRoot,
        Func<MonitorView, string, string, ConnectionRecording>? createRecording = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _recordingRoot = recordingRoot;
        _createRecording = createRecording ?? ((monitor, report, root) => new(monitor, report, root));
        _backend = backend;
        _previewMode = previewMode;
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(this) is HwndSource source)
            {
                source.AddHook(ObserveDisplayMessages);
                int darkMode = 1;
                _ = DwmSetWindowAttribute(source.Handle, 20, ref darkMode, sizeof(int));
            }
        };
        PreviewBanner.Visibility = previewMode ? Visibility.Visible : Visibility.Collapsed;
        if (previewMode)
        {
            PatchButton.ToolTip = "Patch is disabled because this window uses preview data.";
            RestoreButton.ToolTip = "Restore is disabled because this window uses preview data.";
        }

        UpdateActionStates();
    }

    public Task InitialScanCompleted => _initialScanCompletion.Task;

    internal void PrepareRecordingPreview()
    {
        if (!_previewMode) return;
        RecordConnectionButton.Visibility = Visibility.Collapsed;
        MarkBlackoutButton.Visibility = Visibility.Visible;
        StopRecordingButton.Visibility = Visibility.Visible;
        RecordingProgressBar.Visibility = Visibility.Visible;
        RecordingProgressBar.Value = 23;
        SetRecordingWarning(new RecordingProgress(TimeSpan.FromSeconds(23), 46, 2, SelectedConnected: false).Warning);
        RecordingStatusText.Text = "Recording: 37 seconds left · 46 samples · 2 blackout marks.";
        UpdateLayout();
        RecordingCard.BringIntoView();
    }

    internal void PrepareExpandedDiagnosticsPreview()
    {
        if (!_previewMode)
        {
            return;
        }

        DiagnosticsExpander.IsExpanded = true;
        UpdateLayout();
        DiagnosticsExpander.BringIntoView();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            string? recovered = await RecoverRecordingsAsync();
            await RefreshAsync(completionMessage: recovered);
        }
        finally
        {
            _initialScanCompletion.TrySetResult();
        }
    }

    private async Task<string?> RecoverRecordingsAsync()
    {
        if (_previewMode) return null;
        SetBusy(true, "Checking saved recordings…");
        try
        {
            var recovery = await Task.Run(() => RecordingRecovery.Recover(_recordingRoot));
            if (Directory.Exists(_recordingRoot))
            {
                _lastRecordingDirectory = recovery.Recordings.Count == 1 && recovery.UnavailableCount == 0 && !recovery.ScanLimitReached
                    ? recovery.Recordings[0].DirectoryPath : _recordingRoot;
                OpenRecordingButton.Visibility = Visibility.Visible;
            }
            if (!string.IsNullOrWhiteSpace(recovery.Message))
                RecordingStatusText.Text = recovery.Message;
            SetRecordingWarning(recovery.Warning);
            return recovery.Message;
        }
        catch (Exception error) when (PatcherBackend.IsExpected(error))
        {
            RecordingStatusText.Text = "Saved recordings could not be checked. Your files remain in the recording folder.";
            return RecordingStatusText.Text;
        }
        finally { SetBusy(false); }
    }

    private async Task RefreshAsync(string? preferredInstanceId = null, string? completionMessage = null)
    {
        if (_isBusy || _recording is not null)
        {
            return;
        }

        preferredInstanceId ??= _selectedMonitor?.InstanceId;
        SetBusy(true, "Scanning saved monitors…");

        try
        {
            MonitorScan scan = await _backend.ScanAsync();
            _activeRecordingTargets = scan.RecordingTargets ?? [];
            MonitorSelector.ItemsSource = scan.Monitors;

            MonitorView? selection = scan.Monitors.FirstOrDefault(
                monitor => string.Equals(monitor.InstanceId, preferredInstanceId, StringComparison.OrdinalIgnoreCase));
            MonitorSelector.SelectedItem = selection ?? scan.Monitors.FirstOrDefault();
            UpdateRecordingTargets();

            string status = string.IsNullOrWhiteSpace(completionMessage)
                ? scan.Message
                : $"{completionMessage} {scan.Message}";
            SetStatus(status);

            if (scan.Monitors.Count == 0)
            {
                ShowMonitor(null);
            }
        }
        catch (Exception ex)
        {
            _activeRecordingTargets = [];
            MonitorSelector.ItemsSource = Array.Empty<MonitorView>();
            MonitorSelector.SelectedItem = null;
            ShowMonitor(null);
            SetStatus($"OpenBlade could not scan saved monitors: {ex.Message} Select Refresh to try again.");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void MonitorSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ShowMonitor(MonitorSelector.SelectedItem as MonitorView);
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private async void PatchButton_Click(object sender, RoutedEventArgs e)
    {
        await ApplyAsync(restore: false);
    }

    private async void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        await ApplyAsync(restore: true);
    }

    private async Task ApplyAsync(bool restore)
    {
        MonitorView? monitor = _selectedMonitor;
        if (_isBusy || _recording is not null || monitor is null || _previewMode)
        {
            return;
        }

        string action = restore ? "restore" : "patch";
        string warning = !restore && !string.IsNullOrWhiteSpace(monitor.ValidationWarning)
            ? monitor.ValidationWarning + "\n\n" : string.Empty;
        MessageBoxResult confirmation = MessageBox.Show(
            $"{warning}Keep the affected monitor unplugged until you have verified this change.\n\nDo you want to {action} this saved connection?\n\n{monitor.Name}\n{monitor.ConnectionLabel}, {monitor.InstanceLabel}",
            restore ? "Confirm restore" : "Confirm patch",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        string instanceId = monitor.InstanceId;
        SetBusy(true, restore ? "Restoring the backup…" : "Backing up and patching…");

        try
        {
            OperationOutcome outcome = await _backend.ApplyAsync(monitor, restore);
            SetBusy(false);
            await RefreshAsync(instanceId, outcome.Message);
        }
        catch (Exception ex)
        {
            SetBusy(false);
            await RefreshAsync(instanceId, $"OpenBlade could not finish the change: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OpenBackupButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _backend.BackupDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SetStatus($"OpenBlade could not open the backup folder: {ex.Message}");
        }
    }

    private void CopyDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(DiagnosticsText.Text);
            SetStatus("Copied the diagnostic details.");
        }
        catch (Exception ex)
        {
            SetStatus($"OpenBlade could not copy the diagnostic details: {ex.Message}");
        }
    }

    private async void RecordConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _recording is not null || _previewMode
            || RecordingTargetSelector.SelectedItem is not RecordingTargetChoice requested) return;
        bool explicitChoice = _recordingTargetWasExplicitlyChosen;
        // Refresh the read-only snapshot before starting: reconnecting can create
        // a different Windows instance while the old saved selection remains.
        await RefreshAsync();
        var choices = RecordingTargetSelector.ItemsSource as IReadOnlyList<RecordingTargetChoice> ?? [];
        var current = choices.FirstOrDefault(c => c.Monitor.InstanceId.Equals(requested.Monitor.InstanceId,
            StringComparison.OrdinalIgnoreCase));
        if (current is null || (requested.IsActive && !current.IsActive)
            || (!current.IsActive && !explicitChoice && choices.Any(c => c.IsActive)))
        {
            RecordingStatusText.Text = "Choose the connected HDMI monitor below, then start recording.";
            RecordingCard.BringIntoView();
            RecordingTargetSelector.Focus();
            return;
        }
        _settingRecordingTargets = true;
        RecordingTargetSelector.SelectedItem = current;
        _settingRecordingTargets = false;
        _recordingTargetWasExplicitlyChosen = explicitChoice;
        UpdateRecordingTargetHint();
        MonitorView selected = current.Monitor;
        ConnectionRecording recording;
        try
        {
            string context = FormatRecordingContext(RecordingModelText.Text, RecordingFirmwareText.Text, RecordingCableText.Text,
                (RecordingAdaptiveSyncSelector.SelectedItem as ComboBoxItem)?.Tag as string);
            recording = _createRecording(selected, _backend.DiagnosticText(selected) + context, _recordingRoot);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            RecordingStatusText.Text = "The recording could not start. Try Refresh, then record again.";
            return;
        }
        using var cancellation = new CancellationTokenSource();
        _recording = recording;
        _recordingCancellation = cancellation;
        _finishingRecording = false;
        _recordingReady = false;
        _lastRecordingDirectory = null;
        RecordingProgressBar.Value = 0;
        SetRecordingWarning(new RecordingProgress(TimeSpan.Zero, 0, 0).Warning);
        RecordingProgressBar.Visibility = Visibility.Visible;
        MarkBlackoutButton.Visibility = Visibility.Visible;
        StopRecordingButton.Visibility = Visibility.Visible;
        OpenRecordingButton.Visibility = Visibility.Collapsed;
        RecordingStatusText.Text = "Preparing the recording. Leave an already connected monitor connected.";
        UpdateActionStates();
        RecordConnectionButton.Visibility = Visibility.Collapsed;
        RecordingCard.BringIntoView();
        MarkBlackoutButton.Focus();
        var progress = new Progress<RecordingProgress>(value =>
        {
            if (!ReferenceEquals(_recording, recording) || _finishingRecording) return;
            _recordingReady = true;
            SetRecordingWarning(value.Warning);
            if (value.IsFinalizing)
            {
                _finishingRecording = true;
                RecordingStatusText.Text = "Saving the recording and collecting Windows logs…";
                MarkBlackoutButton.IsEnabled = false;
                StopRecordingButton.IsEnabled = false;
                return;
            }
            MarkBlackoutButton.IsEnabled = true;
            RecordingProgressBar.Value = Math.Min(60, value.Elapsed.TotalSeconds);
            int remaining = Math.Max(0, 60 - (int)value.Elapsed.TotalSeconds);
            RecordingStatusText.Text = remaining > 0
                ? $"Recording: {remaining} seconds left · {value.Samples} samples · {value.Blackouts} blackout marks."
                : "Saving the recording and collecting Windows logs…";
        });
        try
        {
            ConnectionRecordingResult result = await Task.Run(() => recording.RunAsync(cancellation.Token, progress));
            _lastRecordingDirectory = result.DirectoryPath;
            RecordingStatusText.Text = result.Message;
            SetRecordingWarning(result.Warning);
            OpenRecordingButton.Visibility = Directory.Exists(_lastRecordingDirectory) ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            _lastRecordingDirectory = recording.DirectoryPath;
            OpenRecordingButton.Visibility = Directory.Exists(_lastRecordingDirectory) ? Visibility.Visible : Visibility.Collapsed;
            RecordingStatusText.Text = "The recording could not finish. Any saved partial files remain in the recording folder.";
        }
        finally
        {
            _recording = null;
            _recordingCancellation = null;
            _finishingRecording = false;
            RecordConnectionButton.Visibility = Visibility.Visible;
            MarkBlackoutButton.Visibility = Visibility.Collapsed;
            StopRecordingButton.Visibility = Visibility.Collapsed;
            RecordingProgressBar.Visibility = Visibility.Collapsed;
            UpdateActionStates();
            if (_closeAfterRecording) Close();
        }
    }

    private void UpdateRecordingTargets()
    {
        var choices = RecordingTargets.Choices(_selectedMonitor, _activeRecordingTargets);
        _settingRecordingTargets = true;
        RecordingTargetSelector.ItemsSource = choices;
        RecordingTargetSelector.SelectedItem = RecordingTargets.Default(_selectedMonitor, choices);
        _settingRecordingTargets = false;
        _recordingTargetWasExplicitlyChosen = false;
        UpdateRecordingTargetHint();
    }

    private void RecordingTargetSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingRecordingTargets) return;
        _recordingTargetWasExplicitlyChosen = true;
        UpdateRecordingTargetHint();
        UpdateActionStates();
    }

    private void UpdateRecordingTargetHint()
    {
        RecordingTargetHintText.Text = RecordingTargetSelector.SelectedItem is null
            ? "No active connection matches the saved selection. Choose the connected HDMI monitor, or choose the saved connection to wait for it."
            : "Recording uses this connection only. Patch and Restore use the saved monitor above.";
    }

    private void SetRecordingWarning(string warning)
    {
        // Repeated samples keep the same warning; announce only a changed message.
        if (RecordingWarningText.Text == warning) return;
        RecordingWarningText.Text = warning;
        RecordingWarningText.Visibility = warning.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (warning.Length != 0)
        {
            AutomationPeer? peer = UIElementAutomationPeer.FromElement(RecordingWarningText)
                ?? UIElementAutomationPeer.CreatePeerForElement(RecordingWarningText);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    private void MarkBlackoutButton_Click(object sender, RoutedEventArgs e)
    {
        RecordingStatusText.Text = _recording?.MarkBlackout() == true
            ? "Blackout marked. Recording continues."
            : "The blackout mark was not saved. Recording has stopped or the mark limit was reached.";
    }

    private void StopRecordingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_recordingCancellation is null) return;
        _finishingRecording = true;
        _recordingCancellation.Cancel();
        MarkBlackoutButton.IsEnabled = false;
        StopRecordingButton.IsEnabled = false;
        RecordingStatusText.Text = "Stopping and saving the recording…";
    }

    private void OpenRecordingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastRecordingDirectory is null) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = _lastRecordingDirectory, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            RecordingStatusText.Text = "The recording folder could not be opened. Recordings are saved in Local AppData, under OpenBlade.HdmiFix\\Recordings.";
        }
    }

    internal static string FormatRecordingContext(string model, string firmware, string cable, string? adaptiveSync = null)
    {
        static string Value(string value, int limit)
        {
            string clean = new(value.Where(c => !char.IsControl(c)
                && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).ToArray());
            clean = clean.Trim();
            return clean.Length == 0 ? "not provided" : clean[..Math.Min(clean.Length, limit)];
        }
        return "\n\nUser-provided context (not detected):\nMonitor model: " + Value(model, 80)
            + "\nMonitor firmware: " + Value(firmware, 32)
            + "\nMonitor Adaptive Sync menu setting: " + (adaptiveSync is "On" or "Off" ? adaptiveSync : "Unknown")
            + "\nCable, adapter or dock: " + Value(cable, 160);
    }

    private IntPtr ObserveDisplayMessages(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_recording is null) return IntPtr.Zero;
        if (message == 0x007E) _recording.NotifyDisplayChange();
        else if (message == 0x0219 && wParam.ToInt64() is 0x0007 or 0x8000 or 0x8004)
            _recording.NotifyDeviceChange();
        return IntPtr.Zero;
    }

    private void ShowMonitor(MonitorView? monitor)
    {
        _selectedMonitor = monitor;

        if (monitor is null)
        {
            MonitorNameText.Text = "No monitor selected";
            MonitorStatusText.Text = "No saved monitors found.";
            ConnectionText.Text = "Unknown";
            InstanceLabelText.Text = "None";
            MonitorDetailsText.Text = "Select Refresh to scan again.";
        }
        else
        {
            MonitorNameText.Text = monitor.Name;
            MonitorStatusText.Text = monitor.Status;
            ConnectionText.Text = monitor.ConnectionLabel;
            InstanceLabelText.Text = monitor.InstanceLabel;
            MonitorDetailsText.Text = monitor.Details;
        }

        ActionHintText.Text = monitor?.ActionHint ?? string.Empty;
        ValidationWarningText.Text = monitor?.ValidationWarning ?? string.Empty;
        ValidationWarningBorder.Visibility = string.IsNullOrWhiteSpace(ValidationWarningText.Text)
            ? Visibility.Collapsed : Visibility.Visible;
        DiagnosticsText.Text = _backend.DiagnosticText(monitor);
        UpdateRecordingTargets();
        UpdateActionStates();
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _isBusy = busy;
        BusyIndicator.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        MonitorSelector.IsEnabled = !busy;
        if (!string.IsNullOrWhiteSpace(status))
        {
            SetStatus(status);
        }

        UpdateStatusVisibility();
        UpdateActionStates();
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
        UpdateStatusVisibility();
    }

    private void UpdateStatusVisibility()
    {
        StatusCard.Visibility = _isBusy || !string.IsNullOrWhiteSpace(StatusText.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateActionStates()
    {
        bool recording = _recording is not null;
        bool idle = !_isBusy && !recording;
        bool canMutate = idle && !_previewMode && _selectedMonitor is not null;
        var primary = (Style)FindResource("PrimaryButtonStyle");
        var secondary = (Style)FindResource(typeof(Button));
        bool restorePreferred = _selectedMonitor?.CanRestore == true;
        PatchButton.Style = restorePreferred ? secondary : primary;
        RestoreButton.Style = restorePreferred ? primary : secondary;
        MonitorSelector.IsEnabled = idle;
        RecordingContextExpander.IsEnabled = idle;
        RecordingTargetSelector.IsEnabled = idle;
        RecordingTargetHintText.Visibility = recording ? Visibility.Collapsed : Visibility.Visible;
        RecordConnectionButton.IsEnabled = idle && !_previewMode && RecordingTargetSelector.SelectedItem is RecordingTargetChoice;
        MarkBlackoutButton.IsEnabled = recording && _recordingReady && !_finishingRecording;
        StopRecordingButton.IsEnabled = recording && !_finishingRecording;
        PatchButton.IsEnabled = canMutate && _selectedMonitor!.CanPatch;
        RestoreButton.IsEnabled = canMutate && _selectedMonitor!.CanRestore;
        RefreshButton.IsEnabled = idle;
        OpenBackupButton.IsEnabled = idle && Directory.Exists(_backend.BackupDirectory);
        CopyDiagnosticsButton.IsEnabled = !_isBusy && !string.IsNullOrWhiteSpace(DiagnosticsText.Text);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, uint attribute, ref int value, uint size);

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_recording is not null)
        {
            e.Cancel = true;
            _closeAfterRecording = true;
            StopRecordingButton_Click(this, new RoutedEventArgs());
            return;
        }
        if (!_isBusy) return;
        e.Cancel = true;
        SetStatus("OpenBlade is still working. Wait for it to finish before closing the app.");
    }
}
