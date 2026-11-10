using AVFoundation;
using CoreFoundation;
using CoreMedia;
using CoreVideo;
using Foundation;
using ImageIO;
using MoxSynth.Client.Services;
using MoxSynth.Contracts;
using MoxSynth.Mobile.Controls;
using UIKit;
using Vision;

namespace MoxSynth.Mobile.Services;

/// <summary>
/// The live scanner's camera on iOS: an AVCaptureSession shows the back camera in a <see cref="CameraPreviewView"/>
/// and Apple Vision reads the text of at most ~2 frames per second, one at a time (frames arriving meanwhile are
/// dropped). The words inside the guide go to the callback in guide-relative pixels; the next frame is read only after
/// the callback's task completes. iOS interrupts the session itself while the app is in the background.
/// Same public surface as the Android version, so <c>LiveScanPage</c> works with both. Must be used from the main thread.
/// </summary>
public sealed class LiveCardCamera
{
    private const long ReadIntervalMs = 500;

    private readonly CameraPreviewView _view;
    private readonly Func<IReadOnlyList<OcrLineDto>, bool, Task> _onText;
    private readonly DispatchQueue _sessionQueue = new("moxsynth.camera.session");
    private readonly DispatchQueue _frameQueue = new("moxsynth.camera.frames");
    private readonly AVCaptureSession _session = new();
    private readonly FrameDelegate _delegate;

    private AVCaptureDevice? _device;
    private AVCaptureVideoDataOutput? _output;

    // Shared with the frame queue.
    private volatile GuideLayout? _layout;
    private volatile bool _busy;
    private volatile bool _stopped;
    private int _forceNext;
    private long _lastReadAt;

    /// <param name="view">The preview (aspect fill).</param>
    /// <param name="onText">Gets the grouped lines inside the guide (may be empty) and whether the read was forced by
    /// <see cref="ReadNextFrame"/>. Called on a background thread.</param>
    public LiveCardCamera(CameraPreviewView view, Func<IReadOnlyList<OcrLineDto>, bool, Task> onText)
    {
        _view = view;
        _onText = onText;
        _delegate = new FrameDelegate(OnFrame);
        _view.LayoutChanged += UpdateOrientation;
    }

    /// <summary>The device has a back camera (the live scanner doesn't use the front one; the simulator has none).</summary>
    public static bool HasBackCamera => BackCamera() is not null;

    /// <summary>Raised on the main thread after the camera started, e.g. to show the torch button.</summary>
    public event Action? Bound;

    /// <summary>Raised on the main thread when the camera can't be opened.</summary>
    public event Action<Exception>? Failed;

    public bool HasFlashUnit => _device?.HasTorch == true;

    public async Task StartAsync()
    {
        var device = BackCamera() ?? throw new InvalidOperationException("This device has no camera.");
        var input = AVCaptureDeviceInput.FromDevice(device, out var error)
                    ?? throw new InvalidOperationException(error?.LocalizedDescription ?? "The camera can't be opened.");

        _session.BeginConfiguration();
        // 1080p, like on Android: at 720p the collector number line gets too small to read. The preview and the frames
        // come from the same format, so they show the same field of view.
        if (_session.CanSetSessionPreset(AVCaptureSession.Preset1920x1080))
            _session.SessionPreset = AVCaptureSession.Preset1920x1080;
        else
            _session.SessionPreset = AVCaptureSession.PresetHigh;
        if (!_session.CanAddInput(input))
        {
            _session.CommitConfiguration();
            throw new InvalidOperationException("The camera can't be opened.");
        }
        _session.AddInput(input);

        var output = new AVCaptureVideoDataOutput { AlwaysDiscardsLateVideoFrames = true };
        output.SetSampleBufferDelegate(_delegate, _frameQueue);
        if (_session.CanAddOutput(output)) _session.AddOutput(output);
        _session.CommitConfiguration();

        _device = device;
        _output = output;
        ConfigureFocus(device);
        _view.PreviewLayer.Session = _session;
        UpdateOrientation();

        // StartRunning blocks until the camera runs: off the main thread.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sessionQueue.DispatchAsync(() =>
        {
            try
            {
                if (!_stopped) _session.StartRunning();
                started.TrySetResult();
            }
            catch (Exception ex)
            {
                started.TrySetException(ex);
            }
        });
        await started.Task;
        if (_stopped) return;

        if (!_session.Running)
        {
            Failed?.Invoke(new InvalidOperationException("The camera can't be started."));
            return;
        }
        Bound?.Invoke();
    }

    /// <summary>The guide's position over the preview; <paramref name="view"/> and <paramref name="guide"/> in the same unit.</summary>
    public void SetGuide(Size view, Rect guide) =>
        _layout = view.Width > 0 && view.Height > 0 && guide.Width > 0 && guide.Height > 0 ? new GuideLayout(view, guide) : null;

    /// <summary>Reads the next frame right away, even within the interval (shutter).</summary>
    public void ReadNextFrame() => Interlocked.Exchange(ref _forceNext, 1);

    /// <summary>Switches the torch.</summary>
    public void ToggleTorch()
    {
        if (_device is not { HasTorch: true } device) return;
        if (!device.LockForConfiguration(out _)) return;
        try
        {
            device.TorchMode = device.TorchMode == AVCaptureTorchMode.On ? AVCaptureTorchMode.Off : AVCaptureTorchMode.On;
        }
        finally
        {
            device.UnlockForConfiguration();
        }
    }

    /// <summary>Releases the camera. A stopped instance can't be restarted.</summary>
    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        _view.LayoutChanged -= UpdateOrientation;
        _view.PreviewLayer.Session = null;
        _output?.SetSampleBufferDelegate(null, null);
        var session = _session;
        _sessionQueue.DispatchAsync(() =>
        {
            if (session.Running) session.StopRunning();
            session.Dispose();
        });
        _device = null;
    }

    private static AVCaptureDevice? BackCamera() =>
        AVCaptureDevice.GetDefaultDevice(AVCaptureDeviceType.BuiltInWideAngleCamera, AVMediaTypes.Video.GetConstant()!,
            AVCaptureDevicePosition.Back);

    /// <summary>Continuous autofocus, biased to near (a card held in front of the phone).</summary>
    private static void ConfigureFocus(AVCaptureDevice device)
    {
        if (!device.LockForConfiguration(out _)) return;
        try
        {
            if (device.IsFocusModeSupported(AVCaptureFocusMode.ContinuousAutoFocus))
                device.FocusMode = AVCaptureFocusMode.ContinuousAutoFocus;
            if (device.AutoFocusRangeRestrictionSupported)
                device.AutoFocusRangeRestriction = AVCaptureAutoFocusRangeRestriction.Near;
        }
        finally
        {
            device.UnlockForConfiguration();
        }
    }

    /// <summary>
    /// Turns preview and frames the way the screen is turned, so the frames Vision reads are upright and match what is
    /// on screen (the iOS counterpart of CameraX's target rotation).
    /// </summary>
    private void UpdateOrientation()
    {
        if (_stopped) return;
        var scene = _view.Window?.WindowScene;
        var interfaceOrientation = scene is null ? UIInterfaceOrientation.Portrait
            : OperatingSystem.IsIOSVersionAtLeast(26) ? scene.EffectiveGeometry.InterfaceOrientation
#pragma warning disable CA1422 // replaced by EffectiveGeometry on iOS 26
            : scene.InterfaceOrientation;
#pragma warning restore CA1422
        var orientation = interfaceOrientation switch
        {
            UIInterfaceOrientation.PortraitUpsideDown => AVCaptureVideoOrientation.PortraitUpsideDown,
            UIInterfaceOrientation.LandscapeLeft => AVCaptureVideoOrientation.LandscapeLeft,
            UIInterfaceOrientation.LandscapeRight => AVCaptureVideoOrientation.LandscapeRight,
            _ => AVCaptureVideoOrientation.Portrait,
        };

#pragma warning disable CA1422 // VideoOrientation is deprecated on iOS 17 (VideoRotationAngle) but still works
        if (_view.PreviewLayer.Connection is { SupportsVideoOrientation: true } preview && preview.VideoOrientation != orientation)
            preview.VideoOrientation = orientation;
        if (_output?.ConnectionFromMediaType(AVMediaTypes.Video.GetConstant()!) is { SupportsVideoOrientation: true } frames
            && frames.VideoOrientation != orientation)
            frames.VideoOrientation = orientation;
#pragma warning restore CA1422
    }

    /// <summary>Frame queue. Late frames are discarded by AVFoundation while this one is being read.</summary>
    private void OnFrame(CMSampleBuffer sample)
    {
        var forced = Volatile.Read(ref _forceNext) == 1;
        var now = Environment.TickCount64;
        if (_stopped || _busy || _layout is not { } layout || (!forced && now - _lastReadAt < ReadIntervalMs)) return;

        if (forced) Interlocked.Exchange(ref _forceNext, 0);
        _busy = true;
        _lastReadAt = now;

        IReadOnlyList<OcrLineDto>? lines = null;
        try
        {
            using var pixels = sample.GetImageBuffer() as CVPixelBuffer;
            if (pixels is not null)
            {
                // Frames are already upright (connection orientation), so no rotation and no crop: the preview shows
                // the whole frame, aspect-filled.
                var size = new Size(pixels.Width, pixels.Height);
                var guide = ScanGeometry.GuideInFrame(layout.View, layout.Guide, size, new Rect(Point.Zero, size), 0);
                var words = ReadWords(pixels, size);
                lines = OcrLineGrouper.Group(ScanGeometry.ToGuideSpace(words, guide));
            }
        }
        catch (Exception ex)
        {
            // One unreadable frame: just take the next one.
            System.Diagnostics.Debug.WriteLine($"Live scan: OCR failed: {ex.Message}");
        }

        _ = DeliverAsync(lines, forced);
    }

    private async Task DeliverAsync(IReadOnlyList<OcrLineDto>? lines, bool forced)
    {
        try
        {
            if (lines is not null && !_stopped) await _onText(lines, forced);
            else if (forced) Interlocked.Exchange(ref _forceNext, 1); // the shutter still wants a read
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Live scan: text handler failed: {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Reads the words of an upright frame with Vision, in frame pixels with the origin at the top left (like ML Kit's
    /// elements). Vision reports lines with normalised boxes from the bottom left; each word gets its own box.
    /// </summary>
    private static List<OcrLineDto> ReadWords(CVPixelBuffer pixels, Size size)
    {
        var words = new List<OcrLineDto>();
        using var request = new VNRecognizeTextRequest((_, _) => { })
        {
            RecognitionLevel = VNRequestTextRecognitionLevel.Accurate,
            // Card names are made-up words; "correcting" them makes the server's job harder.
            UsesLanguageCorrection = false,
        };
        using var handler = new VNImageRequestHandler(pixels, CGImagePropertyOrientation.Up, new VNImageOptions());
        if (!handler.Perform([request], out var error))
        {
            System.Diagnostics.Debug.WriteLine($"Live scan: Vision failed: {error?.LocalizedDescription}");
            return words;
        }

        foreach (var observation in request.GetResults<VNRecognizedTextObservation>() ?? [])
        {
            if (observation.TopCandidates(1).FirstOrDefault() is not { } candidate) continue;
            var text = candidate.String;
            var start = -1;
            for (var i = 0; i <= text.Length; i++)
            {
                var isSpace = i == text.Length || char.IsWhiteSpace(text[i]);
                if (!isSpace && start < 0) start = i;
                else if (isSpace && start >= 0)
                {
                    var box = candidate.GetBoundingBox(new NSRange(start, i - start), out _)?.BoundingBox
                              ?? observation.BoundingBox;
                    words.Add(new OcrLineDto(text[start..i],
                        box.X * size.Width, (1 - box.Y - box.Height) * size.Height,
                        box.Width * size.Width, box.Height * size.Height));
                    start = -1;
                }
            }
        }
        return words;
    }

    private sealed record GuideLayout(Size View, Rect Guide);

    private sealed class FrameDelegate(Action<CMSampleBuffer> onFrame) : AVCaptureVideoDataOutputSampleBufferDelegate
    {
        public override void DidOutputSampleBuffer(AVCaptureOutput captureOutput, CMSampleBuffer sampleBuffer,
            AVCaptureConnection connection)
        {
            try
            {
                onFrame(sampleBuffer);
            }
            finally
            {
                // Must be released right away, or AVFoundation runs out of buffers and stops delivering frames.
                sampleBuffer.Dispose();
            }
        }
    }
}
