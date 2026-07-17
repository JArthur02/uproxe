using System.Text.Json;
using System.Text.Json.Serialization;
using UProxy.Core.Models;

namespace UProxy.Core.Persistence;

/// <summary>
/// Named pool persistence under <c>pools/{safeName}.json</c>.
/// No plaintext passwords in the JSON; hop credentials use <see cref="ProtectedCredentialStore"/>.
/// </summary>
public sealed class PoolStore
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

    public PoolStore(AppDataLayout layout, ProtectedCredentialStore credentials)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    }

    public IReadOnlyList<string> ListNames()
    {
        _layout.EnsureDirectories();
        if (!Directory.Exists(_layout.PoolsDirectory))
            return [];

        return Directory.EnumerateFiles(_layout.PoolsDirectory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n))
            .Cast<string>()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<PoolCandidate>? Load(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var path = PoolPath(name);
        if (!File.Exists(path))
            return null;

        var dto = JsonSerializer.Deserialize<PoolDto>(File.ReadAllText(path), JsonOptions)
                  ?? throw new InvalidDataException($"Failed to parse pool '{name}'.");

        var list = new List<PoolCandidate>(dto.Candidates?.Count ?? 0);
        if (dto.Candidates is null)
            return list;

        foreach (var c in dto.Candidates)
        {
            if (c.Hop is null || string.IsNullOrWhiteSpace(c.Hop.Host))
                continue;

            var hopId = c.Hop.Id == Guid.Empty ? Guid.NewGuid() : c.Hop.Id;
            var key = ProtectedCredentialStore.CredentialKeyForHop(hopId);
            var cred = _credentials.Get(key);
            var protocol = PersistenceProxyMapping.ProtocolFromKind(c.Hop.Kind);
            var proxy = new ParsedProxy(
                c.Hop.Host,
                c.Hop.Port,
                protocol,
                cred?.Username,
                cred?.Password);
            var hop = new ProxyHop(
                hopId,
                proxy,
                c.Hop.Kind,
                c.Hop.Transport,
                c.Hop.Capabilities,
                c.Hop.RemoteDns);
            list.Add(new PoolCandidate(hop, c.Country, c.LatencyMs, c.SuccessRate, c.LastChecked));
        }

        return list;
    }

    public void Save(string name, IReadOnlyList<PoolCandidate> candidates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(candidates);

        _layout.EnsureDirectories();
        var path = PoolPath(name);

        var previousIds = TryReadHopIds(path);
        var currentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in candidates)
            currentIds.Add(ProtectedCredentialStore.CredentialKeyForHop(c.Hop.Id));

        if (previousIds.Count > 0)
        {
            var orphaned = previousIds.Where(id => !currentIds.Contains(id)).ToList();
            if (orphaned.Count > 0)
                _credentials.DeleteMany(orphaned);
        }

        foreach (var c in candidates)
        {
            var key = ProtectedCredentialStore.CredentialKeyForHop(c.Hop.Id);
            var user = c.Hop.Proxy.Username;
            var pass = c.Hop.Proxy.Password;
            if (string.IsNullOrEmpty(user) && string.IsNullOrEmpty(pass))
                _credentials.Delete(key);
            else
                _credentials.Set(key, user, pass);
        }

        var dto = new PoolDto
        {
            Version = CurrentVersion,
            Name = name,
            Candidates = candidates.Select(c => new CandidateDto
            {
                Hop = new HopDto
                {
                    Id = c.Hop.Id,
                    Host = c.Hop.Proxy.Host,
                    Port = c.Hop.Proxy.Port,
                    Kind = c.Hop.Kind,
                    Transport = c.Hop.Transport,
                    Capabilities = c.Hop.Capabilities,
                    RemoteDns = c.Hop.RemoteDns
                },
                Country = c.Country,
                LatencyMs = c.LatencyMs,
                SuccessRate = c.SuccessRate,
                LastChecked = c.LastChecked
            }).ToList()
        };

        AppDataLayout.AtomicWriteAllText(path, JsonSerializer.Serialize(dto, JsonOptions));
    }

    public bool Delete(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var path = PoolPath(name);
        if (!File.Exists(path))
            return false;

        var hopIds = TryReadHopIds(path);
        File.Delete(path);
        if (hopIds.Count > 0)
            _credentials.DeleteMany(hopIds);
        return true;
    }

    public string PoolPath(string name) =>
        Path.Combine(_layout.PoolsDirectory, AppDataLayout.SafeFileStem(name) + ".json");

    private HashSet<string> TryReadHopIds(string path)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(path))
            return ids;

        try
        {
            var dto = JsonSerializer.Deserialize<PoolDto>(File.ReadAllText(path), JsonOptions);
            if (dto?.Candidates is null)
                return ids;
            foreach (var c in dto.Candidates)
            {
                if (c.Hop is { Id: var id } && id != Guid.Empty)
                    ids.Add(ProtectedCredentialStore.CredentialKeyForHop(id));
            }
        }
        catch
        {
            // Corrupt previous file — skip orphan cleanup.
        }

        return ids;
    }

    private sealed class PoolDto
    {
        public int Version { get; set; } = CurrentVersion;
        public string Name { get; set; } = "";
        public List<CandidateDto>? Candidates { get; set; }
    }

    private sealed class CandidateDto
    {
        public HopDto? Hop { get; set; }
        public string? Country { get; set; }
        public int? LatencyMs { get; set; }
        public double? SuccessRate { get; set; }
        public DateTimeOffset? LastChecked { get; set; }
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
