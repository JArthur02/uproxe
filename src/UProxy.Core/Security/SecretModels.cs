namespace UProxy.Core.Security;

/// <summary>A single secret detected by TruffleHog, normalized for display/export.</summary>
public sealed record SecretFinding
{
    /// <summary>Detector that fired, e.g. "Github", "AWS", "SlackWebhook".</summary>
    public required string Detector { get; init; }

    /// <summary>True when TruffleHog live-verified the credential against its API.</summary>
    public bool Verified { get; init; }

    /// <summary>Redacted preview of the secret (never the full value).</summary>
    public required string RedactedSecret { get; init; }

    /// <summary>Source file/URL the secret was found in, when available.</summary>
    public string? Location { get; init; }

    /// <summary>1-based line number within the source, when available.</summary>
    public int? Line { get; init; }

    /// <summary>Decoder used (PLAIN, BASE64, …).</summary>
    public string? Decoder { get; init; }
}

/// <summary>Outcome of a TruffleHog scan.</summary>
public sealed record SecretScanResult
{
    public required bool Ran { get; init; }
    public IReadOnlyList<SecretFinding> Findings { get; init; } = [];
    public int VerifiedCount => Findings.Count(f => f.Verified);
    public string? ScannedTarget { get; init; }
    public int DurationMs { get; init; }

    /// <summary>Populated when the scanner could not run (e.g. binary missing).</summary>
    public string? Error { get; init; }
}
