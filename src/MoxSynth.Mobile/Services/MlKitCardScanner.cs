using MoxSynth.Client.Services;
using MoxSynth.Contracts;
using MoxSynth.Mobile.Pages;
using Plugin.Maui.OCR;

namespace MoxSynth.Mobile.Services;

/// <summary>
/// Reads cards on the device (Google ML Kit on Android, Apple Vision on iOS): live in the in-app camera view (<see cref="LiveScanPage"/>), or from a
/// photo picked from the gallery or taken with the camera app (Plugin.Maui.OCR). Only the recognised text + positions
/// go to the server, never the picture.
/// </summary>
public sealed class MlKitCardScanner(IOcrService ocr, IServiceProvider services) : ICardScanner
{
    private bool _initialized;

    public async Task<LiveScanResult> ScanLiveAsync(
        Func<IReadOnlyList<OcrLineDto>, CancellationToken, Task<RecognizeResultDto>> recognize, CancellationToken ct = default)
    {
        if (!LiveCardCamera.HasBackCamera)
            throw new InvalidOperationException("This device has no camera.");

        if (await Permissions.RequestAsync<Permissions.Camera>() != PermissionStatus.Granted)
            throw new InvalidOperationException("MoxSynth needs camera permission to scan cards.");

        var page = services.GetRequiredService<LiveScanPage>();
        var result = page.Start(recognize, ct);
        await MainThread.InvokeOnMainThreadAsync(() => Shell.Current.Navigation.PushModalAsync(page));
        return await result;
    }

    public async Task<IReadOnlyList<OcrLineDto>?> ScanAsync(bool fromGallery, CancellationToken ct = default)
    {
        if (!fromGallery && !MediaPicker.Default.IsCaptureSupported)
            throw new InvalidOperationException("This device has no camera.");

        if (!fromGallery && await Permissions.RequestAsync<Permissions.Camera>() != PermissionStatus.Granted)
            throw new InvalidOperationException("MoxSynth needs camera permission to scan cards.");

        // Upright (EXIF rotation applied) and not larger than needed: the recognisers don't read the EXIF orientation,
        // and a card title is easily readable at ~1600 px.
        var options = new MediaPickerOptions
        {
            Title = "Scan a card",
            MaximumWidth = 1600,
            MaximumHeight = 1600,
            CompressionQuality = 90,
            RotateImage = true,
        };
        var photo = fromGallery
            ? (await MediaPicker.PickPhotosAsync(options)).FirstOrDefault()
            : await MediaPicker.Default.CapturePhotoAsync(options);
        if (photo is null) return null;

        byte[] bytes;
        await using (var stream = await photo.OpenReadAsync())
        using (var memory = new MemoryStream())
        {
            await stream.CopyToAsync(memory, ct);
            bytes = memory.ToArray();
        }

        if (!_initialized)
        {
            await ocr.InitAsync(ct);
            _initialized = true;
        }

        var result = await ocr.RecognizeTextAsync(bytes, tryHard: true, ct);
        if (!result.Success) return [];

        var words = result.Elements
            .Select(e => new OcrLineDto(e.Text, e.X, e.Y, e.Width, e.Height))
            .ToList();
        // No positions (shouldn't happen): fall back to the plain lines, top to bottom.
        if (words.Count == 0)
            return result.Lines.Select((text, i) => new OcrLineDto(text, 0, i * 20, 100, 18)).ToList();

        return OcrLineGrouper.Group(words);
    }
}
