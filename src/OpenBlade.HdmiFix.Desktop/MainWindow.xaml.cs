using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace OpenBlade.HdmiFix.Desktop;

public partial class MainWindow : Window
{
    private readonly IPatcherBackend _backend;
    private readonly bool _previewMode;
    private readonly TaskCompletionSource _initialScanCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _isBusy;
    private MonitorView? _selectedMonitor;

    public MainWindow(IPatcherBackend backend, bool previewMode = false)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _backend = backend;
        _previewMode = previewMode;
        InitializeComponent();
        PreviewBanner.Visibility = previewMode ? Visibility.Visible : Visibility.Collapsed;
        if (previewMode)
        {
            PatchButton.ToolTip = "Patch is disabled because this window uses preview data.";
            RestoreButton.ToolTip = "Restore is disabled because this window uses preview data.";
        }

        UpdateActionStates();
    }

    public Task InitialScanCompleted => _initialScanCompletion.Task;

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
            await RefreshAsync();
        }
        finally
        {
            _initialScanCompletion.TrySetResult();
        }
    }

    private async Task RefreshAsync(string? preferredInstanceId = null, string? completionMessage = null)
    {
        if (_isBusy)
        {
            return;
        }

        preferredInstanceId ??= _selectedMonitor?.InstanceId;
        SetBusy(true, "Scanning saved monitors…");

        try
        {
            MonitorScan scan = await _backend.ScanAsync();
            MonitorSelector.ItemsSource = scan.Monitors;

            MonitorView? selection = scan.Monitors.FirstOrDefault(
                monitor => string.Equals(monitor.InstanceId, preferredInstanceId, StringComparison.OrdinalIgnoreCase));
            MonitorSelector.SelectedItem = selection ?? scan.Monitors.FirstOrDefault();

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
        if (_isBusy || monitor is null || _previewMode)
        {
            return;
        }

        string action = restore ? "restore" : "patch";
        MessageBoxResult confirmation = MessageBox.Show(
            $"Keep the affected monitor unplugged until you have verified this change.\n\nDo you want to {action} this saved connection?\n\n{monitor.Name}\n{monitor.ConnectionLabel}, {monitor.InstanceLabel}",
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

        DiagnosticsText.Text = _backend.DiagnosticText(monitor);
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
        bool canMutate = !_isBusy && !_previewMode && _selectedMonitor is not null;
        PatchButton.IsEnabled = canMutate && _selectedMonitor!.CanPatch;
        RestoreButton.IsEnabled = canMutate && _selectedMonitor!.CanRestore;
        RefreshButton.IsEnabled = !_isBusy;
        OpenBackupButton.IsEnabled = !_isBusy && Directory.Exists(_backend.BackupDirectory);
        CopyDiagnosticsButton.IsEnabled = !_isBusy && !string.IsNullOrWhiteSpace(DiagnosticsText.Text);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_isBusy)
        {
            return;
        }

        e.Cancel = true;
        SetStatus("OpenBlade is still working. Wait for it to finish before closing the app.");
    }
}
