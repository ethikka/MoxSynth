using System.ComponentModel;
using AndroidX.Camera.View;
using MoxSynth.Client.Services;
using MoxSynth.Contracts;
using MoxSynth.Mobile.Services;

namespace MoxSynth.Mobile.Pages;

/// <summary>
/// Full-screen live scanner: reads the text inside the card guide continuously and asks the server (the callback given
/// to <see cref="Start"/>) until it recognises a card. The shutter forces a read of the next frame; the gallery button
/// hands over to the photo scanner. Closes itself; the task from <see cref="Start"/> completes after that.
/// </summary>
public partial class LiveScanPage : ContentPage
{
    private const string Reading = "Reading…";
    private const string ServerUnreachable = "Can't reach the server";
    /// <summary>After a failed call, automatic reads wait this long so an unreachable server isn't asked twice a second.</summary>
    private const long ErrorPauseMs = 2000;
    /// <summary>Space kept above and below the guide when its height (not the width) limits it.</summary>
    private const double GuideMargin = 8;

    private readonly TaskCompletionSource<LiveScanResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _closed = new();
    private Func<IReadOnlyList<OcrLineDto>, CancellationToken, Task<RecognizeResultDto>>? _recognize;
    private LiveCardCamera? _camera;
    private string? _lastUnrecognisedTitle;
    private long _pausedUntil;
    private bool _appeared;
    private bool _closing;

    public LiveScanPage()
    {
        InitializeComponent();
        Preview.HandlerChanged += (_, _) =>
        {
            if (_appeared) StartCamera();
        };
        // Position changes too, not only sizes: the guide moves when the rows around it change height.
        foreach (var element in new VisualElement[] { Preview, GuideArea, Guide })
            element.PropertyChanged += OnBoundsChanged;
    }

    /// <summary>Prepares a scan; push the page modally afterwards.</summary>
    /// <returns>Completes once the page has closed.</returns>
    public Task<LiveScanResult> Start(Func<IReadOnlyList<OcrLineDto>, CancellationToken, Task<RecognizeResultDto>> recognize,
        CancellationToken ct)
    {
        _recognize = recognize;
        if (ct.CanBeCanceled)
            ct.Register(() => MainThread.BeginInvokeOnMainThread(() => _ = CloseAsync(LiveScanResult.Cancelled)));
        return _result.Task;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _appeared = true;
        StartCamera();
    }

    protected override void OnDisappearing()
    {
        _appeared = false;
        StopCamera();
        base.OnDisappearing();
    }

    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync(LiveScanResult.Cancelled);
        return true;
    }

    private async void StartCamera()
    {
        if (_camera is not null || _closing || Preview.Handler?.PlatformView is not PreviewView view) return;

        var camera = new LiveCardCamera(view, OnTextReadAsync);
        camera.Bound += () => TorchButton.IsVisible = camera.HasFlashUnit;
        camera.Failed += error => _ = FailAsync(error);
        _camera = camera;
        UpdateGuide();
        try
        {
            await camera.StartAsync();
        }
        catch (Exception ex)
        {
            await FailAsync(ex);
        }
    }

    private void StopCamera()
    {
        _camera?.Stop();
        _camera = null;
    }

    private void OnBoundsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(X) or nameof(Y) or nameof(Width) or nameof(Height)) UpdateGuide();
    }

    /// <summary>Sizes the guide and tells the camera where it lies over the preview (which covers the whole page).</summary>
    private void UpdateGuide()
    {
        if (Preview.Width <= 0 || Preview.Height <= 0 || GuideArea.Height <= 0) return;

        var size = ScanGeometry.GuideSize(Preview.Width, GuideArea.Height - 2 * GuideMargin);
        if (Math.Abs(Guide.WidthRequest - size.Width) > 0.5 || Math.Abs(Guide.HeightRequest - size.Height) > 0.5)
        {
            // Laid out later; its new bounds come back through OnBoundsChanged.
            Guide.WidthRequest = size.Width;
            Guide.HeightRequest = size.Height;
        }

        if (Guide.Width <= 0 || Guide.Height <= 0) return;
        _camera?.SetGuide(new Size(Preview.Width, Preview.Height),
            new Rect(GuideArea.X + Guide.X, GuideArea.Y + Guide.Y, Guide.Width, Guide.Height));
    }

    /// <summary>From the camera's analysis thread; the camera waits for the returned task before it reads again.</summary>
    private Task OnTextReadAsync(IReadOnlyList<OcrLineDto> lines, bool forced) =>
        MainThread.InvokeOnMainThreadAsync(() => RecognizeAsync(lines, forced));

    private async Task RecognizeAsync(IReadOnlyList<OcrLineDto> lines, bool forced)
    {
        if (_closing || _recognize is null) return;

        // The top line is where the card name is: the same reading as last time would get the same "no".
        var title = lines.Count > 0 ? lines[0].Text.Trim() : "";
        if (!forced && (lines.Count == 0 || Environment.TickCount64 < _pausedUntil
                                         || string.Equals(title, _lastUnrecognisedTitle, StringComparison.OrdinalIgnoreCase)))
            return;
        if (lines.Count == 0)
        {
            // Shutter, but nothing readable in the guide: nothing the server could match either.
            StateLabel.Text = NotRecognised("");
            return;
        }

        StateLabel.Text = Reading;
        try
        {
            var result = await _recognize(lines, _closed.Token);
            if (_closing) return;
            if (result.Found)
            {
                await CloseAsync(LiveScanResult.Found(result));
                return;
            }

            _lastUnrecognisedTitle = title;
            StateLabel.Text = forced ? NotRecognised(result.TitleText) : "";
        }
        catch (Exception) when (_closing)
        {
            // Closed while the call was running (cancelled).
        }
        catch (Exception)
        {
            StateLabel.Text = ServerUnreachable;
            _pausedUntil = Environment.TickCount64 + ErrorPauseMs;
        }
    }

    private static string NotRecognised(string? titleText) => $"Not recognised – read as \"{titleText}\"";

    private void OnCloseTapped(object? sender, TappedEventArgs e) => _ = CloseAsync(LiveScanResult.Cancelled);

    private void OnGalleryTapped(object? sender, TappedEventArgs e) => _ = CloseAsync(LiveScanResult.Gallery);

    private void OnTorchTapped(object? sender, TappedEventArgs e) => _camera?.ToggleTorch();

    private void OnShutterTapped(object? sender, TappedEventArgs e)
    {
        if (_closing || _camera is null) return;
        StateLabel.Text = Reading;
        _camera.ReadNextFrame();
    }

    private async Task CloseAsync(LiveScanResult result)
    {
        if (!BeginClose()) return;
        await PopAsync();
        _result.TrySetResult(result);
    }

    /// <summary>The camera couldn't start: close and let the caller report it.</summary>
    private async Task FailAsync(Exception error)
    {
        if (!BeginClose()) return;
        await PopAsync();
        _result.TrySetException(error);
    }

    private bool BeginClose()
    {
        if (_closing) return false;
        _closing = true;
        _closed.Cancel(); // aborts a running recognize call
        StopCamera();
        return true;
    }

    private async Task PopAsync()
    {
        try
        {
            // Before completing the result: the caller may navigate next (open the card), which needs the modal gone.
            if (Navigation.ModalStack.Contains(this)) await Navigation.PopModalAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Live scan: closing failed: {ex.Message}");
        }
    }
}
