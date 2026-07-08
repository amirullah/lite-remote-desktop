using System.Windows.Forms;

namespace RemoteDesktop.Client.Rendering;

/// <summary>
/// Hosts the Windows Remote Desktop ActiveX control (mstscax.dll) so a real RDP session renders inside
/// our own window (no external mstsc). We drive it purely through IDispatch late-binding (<c>dynamic</c>)
/// — no COM interop assembly / tlbimp is required, which keeps the project building under the plain
/// .NET SDK. RDP authenticates with the Windows account and can drive the login/lock screen, which
/// LiteRemote's own protocol cannot.
/// </summary>
internal sealed class RdpHost : AxHost
{
    // "Microsoft RDP Client Control - version 10" — present on Windows 10 1607+ and Windows 11.
    private const string ClsidV10 = "8B918B82-7985-4C24-89DF-C33AD2BBFBCD";

    public RdpHost() : base(ClsidV10) { }

    /// <summary>The underlying ActiveX object, driven via late binding (Server, UserName, Connect, …).</summary>
    public dynamic Ocx => GetOcx();

    public bool IsConnected
    {
        get { try { return (int)Ocx.Connected == 1; } catch { return false; } }
    }
}
