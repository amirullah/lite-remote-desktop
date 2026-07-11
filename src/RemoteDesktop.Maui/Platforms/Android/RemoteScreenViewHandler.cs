using Android.Graphics;
using Android.Views;
using Microsoft.Maui.Handlers;
using RemoteDesktop.Maui.Controls;

namespace RemoteDesktop.Maui;

/// <summary>
/// Maps <see cref="RemoteScreenView"/> to an Android <see cref="TextureView"/> and hands its render
/// <see cref="Surface"/> to the control when it becomes available. The <c>ISurfaceTextureListener</c> is
/// a Java interface, so it lives on a <see cref="Java.Lang.Object"/>-derived helper (a plain C# class
/// can't implement a Java peer interface). Registered in MauiProgram.
/// </summary>
public sealed class RemoteScreenViewHandler : ViewHandler<RemoteScreenView, TextureView>
{
    public static readonly IPropertyMapper<RemoteScreenView, RemoteScreenViewHandler> ScreenMapper =
        new PropertyMapper<RemoteScreenView, RemoteScreenViewHandler>(ViewMapper);

    public RemoteScreenViewHandler() : base(ScreenMapper) { }

    protected override TextureView CreatePlatformView() => new(Context!);

    protected override void ConnectHandler(TextureView platformView)
    {
        base.ConnectHandler(platformView);
        platformView.SurfaceTextureListener = new SurfaceListener(this);
    }

    private sealed class SurfaceListener : Java.Lang.Object, TextureView.ISurfaceTextureListener
    {
        private readonly RemoteScreenViewHandler _handler;
        public SurfaceListener(RemoteScreenViewHandler handler) => _handler = handler;

        public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height)
            => _handler.VirtualView?.RaiseSurfaceReady(new Surface(surface));

        public bool OnSurfaceTextureDestroyed(SurfaceTexture surface)
        {
            _handler.VirtualView?.RaiseSurfaceDestroyed();
            return true;
        }

        public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height) { }
        public void OnSurfaceTextureUpdated(SurfaceTexture surface) { }
    }
}
