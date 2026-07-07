using System.IO;
using System.Text.Json;
using RemoteDesktop.Shared;
using RemoteDesktop.Shared.Models;

namespace RemoteDesktop.Client.Services;

/// <summary>Client-side preferences and saved connections. Stored in %LocalAppData%\LiteRemote\client.json.</summary>
public sealed class ClientConfig
{
    public List<SavedConnection> Recent { get; set; } = new();
    public SessionSettings DefaultSettings { get; set; } = new();
    public string? GoogleClientId { get; set; }

    /// <summary>Relay server ("host:port") used for ID-based connections. Shared with the host side.</summary>
    public string RelayAddress { get; set; } = "";

    public void Remember(SavedConnection conn)
    {
        Recent.RemoveAll(c => c.Host == conn.Host && c.Port == conn.Port);
        Recent.Insert(0, conn);
        if (Recent.Count > 10) Recent.RemoveRange(10, Recent.Count - 10);
        Save();
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static ClientConfig Load()
    {
        AppPaths.EnsureRoot();
        try
        {
            if (File.Exists(AppPaths.ClientConfig))
                return JsonSerializer.Deserialize<ClientConfig>(File.ReadAllText(AppPaths.ClientConfig)) ?? new();
        }
        catch { }
        return new ClientConfig();
    }

    public void Save()
    {
        AppPaths.EnsureRoot();
        File.WriteAllText(AppPaths.ClientConfig, JsonSerializer.Serialize(this, Json));
    }
}

public sealed record SavedConnection
{
    public string Host { get; init; } = "";
    public int Port { get; init; } = 7443;
    public string? VpnProfile { get; init; }   // path to an .ovpn profile, null = direct internet
    public string Label { get; init; } = "";
}
