using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace RemoteDesktop.Client.Services;

/// <summary>
/// "Show password" eye for every PasswordBox. The global PasswordBox template (App.xaml) carries a
/// hidden plain TextBox (PART_Reveal) and an eye ToggleButton (PART_Eye); this behaviour keeps the
/// TextBox and PasswordBox.Password in sync while revealed, so editing in either view works and every
/// caller keeps reading <c>PasswordBox.Password</c> unchanged. The plain text is cleared when hidden.
/// </summary>
public static class PasswordReveal
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(PasswordReveal), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject o) => (bool)o.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject o, bool v) => o.SetValue(EnabledProperty, v);

    private static readonly DependencyProperty WiredProperty = DependencyProperty.RegisterAttached(
        "Wired", typeof(bool), typeof(PasswordReveal), new PropertyMetadata(false));

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox pb || e.NewValue is not true) return;
        pb.Loaded += (_, _) => Wire(pb);
        if (pb.IsLoaded) Wire(pb);
    }

    private static void Wire(PasswordBox pb)
    {
        if ((bool)pb.GetValue(WiredProperty)) return;
        pb.ApplyTemplate();
        if (pb.Template?.FindName("PART_Reveal", pb) is not TextBox tb ||
            pb.Template.FindName("PART_Eye", pb) is not ToggleButton eye ||
            pb.Template.FindName("PART_ContentHost", pb) is not FrameworkElement host)
            return;
        pb.SetValue(WiredProperty, true);

        bool syncing = false;

        void Show()
        {
            syncing = true; tb.Text = pb.Password; syncing = false;
            host.Visibility = Visibility.Hidden;
            tb.Visibility = Visibility.Visible;
            tb.CaretIndex = tb.Text.Length;
            tb.Focus();
        }
        void Hide()
        {
            tb.Visibility = Visibility.Collapsed;
            host.Visibility = Visibility.Visible;
            syncing = true; tb.Text = string.Empty; syncing = false;   // don't leave plaintext lying around
        }

        eye.Checked += (_, _) => Show();
        eye.Unchecked += (_, _) => { Hide(); if (pb.IsVisible) pb.Focus(); };

        tb.TextChanged += (_, _) =>
        {
            if (syncing || eye.IsChecked != true) return;
            syncing = true; pb.Password = tb.Text; syncing = false;
        };
        pb.PasswordChanged += (_, _) =>
        {
            if (syncing || eye.IsChecked != true) return;
            syncing = true; tb.Text = pb.Password; tb.CaretIndex = tb.Text.Length; syncing = false;
        };

        // Re-mask whenever the box goes away (window hidden / closed / form collapsed).
        pb.IsVisibleChanged += (_, e) => { if (e.NewValue is false) eye.IsChecked = false; };
        pb.Unloaded += (_, _) => eye.IsChecked = false;
    }
}
