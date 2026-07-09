using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using Microsoft.Win32;
using RemoteDesktop.Client.Services;
using RemoteDesktop.Shared;

namespace RemoteDesktop.Client.Views;

/// <summary>
/// App settings: theme + language, a short guide for enabling RDP on the host PC, a few housekeeping
/// toggles/actions, and an About panel. Theme/language apply live; everything persists to ClientConfig.
/// </summary>
public partial class SettingsWindow : Window
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "LiteRemote";
    private const string RepoUrl = "https://github.com/amirullah/lite-remote-desktop";
    private const string IssuesUrl = RepoUrl + "/issues";

    private readonly ClientConfig _config;
    private bool _loading;

    public SettingsWindow(ClientConfig config)
    {
        _config = config;
        InitializeComponent();

        _loading = true;
        switch (ThemeManager.Preference)
        {
            case AppTheme.Light: ThemeLight.IsChecked = true; break;
            case AppTheme.Dark: ThemeDark.IsChecked = true; break;
            default: ThemeSystem.IsChecked = true; break;
        }
        if (Loc.Lang == "en") LangEn.IsChecked = true; else LangId.IsChecked = true;

        ConfirmDeleteCheck.IsChecked = _config.ConfirmDelete;
        StartupCheck.IsChecked = IsStartupEnabled();

        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = v is null ? "—" : $"v{v.Major}.{v.Minor}.{v.Build}";
        _loading = false;
    }

    // ---------------- appearance ----------------

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var t = ThemeLight.IsChecked == true ? AppTheme.Light
              : ThemeDark.IsChecked == true ? AppTheme.Dark
              : AppTheme.System;
        ThemeManager.SetPreference(t);
        _config.Theme = ThemeManager.ToKey(t);
        _config.Save();
    }

    private void Language_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var lang = LangEn.IsChecked == true ? "en" : "id";
        Loc.SetLanguage(lang);
        _config.Language = lang;
        _config.Save();
        (Owner as MainWindow)?.ApplyLanguageChange();   // refresh code-set (non-binding) text on the hub
    }

    // ---------------- other ----------------

    private void Startup_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try { SetStartup(StartupCheck.IsChecked == true); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "LiteRemote", MessageBoxButton.OK, MessageBoxImage.Warning);
            _loading = true; StartupCheck.IsChecked = IsStartupEnabled(); _loading = false;   // revert to reality
        }
    }

    private void ConfirmDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _config.ConfirmDelete = ConfirmDeleteCheck.IsChecked == true;
        _config.Save();
    }

    private void OpenRdpSettings_Click(object sender, RoutedEventArgs e)
        => ShellOpen("ms-settings:remotedesktop");

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AppPaths.EnsureRoot();
            var folder = Path.GetDirectoryName(AppPaths.ClientConfig);
            if (!string.IsNullOrEmpty(folder)) ShellOpen(folder);
        }
        catch (Exception ex) { Services.Diag.Log("OpenData: " + ex); }
    }

    private void ClearSessions_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, Loc.T("Settings.Other.ClearConfirm"), Loc.T("Settings.Other.ClearSessions"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        _config.ClearAllSessions();   // the owner refreshes its Recent list when this dialog closes
    }

    private void Github_Click(object sender, RoutedEventArgs e) => ShellOpen(RepoUrl);

    private void Issues_Click(object sender, RoutedEventArgs e) => ShellOpen(IssuesUrl);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---------------- helpers ----------------

    private static bool IsStartupEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return k?.GetValue(RunValueName) != null;
    }

    private static void SetStartup(bool on)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                       ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (on)
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe)) k?.SetValue(RunValueName, $"\"{exe}\"");
        }
        else k?.DeleteValue(RunValueName, throwOnMissingValue: false);
    }

    private void ShellOpen(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            Services.Diag.Log("ShellOpen '" + target + "': " + ex);
            MessageBox.Show(this, ex.Message, "LiteRemote", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
