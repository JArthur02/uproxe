using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UProxy.Core.Persistence;

/// <summary>Username/password pair stored under a stable record id (typically hop.Id "N").</summary>
public sealed record StoredCredential(string? Username, string? Password);

/// <summary>
/// Versioned encrypted credential blob at <c>credentials/protected.dat</c>.
/// Windows: DPAPI CurrentUser. Non-Windows (tests/CI): AES with a machine-local key file.
/// </summary>
public sealed class ProtectedCredentialStore
{
    /// <summary>Windows DPAPI payload.</summary>
    public const byte FormatDpapi = 1;

    /// <summary>Dev AES-CBC (non-Windows / CI).</summary>
    public const byte FormatDevAes = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly AppDataLayout _layout;
    private readonly object _gate = new();
    private Dictionary<string, StoredCredential>? _cache;

    public ProtectedCredentialStore(AppDataLayout layout)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
    }

    public static string CredentialKeyForHop(Guid hopId) => hopId.ToString("N");

    public StoredCredential? Get(string recordId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        lock (_gate)
        {
            EnsureLoaded();
            return _cache!.TryGetValue(recordId, out var c) ? c : null;
        }
    }

    public void Set(string recordId, StoredCredential credential)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        ArgumentNullException.ThrowIfNull(credential);
        lock (_gate)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(credential.Username) && string.IsNullOrEmpty(credential.Password))
            {
                _cache!.Remove(recordId);
            }
            else
            {
                _cache![recordId] = credential;
            }

            PersistUnlocked();
        }
    }

    public void Set(string recordId, string? username, string? password) =>
        Set(recordId, new StoredCredential(username, password));

    public bool Delete(string recordId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        lock (_gate)
        {
            EnsureLoaded();
            if (!_cache!.Remove(recordId))
                return false;
            PersistUnlocked();
            return true;
        }
    }

    public int DeleteMany(IEnumerable<string> recordIds)
    {
        ArgumentNullException.ThrowIfNull(recordIds);
        lock (_gate)
        {
            EnsureLoaded();
            var removed = 0;
            foreach (var id in recordIds)
            {
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                if (_cache!.Remove(id))
                    removed++;
            }

            if (removed > 0)
                PersistUnlocked();
            return removed;
        }
    }

    public IReadOnlyCollection<string> ListIds()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _cache!.Keys.ToList();
        }
    }

    private void EnsureLoaded()
    {
        if (_cache is not null)
            return;

        _cache = new Dictionary<string, StoredCredential>(StringComparer.Ordinal);
        var path = _layout.ProtectedCredentialsPath;
        if (!File.Exists(path))
            return;

        var blob = File.ReadAllBytes(path);
        if (blob.Length < 1)
            return;

        var format = blob[0];
        var payload = blob.AsSpan(1);
        byte[] plain = format switch
        {
            FormatDpapi => UnprotectDpapi(payload),
            FormatDevAes => UnprotectDevAes(payload),
            _ => throw new InvalidDataException($"Unknown credential store format byte 0x{format:X2}.")
        };

        var dto = JsonSerializer.Deserialize<CredentialBlobDto>(plain, JsonOptions);
        if (dto?.Records is null)
            return;

        foreach (var (id, rec) in dto.Records)
        {
            if (string.IsNullOrWhiteSpace(id) || rec is null)
                continue;
            _cache[id] = new StoredCredential(rec.Username, rec.Password);
        }
    }

    private void PersistUnlocked()
    {
        _layout.EnsureDirectories();
        var dto = new CredentialBlobDto
        {
            Version = 1,
            Records = _cache!.ToDictionary(
                kv => kv.Key,
                kv => new CredentialRecordDto
                {
                    Username = kv.Value.Username,
                    Password = kv.Value.Password
                },
                StringComparer.Ordinal)
        };

        var plain = JsonSerializer.SerializeToUtf8Bytes(dto, JsonOptions);
        byte format;
        byte[] cipher;
        if (OperatingSystem.IsWindows())
        {
            format = FormatDpapi;
            cipher = ProtectDpapi(plain);
        }
        else
        {
            format = FormatDevAes;
            cipher = ProtectDevAes(plain);
        }

        var blob = new byte[1 + cipher.Length];
        blob[0] = format;
        Buffer.BlockCopy(cipher, 0, blob, 1, cipher.Length);
        AppDataLayout.AtomicWriteAllBytes(_layout.ProtectedCredentialsPath, blob);
    }

    private static byte[] ProtectDpapi(byte[] plain)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI is Windows-only.");
        return ProtectedData.Protect(plain, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
    }

    private static byte[] UnprotectDpapi(ReadOnlySpan<byte> cipher)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI is Windows-only.");
        return ProtectedData.Unprotect(cipher.ToArray(), optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
    }

    private byte[] ProtectDevAes(byte[] plain)
    {
        var key = LoadOrCreateDevKey();
        var iv = RandomNumberGenerator.GetBytes(16);
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var encryptor = aes.CreateEncryptor();
        var cipher = encryptor.TransformFinalBlock(plain, 0, plain.Length);
        var result = new byte[iv.Length + cipher.Length];
        Buffer.BlockCopy(iv, 0, result, 0, iv.Length);
        Buffer.BlockCopy(cipher, 0, result, iv.Length, cipher.Length);
        return result;
    }

    private byte[] UnprotectDevAes(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 17)
            throw new InvalidDataException("Dev AES credential blob is truncated.");

        var key = LoadOrCreateDevKey();
        var iv = payload[..16].ToArray();
        var cipher = payload[16..].ToArray();
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
    }

    private byte[] LoadOrCreateDevKey()
    {
        _layout.EnsureDirectories();
        var keyPath = Path.Combine(_layout.CredentialsDirectory, ".devkey");
        if (File.Exists(keyPath))
        {
            var existing = File.ReadAllBytes(keyPath);
            if (existing.Length == 32)
                return existing;
        }

        var key = RandomNumberGenerator.GetBytes(32);
        // Key file is intentionally local-only for Linux CI / non-Windows tests.
        AppDataLayout.AtomicWriteAllBytes(keyPath, key);
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Best-effort; ignore on filesystems without Unix modes.
        }

        return key;
    }

    private sealed class CredentialBlobDto
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, CredentialRecordDto>? Records { get; set; }
    }

    private sealed class CredentialRecordDto
    {
        public string? Username { get; set; }
        public string? Password { get; set; }
    }
}
