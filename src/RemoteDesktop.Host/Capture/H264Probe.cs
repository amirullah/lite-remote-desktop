using System.Runtime.InteropServices;

namespace RemoteDesktop.Host;

/// <summary>
/// Diagnostic for the upcoming H.264 path: <c>LiteRemoteHost --h264-probe</c>. Enumerates the
/// Media Foundation H.264 encoder transforms registered on this machine and reports whether a
/// hardware encoder (NVENC / AMD VCE / Intel QuickSync) is available. This tells us — before
/// building the full encode pipeline — whether hardware H.264 is usable here (physical PC) or absent
/// (typical VM), so we can decide the codec per host at runtime.
/// </summary>
internal static class H264Probe
{
    public static void Run()
    {
        AttachConsole(-1);
        Console.WriteLine();
        Console.WriteLine("H.264 encoder probe (Media Foundation)");
        Console.WriteLine(new string('-', 60));

        var log = new System.Text.StringBuilder();
        void Line(string s) { Console.WriteLine(s); log.AppendLine(s); }

        try
        {
            MFStartup(MF_VERSION, 0);

            // Output type: video / H.264. Input type left null to match any.
            var outputType = new MFT_REGISTER_TYPE_INFO { guidMajorType = MFMediaType_Video, guidSubtype = MFVideoFormat_H264 };
            var outPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MFT_REGISTER_TYPE_INFO>());
            Marshal.StructureToPtr(outputType, outPtr, false);

            int hwCount = 0, swCount = 0;
            foreach (var (flagName, flags) in new[] {
                ("HARDWARE", MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER),
                ("SOFTWARE", MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_ASYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER) })
            {
                int hr = MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER, flags, IntPtr.Zero, outPtr, out IntPtr activateArr, out int count);
                if (hr != 0) { Line($"{flagName}: enum gagal (hr=0x{hr:X8})"); continue; }
                Line($"{flagName}: {count} encoder");
                for (int i = 0; i < count; i++)
                {
                    IntPtr act = Marshal.ReadIntPtr(activateArr, i * IntPtr.Size);
                    string name = GetActivateString(act, MFT_FRIENDLY_NAME_Attribute);
                    Line($"   - {name}");
                    Marshal.Release(act);
                }
                if (count > 0) { if (flagName == "HARDWARE") hwCount = count; else swCount = count; }
                if (activateArr != IntPtr.Zero) CoTaskMemFree(activateArr);
            }

            Marshal.FreeHGlobal(outPtr);
            MFShutdown();

            Line(new string('-', 60));
            Line(hwCount > 0
                ? $"HASIL: hardware H.264 TERSEDIA ({hwCount}) -> lompatan besar mungkin di sini."
                : "HASIL: TIDAK ada hardware H.264 (khas VM) -> hanya software (bisa berat).");
        }
        catch (Exception ex)
        {
            Line($"ERROR: {ex.Message}");
        }

        try
        {
            var p = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "h264-probe.txt");
            System.IO.File.WriteAllText(p, log.ToString());
            Console.WriteLine($"(disimpan ke {p})");
        }
        catch { }
    }

    private static string GetActivateString(IntPtr activate, Guid key)
    {
        try
        {
            // IMFAttributes::GetAllocatedString via the activate object's vtable is complex from raw
            // interop; use MFGetAttributeString-style helper through the IMFActivate as IMFAttributes.
            var attrs = (IMFAttributes)Marshal.GetObjectForIUnknown(activate);
            attrs.GetAllocatedString(ref key, out IntPtr str, out int _);
            string s = Marshal.PtrToStringUni(str) ?? "?";
            CoTaskMemFree(str);
            return s;
        }
        catch { return "(nama tak terbaca)"; }
    }

    // ---- Media Foundation interop ----
    private const int MF_VERSION = 0x00020070;
    private const int MFT_ENUM_FLAG_SYNCMFT = 0x1, MFT_ENUM_FLAG_ASYNCMFT = 0x2, MFT_ENUM_FLAG_HARDWARE = 0x4, MFT_ENUM_FLAG_SORTANDFILTER = 0x40;

    private static readonly Guid MFT_CATEGORY_VIDEO_ENCODER = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFT_FRIENDLY_NAME_Attribute = new("314ffbae-5b41-4c95-9c19-4e7d586face3");

    [StructLayout(LayoutKind.Sequential)]
    private struct MFT_REGISTER_TYPE_INFO { public Guid guidMajorType; public Guid guidSubtype; }

    [DllImport("mfplat.dll")] private static extern int MFStartup(int version, int flags);
    [DllImport("mfplat.dll")] private static extern int MFShutdown();
    [DllImport("mfplat.dll")]
    private static extern int MFTEnumEx(Guid guidCategory, int flags, IntPtr inputType, IntPtr outputType,
        out IntPtr pppMFTActivate, out int numMFTActivate);
    [DllImport("ole32.dll")] private static extern void CoTaskMemFree(IntPtr ptr);

    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int dwProcessId);

    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFAttributes
    {
        void GetItem(); void GetItemType(); void CompareItem(); void Compare(); void GetUINT32();
        void GetUINT64(); void GetDouble(); void GetGUID(); void GetStringLength();
        void GetString();
        void GetAllocatedString([In] ref Guid guidKey, out IntPtr ppwszValue, out int pcchLength);
        // remaining methods unused
    }
}
