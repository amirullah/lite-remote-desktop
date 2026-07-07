namespace RemoteDesktop.Shared;

/// <summary>Central place for on-disk locations so host and client never disagree.</summary>
public static class AppPaths
{
    public const string ProductName = "LiteRemote";

    private static string Root =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductName);

    public static string HostCertificate => Path.Combine(Root, "host.pfx");
    public static string HostConfig => Path.Combine(Root, "host.json");
    public static string ClientConfig => Path.Combine(Root, "client.json");
    public static string PinStore => Path.Combine(Root, "pins.json");
    public static string LogDirectory => Path.Combine(Root, "logs");

    public static void EnsureRoot() => Directory.CreateDirectory(Root);
}
