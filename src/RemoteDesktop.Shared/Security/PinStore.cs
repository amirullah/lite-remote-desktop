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
    private readonly bool _corrupt;

    public PinStore(string path)
    {
        _path = path;
        (_pins, _corrupt) = Load(path);
    }

    /// <summary>True if a pin file existed but could not be parsed — the store is in fail-closed mode.</summary>
    public bool IsCorrupt => _corrupt;

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
        // A present-but-unreadable store must NOT collapse to "first use" for every host — that would
        // silently drop MITM protection. Force the loud "changed" path until the user re-verifies and
        // Save() rewrites a clean file. (audit M-A0: AUD-005)
        if (_corrupt) return PinCheck.Mismatch;
        if (!_pins.TryGetValue(endpoint, out var known)) return PinCheck.FirstUse;
        return string.Equals(known, fingerprint, StringComparison.OrdinalIgnoreCase)
            ? PinCheck.Match
            : PinCheck.Mismatch;
    }

    private static (Dictionary<string, string> pins, bool corrupt) Load(string path)
    {
        if (!File.Exists(path)) return (new(), false); // genuinely first use — no pins yet

        try
        {
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return (new(), false); // empty file, treat as no pins
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(text);
            return dict is null ? (new(), true) : (dict, false);
        }
        catch
        {
            // Preserve the unreadable file for inspection, then fail closed.
            try { File.Copy(path, path + ".corrupt", overwrite: true); } catch { }
            return (new(), true);
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_pins, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public enum PinCheck { FirstUse, Match, Mismatch }
