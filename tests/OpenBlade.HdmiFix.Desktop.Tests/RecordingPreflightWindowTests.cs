using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Markup;
using System.Xml.Linq;
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
        string phase = "Starting STA dispatcher";
        void SetPhase(string value) => Volatile.Write(ref phase, value);
        var thread = new Thread(() =>
        {
            Exception? failure = null;
            Application? app = null;
            MainWindow? window = null;
            string root = Path.Combine(Path.GetTempPath(), "OpenBlade-preflight-window-" + Guid.NewGuid().ToString("N"));
            try
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                SetPhase("Initializing WPF application");
                app = new Application { Resources = LoadProductionResources() };
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                dispatcher.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        SetPhase("Constructing WPF window");
                        var backend = new ControlledBackend(root);
                        int recorderCalls = 0;
                        window = new MainWindow(backend, false, root, (_, _, _) =>
                        {
                            recorderCalls++;
                            throw new InvalidOperationException("A blocked preflight must not create a recorder.");
                        });
                        // Exercise the real Loaded handler without creating an HWND or
                        // depending on desktop composition in a headless CI session.
                        SetPhase("Waiting for initial Loaded scan");
                        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, window));
                        await window.InitialScanCompleted;
                        SetPhase("Checking initial control state");

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

                        SetPhase("Starting failed recording preflight");
                        record.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert.Equal(2, backend.Scans);
                        await CompleteScanAsync(targetSelector, () => backend.Preflight.SetException(new IOException("Synthetic saved scan failure")), stage => SetPhase("Failed preflight: " + stage));
                        SetPhase("Checking failed preflight control state");
                        Assert.Equal(0, recorderCalls);
                        Assert.Equal(ControlledBackend.SavedId, Assert.IsType<MonitorView>(savedSelector.SelectedItem).InstanceId);
                        Assert.False(patch.IsEnabled);
                        Assert.False(restore.IsEnabled);
                        Assert.Contains("saved monitor information", recordingStatus.Text, StringComparison.OrdinalIgnoreCase);
                        Assert.Contains("Refresh", recordingStatus.Text, StringComparison.Ordinal);

                        SetPhase("Starting recovery refresh");
                        Control<Button>(window, "RefreshButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert.Equal(3, backend.Scans);
                        await CompleteScanAsync(targetSelector, () => backend.Recovery.SetResult(ControlledBackend.RecoveredScan()), stage => SetPhase("Recovery refresh: " + stage));
                        SetPhase("Checking recovered saved-wait control state");
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
                        SetPhase("Closing WPF window and application");
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
                    SetPhase("Removing temporary recording directory");
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
        catch (TimeoutException error)
        {
            throw new TimeoutException($"WPF preflight regression timed out during: {Volatile.Read(ref phase)}.", error);
        }
        finally
        {
            if (!completed.Task.IsCompleted && dispatcher is not null)
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        }
    }

    private static ResourceDictionary LoadProductionResources()
    {
        // Link the production resource source without constructing App: its
        // queued OnStartup would open a second window or an elevation dialog.
        using Stream source = typeof(RecordingPreflightWindowTests).Assembly
            .GetManifestResourceStream("ProductionApp.xaml")!;
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XElement resources = XDocument.Load(source).Root!.Element(ns + "Application.Resources")!;
        var dictionary = new XElement(ns + "ResourceDictionary",
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            resources.Elements());
        return (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
    }
    private static T Control<T>(Window window, string name) where T : FrameworkElement =>
        Assert.IsType<T>(window.FindName(name));

    private static async Task CompleteScanAsync(ComboBox selector, Action complete, Action<string> reportPhase)
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
            reportPhase("waiting for selector re-enable");
            complete();
            await enabled.Task;
            reportPhase("waiting for click handler completion at dispatcher idle");
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
