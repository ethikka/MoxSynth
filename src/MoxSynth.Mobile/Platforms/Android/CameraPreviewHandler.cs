using AndroidX.Camera.View;
using Microsoft.Maui.Handlers;

namespace MoxSynth.Mobile.Controls;

/// <summary>Shows a <see cref="CameraPreview"/> as a CameraX <see cref="PreviewView"/>.</summary>
public sealed class CameraPreviewHandler() : ViewHandler<CameraPreview, PreviewView>(ViewMapper)
{
    protected override PreviewView CreatePlatformView()
    {
        var view = new PreviewView(Context);
        // TextureView instead of SurfaceView: composes with the MAUI overlays and the modal page animation.
        view.SetImplementationMode(PreviewView.ImplementationMode.Compatible!);
        // Fill the screen, centred; ScanGeometry.GuideInFrame relies on exactly this scaling.
        view.SetScaleType(PreviewView.ScaleType.FillCenter!);
        return view;
    }
}
