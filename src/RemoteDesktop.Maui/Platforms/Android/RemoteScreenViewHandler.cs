using Android.Graphics;
using Android.Views;
using Microsoft.Maui.Handlers;
using RemoteDesktop.Maui.Controls;

namespace RemoteDesktop.Maui;

/// <summary>
/// Maps <see cref="RemoteScreenView"/> to an Android <see cref="TextureView"/>: hands its render
/// <see cref="Surface"/> to the control, forwards raw multi-touch, and applies the letterbox/zoom/pan
/// rectangle with <see cref="TextureView.SetTransform"/> (the video texture is drawn full-view by default,
/// so the matrix scales/moves it into the requested rectangle — cheap, no re-layout). The
/// <c>ISurfaceTextureListener</c> is a Java interface, so it lives on a <see cref="Java.Lang.Object"/> helper.
/// </summary>
public sealed class RemoteScreenViewHandler : ViewHandler<RemoteScreenView, TextureView>
{
    public static readonly IPropertyMapper<RemoteScreenView, RemoteScreenViewHandler> ScreenMapper =
        new PropertyMapper<RemoteScreenView, RemoteScreenViewHandler>(ViewMapper);

    private (float l, float t, float w, float h)? _rect;

    public RemoteScreenViewHandler() : base(ScreenMapper) { }

    protected override TextureView CreatePlatformView() => new(Context!);

    protected override void ConnectHandler(TextureView platformView)
    {
        base.ConnectHandler(platformView);
        platformView.SurfaceTextureListener = new SurfaceListener(this);
        platformView.Touch += OnTouch;
        platformView.LayoutChange += OnLayoutChange;
        VirtualView.ContentRectRequested += OnContentRect;
    }

    protected override void DisconnectHandler(TextureView platformView)
    {
        platformView.Touch -= OnTouch;
        platformView.LayoutChange -= OnLayoutChange;
        if (VirtualView is not null) VirtualView.ContentRectRequested -= OnContentRect;
        base.DisconnectHandler(platformView);
    }

    private void OnLayoutChange(object? sender, Android.Views.View.LayoutChangeEventArgs e)
    {
        VirtualView?.SetPixelSize(PlatformView.Width, PlatformView.Height);
        ApplyTransform();
    }

    private void OnContentRect(float l, float t, float w, float h)
    {
        _rect = (l, t, w, h);
        ApplyTransform();
    }

    private void ApplyTransform()
    {
        var v = PlatformView;
        if (v is null || _rect is not { } r || v.Width <= 0 || v.Height <= 0) return;
        // Texture fills the whole view by default → scale it to the rect size and move it to (l, t).
        var m = new Matrix();
        m.SetScale(r.w / v.Width, r.h / v.Height);
        m.PostTranslate(r.l, r.t);
        v.SetTransform(m);
        v.Invalidate();
    }

    private void OnTouch(object? sender, Android.Views.View.TouchEventArgs e)
    {
        var ev = e.Event;
        if (ev is null || VirtualView is null) { e.Handled = false; return; }
        e.Handled = true;
        int count = ev.PointerCount;
        float x = ev.GetX(0), y = ev.GetY(0);
        float x2 = count > 1 ? ev.GetX(1) : x, y2 = count > 1 ? ev.GetY(1) : y;
        long t = ev.EventTime;
        switch (ev.ActionMasked)
        {
            case MotionEventActions.Down:
                VirtualView.RaiseTouch(new TouchFrame(TouchAction.Down, 1, x, y, x2, y2, t)); break;
            case MotionEventActions.PointerDown:
                VirtualView.RaiseTouch(new TouchFrame(TouchAction.Down, count, x, y, x2, y2, t)); break;
            case MotionEventActions.Move:
                VirtualView.RaiseTouch(new TouchFrame(TouchAction.Move, count, x, y, x2, y2, t)); break;
            case MotionEventActions.PointerUp:
            {
                // Report the finger(s) that REMAIN so the gesture layer tracks the right positions.
                int up = ev.ActionIndex;
                int keep = up == 0 ? 1 : 0;
                VirtualView.RaiseTouch(new TouchFrame(TouchAction.Up, count - 1, ev.GetX(keep), ev.GetY(keep), x2, y2, t));
                break;
            }
            case MotionEventActions.Up:
                VirtualView.RaiseTouch(new TouchFrame(TouchAction.Up, 0, x, y, x2, y2, t)); break;
            case MotionEventActions.Cancel:
                VirtualView.RaiseTouch(new TouchFrame(TouchAction.Cancel, 0, x, y, x2, y2, t)); break;
        }
    }

    private sealed class SurfaceListener : Java.Lang.Object, TextureView.ISurfaceTextureListener
    {
        private readonly RemoteScreenViewHandler _handler;
        public SurfaceListener(RemoteScreenViewHandler handler) => _handler = handler;

        public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height)
        {
            _handler.VirtualView?.SetPixelSize(width, height);
            _handler.VirtualView?.RaiseSurfaceReady(new Surface(surface));
            _handler.ApplyTransform();
        }

        public bool OnSurfaceTextureDestroyed(SurfaceTexture surface)
        {
            _handler.VirtualView?.RaiseSurfaceDestroyed();
            return true;
        }

        public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height)
        {
            _handler.VirtualView?.SetPixelSize(width, height);
            _handler.ApplyTransform();
        }

        public void OnSurfaceTextureUpdated(SurfaceTexture surface) { }
    }
}
