using System.Security.Cryptography;
using UProxy.Core.Security;

namespace UProxy.Core.Tests;

public class SecretScannerTests
{
    [Fact]
    public void ParseFindings_ExtractsDetectorLocationAndRedactsSecret()
    {
        // Synthetic TruffleHog JSON line (Raw is a harmless placeholder, not a real credential).
        const string line =
            """
            {"SourceMetadata":{"Data":{"Filesystem":{"file":"proxies.txt","line":7}}},"DetectorType":8,"DetectorName":"Github","DecoderName":"PLAIN","Verified":false,"Raw":"ABCD1234EFGH5678IJKL","Redacted":""}
            """;

        var findings = SecretScanner.ParseFindings(line);

        var f = Assert.Single(findings);
        Assert.Equal("Github", f.Detector);
        Assert.False(f.Verified);
        Assert.Equal("proxies.txt", f.Location);
        Assert.Equal(7, f.Line);
        Assert.Equal("PLAIN", f.Decoder);
        Assert.Equal("ABCD…IJKL", f.RedactedSecret);
    }

    [Fact]
    public void ParseFindings_IgnoresLogLinesAndBlanks()
    {
        var stdout =
            "{\"level\":\"info\",\"msg\":\"running source\"}\n" +
            "\n" +
            "not json at all\n" +
            "{\"DetectorName\":\"AWS\",\"Verified\":true,\"Raw\":\"AKIAEXAMPLEEXAMPLE12\",\"SourceMetadata\":{\"Data\":{\"Filesystem\":{\"file\":\"a.txt\",\"line\":1}}}}";

        var findings = SecretScanner.ParseFindings(stdout);

        var f = Assert.Single(findings);
        Assert.Equal("AWS", f.Detector);
        Assert.True(f.Verified);
    }

    [Theory]
    [InlineData("", "(hidden)")]
    [InlineData("short", "•••••")]
    [InlineData("abcdefghij", "abcd…ghij")]
    public void Redact_MasksSecrets(string input, string expected) =>
        Assert.Equal(expected, SecretScanner.Redact(input));

    [Fact]
    public async Task ScanFilesystem_DetectsHighEntropyToken_EndToEnd()
    {
        var scanner = new SecretScanner();
        if (await scanner.GetVersionAsync() is null)
            return; // TruffleHog not installed on this machine — skip the live scan.

        // Build a detectable-but-fake token at runtime so no real-looking secret is committed.
        var token = "ghp_" + RandomAlphanumeric(36);
        var dir = Path.Combine(Path.GetTempPath(), "uproxy-th-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "creds.txt");
        await File.WriteAllTextAsync(file, $"host=example\napi_token={token}\n");

        try
        {
            var result = await scanner.ScanFilesystemAsync(dir);

            Assert.True(result.Ran, result.Error);
            Assert.Contains(result.Findings, f => f.Detector == "Github");
            var github = result.Findings.First(f => f.Detector == "Github");
            Assert.StartsWith("ghp_", github.RedactedSecret);
            Assert.Contains("…", github.RedactedSecret);
            Assert.NotEqual(token, github.RedactedSecret); // never surfaces the full secret
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    private static string RandomAlphanumeric(int length)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return new string(chars);
    }
}
