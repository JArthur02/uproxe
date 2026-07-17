using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;

namespace UProxy.Core.Windows;

/// <summary>
/// WinINET per-user system proxy helper. Windows-only; no-ops / throws on other OS.
/// Captures exact prior settings before changes and supports crash-recovery restore.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProxyManager
{
    private const string InternetSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    private readonly string _backupPath;

    public WindowsProxyManager(string? backupPath = null)
    {
        _backupPath = backupPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "uProxyTool",
            "wininet-proxy-backup.json");
    }

    public bool IsSupported => OperatingSystem.IsWindows();

    public bool HasPendingRestore => File.Exists(_backupPath);

    public WinInetProxySnapshot Capture()
    {
        EnsureWindows();
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: false)
                        ?? throw new InvalidOperationException("Cannot open Internet Settings registry key.");

        return new WinInetProxySnapshot
        {
            ProxyEnable = Convert.ToInt32(key.GetValue("ProxyEnable", 0)),
            ProxyServer = Convert.ToString(key.GetValue("ProxyServer", "")) ?? "",
            ProxyOverride = Convert.ToString(key.GetValue("ProxyOverride", "")) ?? "",
            AutoConfigURL = Convert.ToString(key.GetValue("AutoConfigURL", "")) ?? "",
            CapturedAtUtc = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Opt-in: saves current settings to disk (only if no pending restore backup exists),
    /// clears AutoConfigURL so PAC cannot override the gateway, then applies the proxy.
    /// </summary>
    public void SetProxyOptIn(string proxyServer)
    {
        EnsureWindows();
        if (string.IsNullOrWhiteSpace(proxyServer))
            throw new ArgumentException("Proxy server is required.", nameof(proxyServer));

        // Never overwrite an existing crash-recovery backup — that would lose the user's
        // original WinINET configuration if we re-enable while a prior backup is pending.
        if (!HasPendingRestore)
        {
            var snapshot = Capture();
            SaveBackup(snapshot);
        }

        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: true)
                        ?? throw new InvalidOperationException("Cannot open Internet Settings registry key.");

        key.SetValue("ProxyServer", proxyServer);
        key.SetValue("ProxyEnable", 1);
        // PAC/AutoConfigURL can override ProxyServer; clear it while the gateway is active.
        // The pending backup retains the original AutoConfigURL for Restore.
        try { key.DeleteValue("AutoConfigURL", throwOnMissingValue: false); }
        catch { /* ignore */ }
        NotifyWinInet();
    }

    /// <summary>Point WinINET HTTP/HTTPS at the local uProxe HTTP gateway. Starts backup first.</summary>
    public void SetLocalGateway(int httpPort, string host = "127.0.0.1")
    {
        if (httpPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(httpPort));
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("Host is required.", nameof(host));

        // Classic WinINET dual form so HTTP and HTTPS both use the local gateway.
        SetProxyOptIn($"http={host}:{httpPort};https={host}:{httpPort}");
    }

    /// <summary>Restores the exact captured snapshot (from memory or backup file).</summary>
    public void Restore(WinInetProxySnapshot? snapshot = null)
    {
        EnsureWindows();
        snapshot ??= LoadBackup() ?? throw new InvalidOperationException("No proxy backup available to restore.");

        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: true)
                        ?? throw new InvalidOperationException("Cannot open Internet Settings registry key.");

        key.SetValue("ProxyEnable", snapshot.ProxyEnable);
        key.SetValue("ProxyServer", snapshot.ProxyServer ?? "");
        key.SetValue("ProxyOverride", snapshot.ProxyOverride ?? "");
        if (!string.IsNullOrEmpty(snapshot.AutoConfigURL))
            key.SetValue("AutoConfigURL", snapshot.AutoConfigURL);
        else
        {
            try { key.DeleteValue("AutoConfigURL", throwOnMissingValue: false); }
            catch { /* ignore */ }
        }

        NotifyWinInet();
        ClearBackup();
    }

    /// <summary>Emergency reset used when a previous run left a temporary proxy active.</summary>
    public bool TryEmergencyRestore()
    {
        if (!IsSupported || !HasPendingRestore)
            return false;
        Restore();
        return true;
    }

    public void SaveBackup(WinInetProxySnapshot snapshot)
    {
        var dir = Path.GetDirectoryName(_backupPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = _backupPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _backupPath, overwrite: true);
    }

    public WinInetProxySnapshot? LoadBackup()
    {
        if (!File.Exists(_backupPath))
            return null;
        try
        {
            return JsonSerializer.Deserialize<WinInetProxySnapshot>(File.ReadAllText(_backupPath));
        }
        catch
        {
            return null;
        }
    }

    public void ClearBackup()
    {
        try
        {
            if (File.Exists(_backupPath))
                File.Delete(_backupPath);
        }
        catch
        {
            // ignore
        }
    }

    private static void NotifyWinInet()
    {
        InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("WinINET system proxy is Windows-only.");
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
}

public sealed class WinInetProxySnapshot
{
    public int ProxyEnable { get; set; }
    public string ProxyServer { get; set; } = "";
    public string ProxyOverride { get; set; } = "";
    public string AutoConfigURL { get; set; } = "";
    public DateTimeOffset CapturedAtUtc { get; set; }
}
