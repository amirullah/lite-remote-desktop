using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace RemoteDesktop.Client.Services;

/// <summary>
/// Tracks every OpenVPN process the app spawns so none can outlive the app as an invisible background
/// tunnel. Each VPN service registers its process on start and unregisters on a clean dispose; whatever
/// is still registered when the app exits gets force-killed (see App.OnExit). This guarantees a closed
/// window / quit app never leaves a live VPN the user isn't aware of.
/// </summary>
public static class VpnProcessTracker
{
    private static readonly HashSet<Process> Active = new();

    public static void Register(Process p) { lock (Active) Active.Add(p); }

    public static void Unregister(Process p) { lock (Active) Active.Remove(p); }

    /// <summary>Force-kill every still-running tracked VPN process (call on app exit).</summary>
    public static void KillAll()
    {
        Process[] snapshot;
        lock (Active) { snapshot = Active.ToArray(); Active.Clear(); }
        foreach (var p in snapshot)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        }
    }
}
