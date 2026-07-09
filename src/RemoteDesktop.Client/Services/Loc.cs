using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;

namespace RemoteDesktop.Client.Services;

/// <summary>
/// Tiny runtime localizer (Indonesian / English) with LIVE switching. XAML binds through
/// <c>{loc:Loc Some.Key}</c> to <see cref="LocProvider"/>'s indexer; changing the language raises a
/// change notification so every bound string updates in place. Code reads strings via <see cref="T"/>.
/// </summary>
public static partial class Loc
{
    private static string _lang = "id";
    public static string Lang => _lang;

    public static void Init(string? lang) => _lang = Normalize(lang);

    public static void SetLanguage(string? lang)
    {
        var l = Normalize(lang);
        if (l == _lang) return;
        _lang = l;
        LocProvider.Instance.Refresh();
    }

    private static string Normalize(string? l) => l?.Trim().ToLowerInvariant() == "en" ? "en" : "id";

    /// <summary>Look up a key in the active language; falls back to Indonesian, then the key itself.</summary>
    public static string T(string key)
    {
        var table = _lang == "en" ? En : Id;
        if (table.TryGetValue(key, out var v)) return v;
        if (Id.TryGetValue(key, out var f)) return f;
        return key;
    }

    /// <summary>Localized string with <see cref="string.Format(string, object[])"/> arguments.</summary>
    public static string F(string key, params object[] args) => string.Format(T(key), args);
}

/// <summary>Notifies bindings when the language changes (via a bumped tick).</summary>
public sealed class LocProvider : INotifyPropertyChanged
{
    public static LocProvider Instance { get; } = new();
    private LocProvider() { }

    public int Tick { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh() { Tick++; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Tick))); }
}

/// <summary>
/// XAML markup extension: <c>{loc:Loc Connect.TypeLabel}</c>. Binds through a converter (not an indexer
/// path, which mis-parses dotted keys) to a notifying tick, so the value re-evaluates on a language change.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public string Key { get; set; } = "";

    public LocExtension() { }
    public LocExtension(string key) { Key = key; }

    private static readonly LocConverter Conv = new();

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding(nameof(LocProvider.Tick))
        {
            Source = LocProvider.Instance,
            Converter = Conv,
            ConverterParameter = Key,
            Mode = BindingMode.OneWay,
        };
        return binding.ProvideValue(serviceProvider);
    }

    private sealed class LocConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => Loc.T(parameter as string ?? "");
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
