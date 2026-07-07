using System.Text.Json;

namespace RemoteDesktop.Shared.Security;

/// <summary>
/// Client-side trust-on-first-use store: maps "host:port" to the pinned public-key fingerprint.
/// On first connect the fingerprint is shown to the user for out-of-band verification and saved;
/// on later connects a mismatch aborts the handshake (possible MITM).
/// </summary>
public sealed class PinStore
{
    private readonly string _path;
    private readonly Dictionary<string, string> _pins;

    public PinStore(string path)
    {
        _path = path;
        _pins = Load(path);
    }

    public string? GetPin(string endpoint) => _pins.GetValueOrDefault(endpoint);

    public bool IsPinned(string endpoint) => _pins.ContainsKey(endpoint);

    public void Pin(string endpoint, string fingerprint)
    {
        _pins[endpoint] = fingerprint;
        Save();
    }

    /// <summary>Returns true if the endpoint is unknown (first use) or the fingerprint matches the pin.</summary>
    public PinCheck Check(string endpoint, string fingerprint)
    {
        if (!_pins.TryGetValue(endpoint, out var known)) return PinCheck.FirstUse;
        return string.Equals(known, fingerprint, StringComparison.OrdinalIgnoreCase)
            ? PinCheck.Match
            : PinCheck.Mismatch;
    }

    private static Dictionary<string, string> Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                       ?? new();
        }
        catch { }
        return new();
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_pins, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public enum PinCheck { FirstUse, Match, Mismatch }
