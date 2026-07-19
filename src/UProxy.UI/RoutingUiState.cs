namespace UProxy.UI;

/// <summary>
/// Shared routing presentation state for the main window and Proxy Chains dialog.
/// Verified means a proxied exit IP was observed and differed from the direct IP.
/// </summary>
public sealed class RoutingUiState
{
    public enum Phase
    {
        Off = 0,
        Starting,
        TunnelUp,
        Verified,
        NotVerified
    }

    public Phase Status { get; private set; } = Phase.Off;
    public string? ExitIp { get; private set; }
    public string? DirectIp { get; private set; }
    public string? Reason { get; private set; }
    public DateTimeOffset? CheckedAtUtc { get; private set; }
    public string? ProfileName { get; private set; }
    public string? ModeLabel { get; private set; }
    public bool AutomaticWindowsRouting { get; private set; }
    public int HttpPort { get; private set; }
    public int SocksPort { get; private set; }

    public event Action? Changed;

    public string Summary => Status switch
    {
        Phase.Off => "Routing: Off",
        Phase.Starting => "Routing: Starting…",
        Phase.TunnelUp => "Routing: Tunnel connected (exit not verified yet)",
        Phase.Verified =>
            $"Routing: Verified — exit {ExitIp}" +
            (CheckedAtUtc is { } t ? $", checked {t.ToLocalTime():HH:mm:ss}" : ""),
        Phase.NotVerified =>
            $"Routing: NOT VERIFIED" +
            (string.IsNullOrWhiteSpace(Reason) ? "" : $" — {Reason}"),
        _ => "Routing: Off"
    };

    public void SetOff()
    {
        Status = Phase.Off;
        ExitIp = null;
        DirectIp = null;
        Reason = null;
        CheckedAtUtc = null;
        ProfileName = null;
        ModeLabel = null;
        AutomaticWindowsRouting = false;
        Raise();
    }

    public void SetStarting(string? profileName, string? modeLabel)
    {
        Status = Phase.Starting;
        ExitIp = null;
        DirectIp = null;
        Reason = null;
        CheckedAtUtc = null;
        ProfileName = profileName;
        ModeLabel = modeLabel;
        Raise();
    }

    public void SetTunnelUp(
        string? profileName,
        string? modeLabel,
        bool automaticWindowsRouting,
        int httpPort,
        int socksPort)
    {
        Status = Phase.TunnelUp;
        ProfileName = profileName;
        ModeLabel = modeLabel;
        AutomaticWindowsRouting = automaticWindowsRouting;
        HttpPort = httpPort;
        SocksPort = socksPort;
        Reason = null;
        Raise();
    }

    public void SetVerified(
        string exitIp,
        string directIp,
        string? profileName,
        string? modeLabel,
        bool automaticWindowsRouting,
        int httpPort,
        int socksPort)
    {
        Status = Phase.Verified;
        ExitIp = exitIp;
        DirectIp = directIp;
        Reason = null;
        CheckedAtUtc = DateTimeOffset.UtcNow;
        ProfileName = profileName;
        ModeLabel = modeLabel;
        AutomaticWindowsRouting = automaticWindowsRouting;
        HttpPort = httpPort;
        SocksPort = socksPort;
        Raise();
    }

    public void SetNotVerified(
        string reason,
        string? exitIp,
        string? directIp,
        string? profileName,
        string? modeLabel,
        bool automaticWindowsRouting,
        int httpPort,
        int socksPort)
    {
        Status = Phase.NotVerified;
        Reason = reason;
        ExitIp = exitIp;
        DirectIp = directIp;
        CheckedAtUtc = DateTimeOffset.UtcNow;
        ProfileName = profileName;
        ModeLabel = modeLabel;
        AutomaticWindowsRouting = automaticWindowsRouting;
        HttpPort = httpPort;
        SocksPort = socksPort;
        Raise();
    }

    private void Raise() => Changed?.Invoke();
}
