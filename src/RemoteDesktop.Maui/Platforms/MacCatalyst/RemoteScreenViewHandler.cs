using AVFoundation;
using Microsoft.Maui.Handlers;
using RemoteDesktop.Maui.Controls;
using UIKit;

namespace RemoteDesktop.Maui;

/// <summary>
/// Maps <see cref="RemoteScreenView"/> to a UIKit <see cref="UIView"/> whose backing sublayer is an
/// <see cref="AVSampleBufferDisplayLayer"/>; that layer is handed to the control (as the render surface)
/// for <see cref="MacVideoDecoder"/> to enqueue decoded H.264 into. Registered in MauiProgram.
/// </summary>
public sealed class RemoteScreenViewHandler : ViewHandler<RemoteScreenView, UIView>
{
    public static readonly IPropertyMapper<RemoteScreenView, RemoteScreenViewHandler> ScreenMapper =
        new PropertyMapper<RemoteScreenView, RemoteScreenViewHandler>(ViewMapper);

    public RemoteScreenViewHandler() : base(ScreenMapper) { }

    protected override UIView CreatePlatformView() => new VideoView();

    protected override void ConnectHandler(UIView platformView)
    {
        base.ConnectHandler(platformView);
        if (platformView is VideoView vv)
            VirtualView?.RaiseSurfaceReady(vv.SampleLayer);
    }

    protected override void DisconnectHandler(UIView platformView)
    {
        VirtualView?.RaiseSurfaceDestroyed();
        base.DisconnectHandler(platformView);
    }

    private sealed class VideoView : UIView
    {
        public AVSampleBufferDisplayLayer SampleLayer { get; } = new();

        public VideoView()
        {
            BackgroundColor = UIColor.Black;
            // Preserve aspect ratio. VideoGravity is typed as string (the raw AVLayerVideoGravity
            // constant); the enum has no GetConstant() here, so set the well-known constant value.
            SampleLayer.VideoGravity = "AVLayerVideoGravityResizeAspect";
            Layer.AddSublayer(SampleLayer);
        }

        public override void LayoutSubviews()
        {
            base.LayoutSubviews();
            SampleLayer.Frame = Bounds; // keep the video layer sized to the view
        }
    }
}
