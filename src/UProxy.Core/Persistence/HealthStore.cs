using System.Text.Json;
using System.Text.Json.Serialization;
using UProxy.Core.Chaining;
using UProxy.Core.Models;

namespace UProxy.Core.Persistence;

/// <summary>
/// Persists <see cref="ChainHealthTracker"/> export/import to <c>health/proxy-health.json</c>.
/// </summary>
public sealed class HealthStore
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly AppDataLayout _layout;

    public HealthStore(AppDataLayout layout)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
    }

    public void Save(ChainHealthTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        SaveStates(tracker.ExportStates());
    }

    public void SaveStates(IReadOnlyList<ProxyHealthState> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        _layout.EnsureDirectories();
        var dto = new HealthFileDto
        {
            Version = CurrentVersion,
            States = states.ToList()
        };
        AppDataLayout.AtomicWriteAllText(
            _layout.ProxyHealthPath,
            JsonSerializer.Serialize(dto, JsonOptions));
    }

    public IReadOnlyList<ProxyHealthState> LoadStates()
    {
        var path = _layout.ProxyHealthPath;
        if (!File.Exists(path))
            return [];

        var dto = JsonSerializer.Deserialize<HealthFileDto>(File.ReadAllText(path), JsonOptions);
        return dto?.States ?? [];
    }

    public void LoadInto(ChainHealthTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        var states = LoadStates();
        if (states.Count > 0)
            tracker.ImportStates(states);
    }

    private sealed class HealthFileDto
    {
        public int Version { get; set; } = CurrentVersion;
        public List<ProxyHealthState>? States { get; set; }
    }
}
