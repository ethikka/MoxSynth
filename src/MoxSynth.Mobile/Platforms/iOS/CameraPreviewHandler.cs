using AVFoundation;
using CoreAnimation;
using Microsoft.Maui.Handlers;
using UIKit;

namespace MoxSynth.Mobile.Controls;

/// <summary>Shows a <see cref="CameraPreview"/> as a <see cref="CameraPreviewView"/> (AVFoundation preview layer).</summary>
public sealed class CameraPreviewHandler() : ViewHandler<CameraPreview, CameraPreviewView>(ViewMapper)
{
    protected override CameraPreviewView CreatePlatformView() => new();
}

/// <summary>
/// Hosts an <see cref="AVCaptureVideoPreviewLayer"/> that fills the view, centred (aspect fill, the iOS counterpart of
/// CameraX FILL_CENTER); ScanGeometry.GuideInFrame relies on exactly this scaling.
/// </summary>
public sealed class CameraPreviewView : UIView
{
    public CameraPreviewView()
    {
        BackgroundColor = UIColor.Black;
        PreviewLayer = new AVCaptureVideoPreviewLayer { VideoGravity = AVLayerVideoGravity.ResizeAspectFill };
        Layer.AddSublayer(PreviewLayer);
    }

    public AVCaptureVideoPreviewLayer PreviewLayer { get; }

    /// <summary>Raised on the main thread after the view got a new size (or the device rotated).</summary>
    public event Action? LayoutChanged;

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        // No implicit animation: the layer should follow the view at once.
        CATransaction.Begin();
        CATransaction.DisableActions = true;
        PreviewLayer.Frame = Bounds;
        CATransaction.Commit();
        LayoutChanged?.Invoke();
    }
}
