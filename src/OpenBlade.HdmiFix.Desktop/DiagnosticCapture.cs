namespace OpenBlade.HdmiFix.Desktop;

internal sealed record DiagnosticSnapshot(HostDiagnosticSnapshot Host, LiveDisplayScan Live, DateTimeOffset? Captured);

internal static class DiagnosticCapture
{
    internal static DiagnosticSnapshot Read(Func<HostDiagnosticSnapshot> readHost, Func<LiveDisplayScan> readLive)
    {
        HostDiagnosticSnapshot host;
        LiveDisplayScan live;
        try { host = readHost(); }
        catch (Exception e) when (IsRecoverable(e))
        {
            host = new("unknown", "unknown", "unknown", [], "capture unavailable");
        }
        try { live = readLive(); }
        catch (Exception e) when (IsRecoverable(e))
        {
            live = new([], "LiveCapture:Unavailable");
        }
        return new(host, live, DateTimeOffset.UtcNow);
    }

    // Optional observations must not block saved-monitor discovery. Fatal
    // memory or native-corruption errors cannot be reconciled by this fallback.
    private static bool IsRecoverable(Exception error) => error is not
        (OutOfMemoryException or StackOverflowException or AccessViolationException);
}
