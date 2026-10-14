using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace OpenBlade.HdmiFix.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        int? elevatedResult = ElevatedRunner.TryRun(e.Args);
        if (elevatedResult is int exitCode)
        {
            Shutdown(exitCode);
            return;
        }

        if (TryReadPreviewArguments(
            e.Args,
            out string? outputPath,
            out double previewWidth,
            out bool expandedPreview,
            out bool recordingPreview,
            out string? previewError))
        {
            if (previewError is not null)
            {
                MessageBox.Show(previewError, "OpenBlade HDMI Fix", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(2);
                return;
            }

            RunPreview(outputPath!, previewWidth, expandedPreview, recordingPreview);
            return;
        }

        if (IsRunningAsAdministrator())
        {
            MessageBox.Show(
                "Start OpenBlade HDMI Fix normally. The app asks for administrator permission when a patch or restore needs it.",
                "Start normally",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }

        MainWindow = new MainWindow(new PatcherBackend());
        MainWindow.Show();
    }

    private void RunPreview(string outputPath, double previewWidth, bool expandedPreview, bool recordingPreview)
    {
        var window = new MainWindow(new PreviewBackend(), previewMode: true)
        {
            Width = previewWidth,
            Height = 760,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };

        MainWindow = window;
        window.Loaded += async (_, _) =>
        {
            try
            {
                await window.InitialScanCompleted;
                if (recordingPreview) window.PrepareRecordingPreview();
                if (expandedPreview)
                {
                    window.PrepareExpandedDiagnosticsPreview();
                }

                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                SaveWindowPng(window, outputPath);
                Shutdown(0);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"OpenBlade could not save the preview.\n\n{ex.Message}",
                    "Preview failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(3);
            }
        };
        window.Show();
    }

    private static void SaveWindowPng(Window window, string outputPath)
    {
        string? directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        window.UpdateLayout();
        DpiScale dpi = VisualTreeHelper.GetDpi(window);
        int width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX));
        int height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(outputPath);
        encoder.Save(stream);
    }

    private static bool TryReadPreviewArguments(
        string[] args,
        out string? outputPath,
        out double previewWidth,
        out bool expandedPreview,
        out bool recordingPreview,
        out string? error)
    {
        outputPath = null;
        previewWidth = 720;
        expandedPreview = false;
        recordingPreview = false;
        error = null;

        if (args.Length == 0 || !string.Equals(args[0], "--preview", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (args.Length is < 2 or > 5 || !Path.IsPathFullyQualified(args[1]))
        {
            error = "Preview usage: --preview <absolute output.png> [width] [--expanded] [--recording]";
            return true;
        }

        if (!string.Equals(Path.GetExtension(args[1]), ".png", StringComparison.OrdinalIgnoreCase))
        {
            error = "The preview output path must end in .png.";
            return true;
        }

        int nextArgument = 2;
        if (nextArgument < args.Length &&
            !args[nextArgument].StartsWith("--", StringComparison.Ordinal))
        {
            if (!double.TryParse(args[nextArgument], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out previewWidth) ||
                previewWidth < 560 || previewWidth > 1600)
            {
                error = "Preview width must be a number from 560 to 1600.";
                return true;
            }

            nextArgument++;
        }

        for (; nextArgument < args.Length; nextArgument++)
        {
            if (string.Equals(args[nextArgument], "--expanded", StringComparison.OrdinalIgnoreCase) && !expandedPreview)
                expandedPreview = true;
            else if (string.Equals(args[nextArgument], "--recording", StringComparison.OrdinalIgnoreCase) && !recordingPreview)
                recordingPreview = true;
            else
            {
                error = "Preview usage: --preview <absolute output.png> [width] [--expanded] [--recording]";
                return true;
            }
        }

        outputPath = Path.GetFullPath(args[1]);
        return true;
    }

    private static bool IsRunningAsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
