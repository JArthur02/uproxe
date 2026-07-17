using System.Text.Json;
using System.Text.Json.Serialization;
using UProxy.Core.Models;

namespace UProxy.Core.Persistence;

/// <summary>
/// Versioned JSON persistence for <see cref="ProxyChainProfile"/> under <c>chains/{safeName}.json</c>.
/// Credentials are stored separately via <see cref="ProtectedCredentialStore"/> keyed by hop id.
/// </summary>
public sealed class ChainProfileStore
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly AppDataLayout _layout;
    private readonly ProtectedCredentialStore _credentials;

    public ChainProfileStore(AppDataLayout layout, ProtectedCredentialStore credentials)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    }

    public IReadOnlyList<string> ListNames()
    {
        _layout.EnsureDirectories();
        if (!Directory.Exists(_layout.ChainsDirectory))
            return [];

        return Directory.EnumerateFiles(_layout.ChainsDirectory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n))
            .Cast<string>()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public ProxyChainProfile? Load(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var path = ProfilePath(name);
        if (!File.Exists(path))
            return null;

        var dto = JsonSerializer.Deserialize<ChainProfileDto>(File.ReadAllText(path), JsonOptions)
                  ?? throw new InvalidDataException($"Failed to parse chain profile '{name}'.");

        var hops = new List<ProxyHop>(dto.Hops?.Count ?? 0);
        if (dto.Hops is not null)
        {
            foreach (var h in dto.Hops)
            {
                var hopId = h.Id == Guid.Empty ? Guid.NewGuid() : h.Id;
                var key = ProtectedCredentialStore.CredentialKeyForHop(hopId);
                var cred = _credentials.Get(key);
                var protocol = PersistenceProxyMapping.ProtocolFromKind(h.Kind);
                var proxy = new ParsedProxy(
                    h.Host,
                    h.Port,
                    protocol,
                    cred?.Username,
                    cred?.Password);
                hops.Add(new ProxyHop(
                    hopId,
                    proxy,
                    h.Kind,
                    h.Transport,
                    h.Capabilities,
                    h.RemoteDns));
            }
        }

        return new ProxyChainProfile(
            dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id,
            string.IsNullOrWhiteSpace(dto.Name) ? name : dto.Name,
            dto.Mode,
            hops,
            dto.CandidatePoolId);
    }

    public void Save(ProxyChainProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.Name);

        _layout.EnsureDirectories();
        var path = ProfilePath(profile.Name);

        // Drop credentials for hops that disappeared from this profile file (same stem).
        var previousIds = TryReadHopIds(path);
        var currentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hop in profile.Hops)
            currentIds.Add(ProtectedCredentialStore.CredentialKeyForHop(hop.Id));

        if (previousIds.Count > 0)
        {
            var orphaned = previousIds.Where(id => !currentIds.Contains(id)).ToList();
            if (orphaned.Count > 0)
                _credentials.DeleteMany(orphaned);
        }

        foreach (var hop in profile.Hops)
        {
            var key = ProtectedCredentialStore.CredentialKeyForHop(hop.Id);
            var user = hop.Proxy.Username;
            var pass = hop.Proxy.Password;
            if (string.IsNullOrEmpty(user) && string.IsNullOrEmpty(pass))
                _credentials.Delete(key);
            else
                _credentials.Set(key, user, pass);
        }

        var dto = new ChainProfileDto
        {
            Version = CurrentVersion,
            Id = profile.Id,
            Name = profile.Name,
            Mode = profile.Mode,
            CandidatePoolId = profile.CandidatePoolId,
            Hops = profile.Hops.Select(h => new HopDto
            {
                Id = h.Id,
                Host = h.Proxy.Host,
                Port = h.Proxy.Port,
                Kind = h.Kind,
                Transport = h.Transport,
                Capabilities = h.Capabilities,
                RemoteDns = h.RemoteDns
                // Username/Password intentionally omitted
            }).ToList()
        };

        AppDataLayout.AtomicWriteAllText(path, JsonSerializer.Serialize(dto, JsonOptions));
    }

    public bool Delete(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var path = ProfilePath(name);
        if (!File.Exists(path))
            return false;

        var hopIds = TryReadHopIds(path);
        File.Delete(path);
        if (hopIds.Count > 0)
            _credentials.DeleteMany(hopIds);
        return true;
    }

    public string ProfilePath(string name) =>
        Path.Combine(_layout.ChainsDirectory, AppDataLayout.SafeFileStem(name) + ".json");

    private HashSet<string> TryReadHopIds(string path)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(path))
            return ids;

        try
        {
            var dto = JsonSerializer.Deserialize<ChainProfileDto>(File.ReadAllText(path), JsonOptions);
            if (dto?.Hops is null)
                return ids;
            foreach (var h in dto.Hops)
            {
                if (h.Id != Guid.Empty)
                    ids.Add(ProtectedCredentialStore.CredentialKeyForHop(h.Id));
            }
        }
        catch
        {
            // Corrupt previous file — skip orphan cleanup.
        }

        return ids;
    }

    private sealed class ChainProfileDto
    {
        public int Version { get; set; } = CurrentVersion;
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public ChainMode Mode { get; set; }
        public string? CandidatePoolId { get; set; }
        public List<HopDto>? Hops { get; set; }
    }

    private sealed class HopDto
    {
        public Guid Id { get; set; }
        public string Host { get; set; } = "";
        public int Port { get; set; }
        public ProxyKind Kind { get; set; }
        public ProxyTransport Transport { get; set; } = ProxyTransport.Tcp;
        public ProxyCapabilities Capabilities { get; set; }
        public bool RemoteDns { get; set; } = true;
    }
}
