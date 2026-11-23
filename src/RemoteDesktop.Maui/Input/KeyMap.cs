namespace RemoteDesktop.Maui.Input;

/// <summary>
/// Maps typed characters / special keys to Windows virtual-key codes (US layout) for the host, which
/// injects them with SendInput (VK path when ScanCode = 0). Pure data — unit-testable.
/// </summary>
public static class KeyMap
{
    public const ushort Back = 0x08, Tab = 0x09, Enter = 0x0D, Shift = 0x10, Ctrl = 0x11, Alt = 0x12,
        Esc = 0x1B, Space = 0x20, PageUp = 0x21, PageDown = 0x22, End = 0x23, Home = 0x24,
        Left = 0x25, Up = 0x26, Right = 0x27, Down = 0x28, Delete = 0x2E, Win = 0x5B, F1 = 0x70;

    /// <summary>Keys that need the "extended" flag (navigation cluster, right-hand modifiers, Win).</summary>
    public static bool IsExtended(ushort vk) => vk is >= PageUp and <= Down or Delete or 0x2D /*Insert*/ or Win;

    private static readonly Dictionary<char, (ushort vk, bool shift)> Chars = Build();

    private static Dictionary<char, (ushort, bool)> Build()
    {
        var m = new Dictionary<char, (ushort, bool)>();
        for (char c = 'a'; c <= 'z'; c++) m[c] = ((ushort)(c - 'a' + 0x41), false);
        for (char c = 'A'; c <= 'Z'; c++) m[c] = ((ushort)(c - 'A' + 0x41), true);
        for (char c = '0'; c <= '9'; c++) m[c] = ((ushort)(c - '0' + 0x30), false);
        const string shiftedDigits = ")!@#$%^&*(";   // Shift + 0..9
        for (int i = 0; i < 10; i++) m[shiftedDigits[i]] = ((ushort)(0x30 + i), true);
        void P(char plain, char shifted, ushort vk) { m[plain] = (vk, false); m[shifted] = (vk, true); }
        P(';', ':', 0xBA); P('=', '+', 0xBB); P(',', '<', 0xBC); P('-', '_', 0xBD); P('.', '>', 0xBE);
        P('/', '?', 0xBF); P('`', '~', 0xC0); P('[', '{', 0xDB); P('\\', '|', 0xDC); P(']', '}', 0xDD); P('\'', '"', 0xDE);
        m[' '] = (Space, false); m['\n'] = (Enter, false); m['\r'] = (Enter, false); m['\t'] = (Tab, false);
        return m;
    }

    /// <summary>VK + whether Shift must be held for <paramref name="c"/>; false if not typeable on a US layout.</summary>
    public static bool TryMap(char c, out ushort vk, out bool shift)
    {
        if (Chars.TryGetValue(c, out var v)) { vk = v.vk; shift = v.shift; return true; }
        vk = 0; shift = false; return false;
    }
}
