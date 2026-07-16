namespace UProxy.Core.Config;

/// <summary>
/// Selectable User-Agent presets. Some judges/targets reject non-browser agents,
/// so a browser-like preset can raise pass-rates (cf. Proxifier's "Appear as Internet Explorer").
/// </summary>
public static class UserAgents
{
    public const string Default =
        "μProxy-Tool/2.0 (+https://github.com; privacy-respecting proxy checker)";

    public const string Chrome =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    public const string Firefox =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:127.0) Gecko/20100101 Firefox/127.0";

    public const string InternetExplorer =
        "Mozilla/5.0 (Windows NT 10.0; WOW64; Trident/7.0; rv:11.0) like Gecko";

    /// <summary>Named presets for the settings UI. The value is the raw User-Agent header.</summary>
    public static IReadOnlyList<(string Name, string Value)> Presets { get; } =
    [
        ("Default (μProxy)", Default),
        ("Chrome (Windows)", Chrome),
        ("Firefox (Windows)", Firefox),
        ("Internet Explorer 11", InternetExplorer)
    ];
}
