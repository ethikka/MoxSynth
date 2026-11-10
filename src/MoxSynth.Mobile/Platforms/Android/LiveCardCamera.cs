using Android.Content.PM;
using Android.Gms.Extensions;
using Android.Runtime;
using Android.Views;
using AndroidX.Camera.Core;
using AndroidX.Camera.Lifecycle;
using AndroidX.Camera.View;
using AndroidX.Core.Content;
using AndroidX.Lifecycle;
using MoxSynth.Client.Services;
using MoxSynth.Contracts;
using Xamarin.Google.MLKit.Vision.Common;
using Xamarin.Google.MLKit.Vision.Text;
using Xamarin.Google.MLKit.Vision.Text.Latin;
using AspectRatioStrategy = AndroidX.Camera.Core.ResolutionSelector.AspectRatioStrategy;
using MlText = Xamarin.Google.MLKit.Vision.Text.Text;
using ResolutionSelector = AndroidX.Camera.Core.ResolutionSelector.ResolutionSelector;
using ResolutionStrategy = AndroidX.Camera.Core.ResolutionSelector.ResolutionStrategy;

namespace MoxSynth.Mobile.Services;

/// <summary>
/// The live scanner's camera: CameraX shows the back camera in a <see cref="PreviewView"/> and ML Kit reads the text
/// of at most ~2 frames per second, one at a time (frames arriving meanwhile are dropped). The words inside the guide
/// go to the callback in guide-relative pixels; the next frame is read only after the callback's task completes.
/// Bound to the activity's lifecycle, so CameraX also closes the camera while the app is in the background.
/// Must be used from the main thread.
/// </summary>
public sealed class LiveCardCamera
{
    private const long ReadIntervalMs = 500;

    private readonly PreviewView _view;
    private readonly Func<IReadOnlyList<OcrLineDto>, bool, Task> _onText;
    private readonly ITextRecognizer _recognizer = TextRecognition.GetClient(TextRecognizerOptions.DefaultOptions);
    private readonly Java.Util.Concurrent.IExecutorService _executor = Java.Util.Concurrent.Executors.NewSingleThreadExecutor()!;
    private readonly Analyzer _analyzer;

    private ProcessCameraProvider? _provider;
    private ICamera? _camera;
    private Preview? _preview;
    private ImageAnalysis? _analysis;
    private (int Width, int Height, int Rotation) _boundFor;

    // Shared with the analysis thread.
    private volatile GuideLayout? _layout;
    private volatile bool _busy;
    private volatile bool _stopped;
    private int _forceNext;
    private long _lastReadAt;

    /// <param name="view">The preview (FILL_CENTER).</param>
    /// <param name="onText">Gets the grouped lines inside the guide (may be empty) and whether the read was forced by
    /// <see cref="ReadNextFrame"/>. Called on a background thread.</param>
    public LiveCardCamera(PreviewView view, Func<IReadOnlyList<OcrLineDto>, bool, Task> onText)
    {
        _view = view;
        _onText = onText;
        _analyzer = new Analyzer(Analyze);
        _view.LayoutChange += OnViewLayoutChange;
    }

    /// <summary>The device has a back camera (the live scanner doesn't use the front one).</summary>
    public static bool HasBackCamera =>
        Microsoft.Maui.ApplicationModel.Platform.AppContext.PackageManager?.HasSystemFeature(PackageManager.FeatureCamera) == true;

    /// <summary>Raised on the main thread after the camera was (re)bound, e.g. to show the torch button.</summary>
    public event Action? Bound;

    /// <summary>Raised on the main thread when the camera can't be opened.</summary>
    public event Action<Exception>? Failed;

    public bool HasFlashUnit => _camera?.CameraInfo.HasFlashUnit == true;

    public async Task StartAsync()
    {
        var provider = await GetProviderAsync(_view.Context!);
        if (_stopped) return;
        _provider = provider;
        EnsureBound();
    }

    /// <summary>The guide's position over the preview; <paramref name="view"/> and <paramref name="guide"/> in the same unit.</summary>
    public void SetGuide(Size view, Rect guide) =>
        _layout = view.Width > 0 && view.Height > 0 && guide.Width > 0 && guide.Height > 0 ? new GuideLayout(view, guide) : null;

    /// <summary>Reads the next frame right away, even within the interval (shutter).</summary>
    public void ReadNextFrame() => Interlocked.Exchange(ref _forceNext, 1);

    /// <summary>Switches the torch; asks the camera for its current state, which resets when the camera reopens.</summary>
    public void ToggleTorch()
    {
        if (_camera is not { } camera || !camera.CameraInfo.HasFlashUnit) return;
        var on = (camera.CameraInfo.TorchState.Value as Java.Lang.Integer)?.IntValue() == TorchState.On;
        camera.CameraControl.EnableTorch(!on);
    }

    /// <summary>Releases the camera and the recognizer. A stopped instance can't be restarted.</summary>
    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        _view.LayoutChange -= OnViewLayoutChange;
        _analysis?.ClearAnalyzer();
        if (_provider is not null && _preview is not null && _analysis is not null) _provider.Unbind([_preview, _analysis]);
        _camera = null;
        _recognizer.Close();
        _executor.Shutdown();
    }

    private void OnViewLayoutChange(object? sender, Android.Views.View.LayoutChangeEventArgs e) =>
        // Not during the layout pass itself; rebinding changes the preview's surface.
        _view.Post(EnsureBound);

    /// <summary>
    /// Binds once the preview has a size (the viewport needs it), and again when its size or the display rotation
    /// changed (device rotated): the viewport and the analysis' target rotation must match what is on screen.
    /// </summary>
    private void EnsureBound()
    {
        if (_stopped || _provider is null || _view.Width == 0 || _view.Height == 0) return;
        var rotation = (int)(_view.Display?.Rotation ?? SurfaceOrientation.Rotation0);
        var key = (_view.Width, _view.Height, rotation);
        if (_camera is not null && key == _boundFor) return;

        try
        {
            Bind(rotation);
            _boundFor = key;
            Bound?.Invoke();
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex);
        }
    }

    private void Bind(int rotation)
    {
        var owner = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity as ILifecycleOwner
                    ?? throw new InvalidOperationException("The camera needs the app to be in the foreground.");

        // Both streams 16:9, so they show the same field of view even when there is no viewport (see ScanGeometry).
        // Continuous autofocus is CameraX's default for a preview.
        var preview = new Preview.Builder()
            .SetResolutionSelector(new ResolutionSelector.Builder()
                .SetAspectRatioStrategy(AspectRatioStrategy.Ratio169FallbackAutoStrategy)
                .Build())
            .SetTargetRotation(rotation)
            .Build();
        preview.SurfaceProvider = _view.SurfaceProvider;

        var analysis = new ImageAnalysis.Builder()
            .SetResolutionSelector(new ResolutionSelector.Builder()
                .SetAspectRatioStrategy(AspectRatioStrategy.Ratio169FallbackAutoStrategy)
                // With the card in the guide, 1080p gives a title ~40 px high; at 720p the collector number line
                // (set code + number, for the exact printing) gets too small to read.
                .SetResolutionStrategy(new ResolutionStrategy(new Android.Util.Size(1920, 1080),
                    ResolutionStrategy.FallbackRuleClosestLowerThenHigher))
                .Build())
            .SetBackpressureStrategy(ImageAnalysis.StrategyKeepOnlyLatest)
            .SetTargetRotation(rotation)
            .Build();
        analysis.SetAnalyzer(_executor, _analyzer);

        var group = new UseCaseGroup.Builder().AddUseCase(preview).AddUseCase(analysis);
        // The viewport gives every stream the crop rect that the preview shows (FILL_CENTER of this view), so the
        // analysis frame's CropRect is exactly what the user sees.
        if (_view.GetViewPort(rotation) is { } viewPort) group.SetViewPort(viewPort);

        if (_preview is not null && _analysis is not null)
        {
            _analysis.ClearAnalyzer();
            _provider!.Unbind([_preview, _analysis]);
        }

        _preview = preview;
        _analysis = analysis;
        _camera = _provider!.BindToLifecycle(owner, CameraSelector.DefaultBackCamera, group.Build());
    }

    /// <summary>Analysis thread. With KEEP_ONLY_LATEST CameraX delivers the next frame only after this one is closed.</summary>
    private void Analyze(IImageProxy frame)
    {
        var forced = Volatile.Read(ref _forceNext) == 1;
        var now = Environment.TickCount64;
        if (_stopped || _busy || _layout is null || (!forced && now - _lastReadAt < ReadIntervalMs))
        {
            frame.Close();
            return;
        }

        if (forced) Interlocked.Exchange(ref _forceNext, 0);
        _busy = true;
        _lastReadAt = now;
        _ = ReadAsync(frame, forced);
    }

    private async Task ReadAsync(IImageProxy frame, bool forced)
    {
        IReadOnlyList<OcrLineDto>? lines = null;
        try
        {
            var layout = _layout!;
            var rotation = frame.ImageInfo.RotationDegrees;
            var crop = frame.CropRect;
            var guide = ScanGeometry.GuideInFrame(layout.View, layout.Guide, new Size(frame.Width, frame.Height),
                new Rect(crop.Left, crop.Top, crop.Width(), crop.Height()), rotation);

            if (frame.Image is { } image)
            {
                // ML Kit applies the rotation itself and reports boxes in the upright frame.
                var text = await _recognizer.Process(InputImage.FromMediaImage(image, rotation)).AsAsync<MlText>();
                var words = new List<OcrLineDto>();
                foreach (var block in text.TextBlocks)
                foreach (var line in block.Lines)
                foreach (var element in line.Elements)
                {
                    if (element.BoundingBox is { } box)
                        words.Add(new OcrLineDto(element.Text, box.Left, box.Top, box.Width(), box.Height()));
                }

                lines = OcrLineGrouper.Group(ScanGeometry.ToGuideSpace(words, guide));
            }
        }
        catch (Exception ex)
        {
            // One unreadable frame (or the recognizer closing as the page closes): just take the next one.
            System.Diagnostics.Debug.WriteLine($"Live scan: OCR failed: {ex.Message}");
        }
        finally
        {
            // Only now: ML Kit reads the image asynchronously.
            frame.Close();
        }

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

    private static Task<ProcessCameraProvider> GetProviderAsync(Android.Content.Context context)
    {
        var future = ProcessCameraProvider.GetInstance(context);
        var tcs = new TaskCompletionSource<ProcessCameraProvider>();
        future.AddListener(new Java.Lang.Runnable(() =>
        {
            try
            {
                tcs.TrySetResult(future.Get().JavaCast<ProcessCameraProvider>()!);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }), ContextCompat.GetMainExecutor(context));
        return tcs.Task;
    }

    private sealed record GuideLayout(Size View, Rect Guide);

    private sealed class Analyzer(Action<IImageProxy> analyze) : Java.Lang.Object, ImageAnalysis.IAnalyzer
    {
        public void Analyze(IImageProxy image) => analyze(image);
    }
}
