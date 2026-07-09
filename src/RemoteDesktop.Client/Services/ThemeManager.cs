using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace RemoteDesktop.Client.Services;

public enum AppTheme { System, Light, Dark }

/// <summary>
/// Live dark/light theming. On each apply we REPLACE the palette brush objects in the application
/// resources (resource brushes get frozen, so in-place Color mutation wouldn't propagate). Every
/// consumer therefore references these keys via <c>DynamicResource</c>, so swapping the object repaints
/// the whole UI instantly. "System" follows the OS light/dark setting and re-applies when it changes.
/// </summary>
public static class ThemeManager
{
    private static AppTheme _pref = AppTheme.System;

    public static AppTheme Preference => _pref;

    public static AppTheme Parse(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "light" => AppTheme.Light,
        "dark" => AppTheme.Dark,
        _ => AppTheme.System,
    };

    public static string ToKey(AppTheme t) => t switch { AppTheme.Light => "light", AppTheme.Dark => "dark", _ => "system" };

    public static void Init(AppTheme pref)
    {
        _pref = pref;
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (_pref == AppTheme.System && e.Category == UserPreferenceCategory.General)
                Application.Current?.Dispatcher.Invoke(Apply);
        };
        Apply();
    }

    public static void SetPreference(AppTheme pref)
    {
        _pref = pref;
        Apply();
    }

    private static bool EffectiveDark() => _pref switch
    {
        AppTheme.Dark => true,
        AppTheme.Light => false,
        _ => SystemUsesDark(),
    };

    private static bool SystemUsesDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (k?.GetValue("AppsUseLightTheme") is int i) return i == 0;
        }
        catch { }
        return true; // default to dark if the setting can't be read
    }

    private static void Apply()
    {
        var app = Application.Current;
        if (app == null) return;
        var p = EffectiveDark() ? Dark : Light;

        SetColor("Bg", p.Bg);
        SetColor("Panel", p.Panel);
        SetColor("PanelHi", p.PanelHi);
        SetColor("Stroke", p.Stroke);
        SetColor("Fg", p.Fg);
        SetColor("Muted", p.Muted);
        SetColor("Good", p.Good);
        SetColor("Bad", p.Bad);
        SetColor("Accent", p.Accent);
        SetColor("GhostBg", p.GhostBg);
        SetColor("GhostBorder", p.GhostBorder);
        SetColor("DisabledBg", p.DisabledBg);
        SetColor("DisabledBorder", p.DisabledBorder);
        SetColor("DisabledFg", p.DisabledFg);
        SetGradient("AccentGrad", p.Accent, p.Accent2);
        SetGradient("AppBg", p.AppBg0, p.AppBg1);
    }

    // Replace the resource object (not mutate) — resource brushes may be frozen, and every consumer
    // now references them via DynamicResource, so swapping the object repaints the whole UI live.
    private static void SetColor(string key, Color c)
    {
        var app = Application.Current;
        if (app != null) app.Resources[key] = new SolidColorBrush(c);
    }

    private static void SetGradient(string key, Color a, Color b)
    {
        var app = Application.Current;
        if (app == null) return;
        var end = key == "AppBg" ? new Point(0.6, 1) : new Point(1, 1);
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = end };
        g.GradientStops.Add(new GradientStop(a, 0));
        g.GradientStops.Add(new GradientStop(b, 1));
        app.Resources[key] = g;
    }

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

    private sealed record Palette(
        Color Bg, Color Panel, Color PanelHi, Color Stroke, Color Fg, Color Muted, Color Good, Color Bad,
        Color Accent, Color Accent2, Color GhostBg, Color GhostBorder, Color DisabledBg, Color DisabledBorder,
        Color DisabledFg, Color AppBg0, Color AppBg1);

    private static readonly Palette Dark = new(
        Bg: C("#0A1220"), Panel: C("#111C30"), PanelHi: C("#1A2A47"), Stroke: C("#22335A"),
        Fg: C("#EAF1FB"), Muted: C("#7E90AE"), Good: C("#2FBF71"), Bad: C("#E5534B"),
        // GhostBg is a FILLED secondary-button surface (white text) — muted, not the bright primary.
        Accent: C("#2F7CF6"), Accent2: C("#4EA1FF"), GhostBg: C("#2A3D5F"), GhostBorder: C("#33507F"),
        DisabledBg: C("#26344F"), DisabledBorder: C("#33456A"), DisabledFg: C("#8494B0"),
        AppBg0: C("#0D1930"), AppBg1: C("#080E1B"));

    private static readonly Palette Light = new(
        Bg: C("#EDF1F8"), Panel: C("#FFFFFF"), PanelHi: C("#E3EAF6"), Stroke: C("#D2DCEC"),
        Fg: C("#16233B"), Muted: C("#546482"), Good: C("#1E9E5A"), Bad: C("#D23F36"),
        // Deeper blue in light mode so WHITE button text stays clearly readable (a bright accent + white
        // text is low-contrast). Dark theme keeps the lighter accent (it pops against a dark background).
        Accent: C("#1A64DC"), Accent2: C("#2E7DE8"), GhostBg: C("#51648A"), GhostBorder: C("#C7D3E7"),
        DisabledBg: C("#C9D3E4"), DisabledBorder: C("#B7C4DA"), DisabledFg: C("#5E6C86"),
        AppBg0: C("#F4F7FC"), AppBg1: C("#E8EEF8"));
}
