using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace UProxy.Core.Security;

public sealed class SecretScannerOptions
{
    /// <summary>Path to, or name of, the TruffleHog executable (must be on PATH if a bare name).</summary>
    public string ExecutablePath { get; set; } = "trufflehog";

    /// <summary>
    /// Verify findings against provider APIs. Off by default: verification sends candidate
    /// secrets to third-party endpoints, which conflicts with this tool's privacy stance.
    /// </summary>
    public bool Verify { get; set; }

    /// <summary>Only surface verified secrets (implies <see cref="Verify"/>).</summary>
    public bool OnlyVerified { get; set; }

    /// <summary>Hard cap on a single scan.</summary>
    public int TimeoutMs { get; set; } = 120_000;
}

/// <summary>
/// Thin wrapper around the TruffleHog CLI (https://github.com/trufflesecurity/trufflehog).
/// Scans proxy lists, pasted blobs, and export folders for leaked credentials before you
/// share them. TruffleHog is invoked as an external process with JSON output.
/// </summary>
public sealed class SecretScanner
{
    private readonly SecretScannerOptions _options;

    public SecretScanner(SecretScannerOptions? options = null) => _options = options ?? new SecretScannerOptions();

    /// <summary>Returns the TruffleHog version string, or null if the binary cannot be launched.</summary>
    public async Task<string?> GetVersionAsync(CancellationToken ct = default)
    {
        try
        {
            var (exit, stdout, stderr) = await RunAsync(["--version"], 10_000, ct).ConfigureAwait(false);
            // TruffleHog prints its version banner to stderr.
            var text = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
            var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            return exit == 0 || !string.IsNullOrEmpty(line) ? line : null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default) =>
        await GetVersionAsync(ct).ConfigureAwait(false) is not null;

    /// <summary>Scans a file or directory for secrets.</summary>
    public Task<SecretScanResult> ScanFilesystemAsync(string path, CancellationToken ct = default) =>
        ScanAsync(["filesystem", path], path, ct);

    /// <summary>Writes <paramref name="text"/> to a temp file and scans it (for pasted/scraped content).</summary>
    public async Task<SecretScanResult> ScanTextAsync(string text, string? label = null, CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "uproxy-secretscan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, SanitizeLabel(label) + ".txt");
        try
        {
            await File.WriteAllTextAsync(file, text, ct).ConfigureAwait(false);
            var result = await ScanAsync(["filesystem", dir], label ?? "(pasted text)", ct).ConfigureAwait(false);
            return result;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private async Task<SecretScanResult> ScanAsync(IReadOnlyList<string> baseArgs, string target, CancellationToken ct)
    {
        var args = new List<string>(baseArgs) { "--json", "--no-update", "--concurrency=4" };
        if (_options.OnlyVerified)
            args.Add("--only-verified");
        else if (!_options.Verify)
            args.Add("--no-verification");

        var sw = Stopwatch.StartNew();
        int exit;
        string stdout, stderr;
        try
        {
            (exit, stdout, stderr) = await RunAsync(args, _options.TimeoutMs, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SecretScanResult
            {
                Ran = false,
                ScannedTarget = target,
                DurationMs = (int)sw.ElapsedMilliseconds,
                Error = $"Could not launch TruffleHog ('{_options.ExecutablePath}'). Install it and/or set the path. {ex.Message}"
            };
        }
        sw.Stop();

        var findings = ParseFindings(stdout);

        // Exit code 0 = clean run (no --fail flag is passed). A non-zero code with no findings
        // and no stdout usually means a launch/usage error worth surfacing.
        if (exit != 0 && findings.Count == 0 && string.IsNullOrWhiteSpace(stdout))
        {
            return new SecretScanResult
            {
                Ran = false,
                ScannedTarget = target,
                DurationMs = (int)sw.ElapsedMilliseconds,
                Error = $"TruffleHog exited with code {exit}. {FirstErrorLine(stderr)}".Trim()
            };
        }

        return new SecretScanResult
        {
            Ran = true,
            Findings = findings,
            ScannedTarget = target,
            DurationMs = (int)sw.ElapsedMilliseconds
        };
    }

    internal static IReadOnlyList<SecretFinding> ParseFindings(string stdout)
    {
        var findings = new List<SecretFinding>();
        if (string.IsNullOrWhiteSpace(stdout))
            return findings;

        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] != '{')
                continue;

            SecretFinding? finding = TryParseLine(line);
            if (finding is not null)
                findings.Add(finding);
        }
        return findings;
    }

    private static SecretFinding? TryParseLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            // Only treat objects that look like findings (have a DetectorName) as findings.
            if (!root.TryGetProperty("DetectorName", out var detectorEl) ||
                detectorEl.ValueKind != JsonValueKind.String)
                return null;

            var detector = detectorEl.GetString() ?? "Unknown";
            var verified = root.TryGetProperty("Verified", out var v) && v.ValueKind == JsonValueKind.True;
            var decoder = root.TryGetProperty("DecoderName", out var d) ? d.GetString() : null;

            var rawSecret = root.TryGetProperty("Raw", out var r) ? r.GetString() : null;
            var redacted = root.TryGetProperty("Redacted", out var red) ? red.GetString() : null;
            var preview = !string.IsNullOrEmpty(redacted) ? redacted! : Redact(rawSecret);

            var (location, lineNo) = ExtractLocation(root);

            return new SecretFinding
            {
                Detector = detector,
                Verified = verified,
                RedactedSecret = preview,
                Location = location,
                Line = lineNo,
                Decoder = decoder
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (string? Location, int? Line) ExtractLocation(JsonElement root)
    {
        if (!root.TryGetProperty("SourceMetadata", out var meta) ||
            !meta.TryGetProperty("Data", out var data) ||
            data.ValueKind != JsonValueKind.Object)
            return (null, null);

        // Data has one nested object keyed by source type (Filesystem, Git, Github, …).
        foreach (var prop in data.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Object)
                continue;

            string? location = null;
            int? line = null;
            foreach (var field in prop.Value.EnumerateObject())
            {
                switch (field.Name.ToLowerInvariant())
                {
                    case "file" or "link" or "repository" or "email":
                        location ??= field.Value.ValueKind == JsonValueKind.String ? field.Value.GetString() : null;
                        break;
                    case "line":
                        if (field.Value.ValueKind == JsonValueKind.Number && field.Value.TryGetInt32(out var n) && n > 0)
                            line = n;
                        break;
                }
            }
            return (location, line);
        }
        return (null, null);
    }

    internal static string Redact(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
            return "(hidden)";
        secret = secret.Trim();
        if (secret.Length <= 8)
            return new string('•', secret.Length);
        return $"{secret[..4]}…{secret[^4..]}";
    }

    private static string SanitizeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return "content";
        var cleaned = new string(label.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        return cleaned.Length == 0 ? "content" : cleaned[..Math.Min(cleaned.Length, 40)];
    }

    private static string FirstErrorLine(string stderr)
    {
        var line = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase));
        return line?.Trim() ?? "";
    }

    private async Task<(int Exit, string Stdout, string Stderr)> RunAsync(
        IReadOnlyList<string> args, int timeoutMs, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _options.ExecutablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            if (ct.IsCancellationRequested)
                throw;
            // timeout: return what we have
        }

        return (process.HasExited ? process.ExitCode : -1, stdout.ToString(), stderr.ToString());
    }
}
