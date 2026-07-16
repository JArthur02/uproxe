using MaxMind.GeoIP2;
using MaxMind.GeoIP2.Exceptions;

namespace UProxy.Core.GeoIp;

public interface IGeoIpResolver : IDisposable
{
    string LookupCountry(string hostOrIp);
}

public sealed class NullGeoIpResolver : IGeoIpResolver
{
    public static NullGeoIpResolver Instance { get; } = new();
    public string LookupCountry(string hostOrIp) => "Unknown";
    public void Dispose() { }
}

public sealed class MaxMindGeoIpResolver : IGeoIpResolver
{
    private readonly DatabaseReader? _reader;
    private readonly object _gate = new();

    public MaxMindGeoIpResolver(string databasePath)
    {
        if (File.Exists(databasePath))
            _reader = new DatabaseReader(databasePath);
    }

    public string LookupCountry(string hostOrIp)
    {
        if (_reader is null)
            return "Unknown";

        try
        {
            // Host should already be an IP for proxies we parse; skip DNS to avoid leaks/delays.
            if (!System.Net.IPAddress.TryParse(hostOrIp.Trim().TrimStart('[').TrimEnd(']'), out _))
                return "Unknown";

            lock (_gate)
            {
                var response = _reader.Country(hostOrIp);
                var name = response.Country.Name;
                return string.IsNullOrWhiteSpace(name) ? "Unknown" : name;
            }
        }
        catch (AddressNotFoundException)
        {
            return "Unknown";
        }
        catch (GeoIP2Exception)
        {
            return "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    public void Dispose() => _reader?.Dispose();
}
