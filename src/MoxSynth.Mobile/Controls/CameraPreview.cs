namespace MoxSynth.Mobile.Controls;

/// <summary>
/// Live camera picture for the scanner. Only a host for the native preview (CameraX <c>PreviewView</c> on Android,
/// an AVFoundation preview layer on iOS; see <c>CameraPreviewHandler</c>); the camera itself is driven by
/// <c>LiveCardCamera</c>.
/// </summary>
public sealed class CameraPreview : View
{
}
