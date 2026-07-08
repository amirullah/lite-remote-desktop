using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RemoteDesktop.Client.Rendering;

/// <summary>
/// Hosts the Windows Remote Desktop ActiveX control (mstscax.dll) so a real RDP session renders inside
/// our own window (no external mstsc). We drive it mostly through IDispatch late-binding (<c>dynamic</c>)
/// — no COM interop assembly / tlbimp — plus a couple of hand-declared dispatch interfaces for the
/// pieces late-binding can't reach: per-monitor DPI (<see cref="IMsRdpExtendedSettings"/>) and dynamic
/// resolution (<see cref="IMsRdpClient9"/>). RDP authenticates with the Windows account and can drive the
/// login/lock screen, which LiteRemote's own protocol cannot.
/// </summary>
internal sealed class RdpHost : AxHost
{
    // "Microsoft RDP Client Control - version 10" — present on Windows 10 1607+ and Windows 11.
    private const string ClsidV10 = "8B918B82-7985-4C24-89DF-C33AD2BBFBCD";

    public RdpHost() : base(PickClsid()) { }

    // Resolve the newest RDP control actually registered on THIS machine via its ProgID, so we don't
    // hardcode a GUID that may be absent on a down-level Windows image. Type.GetTypeFromProgID returns
    // null (never throws) when a ProgID isn't registered. Falls back to the known-good v10 GUID.
    private static string PickClsid()
    {
        foreach (var progId in new[]
        {
            "MsRdpClient12NotSafeForScripting", "MsRdpClient11NotSafeForScripting",
            "MsRdpClient10NotSafeForScripting", "MsRdpClient9NotSafeForScripting",
            "MsRdpClient8NotSafeForScripting",  "MsRdpClient7NotSafeForScripting",
        })
        {
            var t = Type.GetTypeFromProgID(progId);
            if (t != null) return t.GUID.ToString();
        }
        return ClsidV10;
    }

    /// <summary>The underlying ActiveX object, driven via late binding (Server, UserName, Connect, …).</summary>
    public dynamic Ocx => GetOcx();

    /// <summary>Per-monitor DPI / scale settings (QueryInterface off the OCX — not on the default dispatch).</summary>
    public IMsRdpExtendedSettings Extended => (IMsRdpExtendedSettings)GetOcx();

    /// <summary>Dynamic-resolution control (UpdateSessionDisplaySettings) — MsRdpClient8/9+ only.</summary>
    public IMsRdpClient9 Client9 => (IMsRdpClient9)GetOcx();

    public bool IsConnected
    {
        get { try { return (int)Ocx.Connected == 1; } catch { return false; } }
    }
}

/// <summary>
/// IMsRdpExtendedSettings — a single parameterized property "Property" for extended settings such as
/// "DesktopScaleFactor" / "DeviceScaleFactor". Declared as an IDispatch interface so the CLR invokes by
/// name; setting a non-100 DeviceScaleFactor before Connect is also the documented workaround that stops
/// the Win10/11 SmartSizing back-buffer from blanking to black when the control is resized after connect.
/// </summary>
[ComImport, Guid("302D8188-0052-4807-806A-362B628F9AC5"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface IMsRdpExtendedSettings
{
    void set_Property([MarshalAs(UnmanagedType.BStr)] string name, [MarshalAs(UnmanagedType.Struct)] ref object value);
    [return: MarshalAs(UnmanagedType.Struct)]
    object get_Property([MarshalAs(UnmanagedType.BStr)] string name);
}

/// <summary>
/// IMsRdpClient9 — we only need UpdateSessionDisplaySettings (present since IMsRdpClient8). Declared as an
/// IDispatch interface, so only the method name matters, not the (large) vtable layout.
/// </summary>
[ComImport, Guid("28904001-04B6-436C-A55B-0AF1A0883DC9"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface IMsRdpClient9
{
    void UpdateSessionDisplaySettings(uint desktopWidth, uint desktopHeight, uint physicalWidth,
        uint physicalHeight, uint orientation, uint desktopScaleFactor, uint deviceScaleFactor);
}
