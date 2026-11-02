using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Xunit;

namespace OpenBlade.HdmiFix.Desktop.Tests;

[Collection("Connection recording")]
public sealed class RecordingPreflightWindowTests
{
    [Fact]
    public async Task SavedWaitAfterSuccessfulRefreshClearsFailedPreflightStatus()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher? dispatcher = null;
        var thread = new Thread(() =>
        {
            Exception? failure = null;
            App? app = null;
            MainWindow? window = null;
            string root = Path.Combine(Path.GetTempPath(), "OpenBlade-preflight-window-" + Guid.NewGuid().ToString("N"));
            try
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                app = new App();
                app.InitializeComponent();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                dispatcher.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        var backend = new ControlledBackend(root);
                        int recorderCalls = 0;
                        window = new MainWindow(backend, false, root, (_, _, _) =>
                        {
                            recorderCalls++;
                            throw new InvalidOperationException("A blocked preflight must not create a recorder.");
                        })
                        {
                            ShowActivated = false,
                            ShowInTaskbar = false,
                            Opacity = 0,
                        };
                        window.Show();
                        await window.InitialScanCompleted;

                        var savedSelector = Control<ComboBox>(window, "MonitorSelector");
                        var targetSelector = Control<ComboBox>(window, "RecordingTargetSelector");
                        var record = Control<Button>(window, "RecordConnectionButton");
                        var patch = Control<Button>(window, "PatchButton");
                        var restore = Control<Button>(window, "RestoreButton");
                        var recordingStatus = Control<TextBlock>(window, "RecordingStatusText");
                        Assert.Equal(ControlledBackend.SavedId, Assert.IsType<MonitorView>(savedSelector.SelectedItem).InstanceId);
                        Assert.True(patch.IsEnabled);
                        Assert.True(restore.IsEnabled);
                        targetSelector.SelectedItem = targetSelector.Items.Cast<RecordingTargetChoice>().Single(c => c.IsActive);
                        Assert.True(record.IsEnabled);

                        record.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert.Equal(2, backend.Scans);
                        await CompleteScanAsync(targetSelector, () => backend.Preflight.SetException(new IOException("Synthetic saved scan failure")));
                        Assert.Equal(0, recorderCalls);
                        Assert.Equal(ControlledBackend.SavedId, Assert.IsType<MonitorView>(savedSelector.SelectedItem).InstanceId);
                        Assert.False(patch.IsEnabled);
                        Assert.False(restore.IsEnabled);
                        Assert.Contains("saved monitor information", recordingStatus.Text, StringComparison.OrdinalIgnoreCase);
                        Assert.Contains("Refresh", recordingStatus.Text, StringComparison.Ordinal);

                        Control<Button>(window, "RefreshButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert.Equal(3, backend.Scans);
                        await CompleteScanAsync(targetSelector, () => backend.Recovery.SetResult(ControlledBackend.RecoveredScan()));
                        var choice = Assert.IsType<RecordingTargetChoice>(targetSelector.SelectedItem);
                        Assert.False(choice.IsActive);
                        Assert.Equal(ControlledBackend.SavedId, choice.Monitor.InstanceId);
                        Assert.Single(targetSelector.Items.Cast<RecordingTargetChoice>());
                        Assert.True(record.IsEnabled);
                        Assert.True(patch.IsEnabled);
                        Assert.True(restore.IsEnabled);
                        Assert.Equal(ControlledBackend.SavedId, Assert.IsType<MonitorView>(savedSelector.SelectedItem).InstanceId);
                        Assert.Equal("Ready to record and wait for readable data from this saved connection.", recordingStatus.Text);
                        Assert.DoesNotContain("couldn't", recordingStatus.Text, StringComparison.OrdinalIgnoreCase);
                        Assert.Equal(0, recorderCalls);
                        Assert.Equal(0, backend.Mutations);
                    }
                    catch (Exception error) { failure = error; }
                    finally
                    {
                        window?.Close();
                        app.Shutdown();
                        Dispatcher.CurrentDispatcher.InvokeShutdown();
                    }
                }));
                Dispatcher.Run();
            }
            catch (Exception error) { failure = error; }
            finally
            {
                try
                {
                    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                }
                catch (Exception error) { failure ??= error; }
                if (failure is null) completed.TrySetResult();
                else completed.TrySetException(failure);
            }
        }) { IsBackground = true, Name = "Recording preflight WPF regression" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { await completed.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
        finally
        {
            if (!completed.Task.IsCompleted && dispatcher is not null)
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        }
    }

    private static T Control<T>(Window window, string name) where T : FrameworkElement =>
        Assert.IsType<T>(window.FindName(name));

    private static async Task CompleteScanAsync(ComboBox selector, Action complete)
    {
        Assert.False(selector.IsEnabled);
        var enabled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var descriptor = DependencyPropertyDescriptor.FromProperty(UIElement.IsEnabledProperty, typeof(ComboBox))!;
        EventHandler changed = (_, _) =>
        {
            if (selector.IsEnabled) enabled.TrySetResult();
        };
        descriptor.AddValueChanged(selector, changed);
        try
        {
            complete();
            await enabled.Task;
            // SetBusy ends before the async click handler updates its result text.
            await selector.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        }
        finally { descriptor.RemoveValueChanged(selector, changed); }
    }

    private sealed class ControlledBackend(string root) : IPatcherBackend
    {
        internal const string SavedId = @"DISPLAY\SAM74AF\5&saved&0&UID100";
        private const string LiveId = @"DISPLAY\SAM74AF\5&other&0&UID202";
        private static readonly MonitorView Saved = new(SavedId, "Synthetic saved monitor", "Previously connected",
            "Synthetic saved connection", "Synthetic", "", true, true, null);
        internal TaskCompletionSource<MonitorScan> Preflight { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<MonitorScan> Recovery { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Scans { get; private set; }
        internal int Mutations { get; private set; }
        public string BackupDirectory => Path.Combine(root, "unused-backups");

        public Task<MonitorScan> ScanAsync() => ++Scans switch
        {
            1 => Task.FromResult(InitialScan()),
            2 => Preflight.Task,
            3 => Recovery.Task,
            _ => throw new InvalidOperationException("Unexpected extra scan."),
        };

        private static MonitorScan InitialScan()
        {
            var live = new LiveDisplayScan([new LiveDisplayDiagnostics(LiveId, "Synthetic live monitor", "240 Hz",
                "HDMI", "inactive", "No", "No", "No", "No")]);
            return new([Saved], "Synthetic saved information refreshed.", RecordingTargets.FromSnapshot([Saved], live),
                RecordingTargets.ReadFailure(live));
        }

        internal static MonitorScan RecoveredScan()
        {
            var live = new LiveDisplayScan([], "", true);
            return new([Saved], "Synthetic saved information refreshed.", RecordingTargets.FromSnapshot([Saved], live),
                RecordingTargets.ReadFailure(live));
        }

        public Task<OperationOutcome> ApplyAsync(MonitorView monitor, bool restore)
        {
            Mutations++;
            throw new InvalidOperationException("The GUI regression must never mutate a monitor.");
        }

        public string DiagnosticText(MonitorView? monitor) => "Synthetic diagnostics; no hardware queried.";
    }
}
