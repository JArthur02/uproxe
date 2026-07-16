using UProxy.Core.Config;
using UProxy.Core.GeoIp;

namespace UProxy.Core.Tests;

public class AppDataPathsTests
{
    [Fact]
    public void ResolveExistingOrDefault_FallsBackWhenConfiguredAbsolutePathIsMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "uproxy-path-" + Guid.NewGuid().ToString("N"));
        var dataDir = Path.Combine(root, "Data");
        Directory.CreateDirectory(dataDir);
        var realDb = Path.Combine(dataDir, "Country.mmdb");
        File.WriteAllText(realDb, "placeholder");

        try
        {
            var stale = Path.Combine(Path.GetTempPath(), "gone-install", "Data", "Country.mmdb");
            var resolved = AppDataPaths.ResolveExistingOrDefault(
                stale, Path.Combine("Data", "Country.mmdb"), root);

            Assert.True(File.Exists(resolved));
            Assert.Equal(Path.GetFullPath(realDb), Path.GetFullPath(resolved));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void ToPortable_StoresRelativePathUnderAppDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "uproxy-port-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var full = Path.Combine(root, "Data", "Country.mmdb");
        var portable = AppDataPaths.ToPortable(full, root);
        Assert.Equal(Path.Combine("Data", "Country.mmdb"), portable);
    }
}

public class GeoIpLookupTests
{
    private static string? FindMmdb()
    {
        var candidates = new[]
        {
            Path.Combine("/workspace", "src", "UProxy.UI", "Data", "Country.mmdb"),
            Path.Combine(AppContext.BaseDirectory, "Data", "Country.mmdb"),
            Path.Combine("/workspace", "Country.mmdb")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    [Fact]
    public void LookupCountry_ResolvesAttachedSocksListIps()
    {
        var mmdb = FindMmdb();
        if (mmdb is null)
            return; // no DB in this environment

        var list = "/home/ubuntu/.cursor/projects/workspace/uploads/new_socks5_935e.txt";
        if (!File.Exists(list))
            return;

        using var geo = new MaxMindGeoIpResolver(mmdb);
        var ips = File.ReadAllLines(list)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Trim().Split(':')[0])
            .ToList();

        Assert.NotEmpty(ips);
        var unknown = 0;
        foreach (var ip in ips)
        {
            var country = geo.LookupCountry(ip);
            if (country == "Unknown")
                unknown++;
        }

        // The attached SOCKS sample resolves for every IP when the MMDB is present.
        Assert.Equal(0, unknown);
        Assert.Equal("Turkey", geo.LookupCountry("139.28.240.200"));
        Assert.Equal("United States", geo.LookupCountry("45.38.170.123"));
    }
}
