using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;

namespace RemoteDesktop.Maui;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    private const int VpnConsentRequest = 0x4C52;
    private static TaskCompletionSource<bool>? _vpnConsent;

    /// <summary>Android's "allow LiteRemote to set up a VPN connection?" dialog. True when granted
    /// (or already granted earlier and not taken over by another VPN app since).</summary>
    public static async Task<bool> RequestVpnConsentAsync()
    {
        return await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var activity = Platform.CurrentActivity as MainActivity;
            var intent = VpnService.Prepare(activity ?? Android.App.Application.Context);
            if (intent is null) return true;
            if (activity is null) return false;
            _vpnConsent?.TrySetResult(false);
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _vpnConsent = tcs;
            activity.StartActivityForResult(intent, VpnConsentRequest);
            return await tcs.Task;
        });
    }

    /// <summary>Android 13+: ask once for notifications so the "VPN kampus aktif" notice is visible. Best effort.</summary>
    public static async Task RequestNotificationPermissionAsync()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return;
        try
        {
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                if (await Permissions.CheckStatusAsync<Permissions.PostNotifications>() != PermissionStatus.Granted)
                    await Permissions.RequestAsync<Permissions.PostNotifications>();
            });
        }
        catch { }
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == VpnConsentRequest)
        {
            var tcs = _vpnConsent;
            _vpnConsent = null;
            tcs?.TrySetResult(resultCode == Result.Ok);
        }
    }
}
