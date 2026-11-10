using MoxSynth.Contracts;

namespace MoxSynth.Mobile.Services;

/// <summary>
/// Geometry of the live scanner: the card guide's size, and where the guide (drawn over the preview) lies in the
/// camera frame that ML Kit reports its boxes in. Pure functions without Android types, because this is where
/// rotation and scaling mistakes would hide.
/// </summary>
public static class ScanGeometry
{
    /// <summary>A Magic card is 63 × 88 mm (width / height).</summary>
    public const double CardAspect = 63.0 / 88.0;

    /// <summary>
    /// Card-shaped guide: <paramref name="widthFraction"/> of the screen width, but never taller than the space it is
    /// centred in (landscape, small screens).
    /// </summary>
    public static Size GuideSize(double screenWidth, double availableHeight, double widthFraction = 0.75)
    {
        var width = Math.Min(screenWidth * widthFraction, availableHeight * CardAspect);
        return width > 0 ? new Size(width, width / CardAspect) : Size.Zero;
    }

    /// <summary>
    /// Turns a rectangle from camera-buffer pixels (sensor orientation) into the upright frame: the buffer rotated
    /// clockwise by <paramref name="rotationDegrees"/>. That is the frame ML Kit reports its boxes in for
    /// <c>InputImage.FromMediaImage(image, rotationDegrees)</c>.
    /// </summary>
    public static Rect ToUpright(Rect rect, double bufferWidth, double bufferHeight, int rotationDegrees) =>
        Normalize(rotationDegrees) switch
        {
            0 => rect,
            // A buffer point (x, y) ends up at (h − y, x); the upright frame is h wide and w high.
            90 => new Rect(bufferHeight - rect.Bottom, rect.Left, rect.Height, rect.Width),
            // (x, y) → (w − x, h − y)
            180 => new Rect(bufferWidth - rect.Right, bufferHeight - rect.Bottom, rect.Width, rect.Height),
            // (x, y) → (y, w − x); the upright frame is h wide and w high.
            270 => new Rect(rect.Top, bufferWidth - rect.Right, rect.Height, rect.Width),
            _ => throw new ArgumentOutOfRangeException(nameof(rotationDegrees), rotationDegrees, "Must be a multiple of 90."),
        };

    /// <summary>
    /// Maps the guide from view coordinates (relative to the preview, any unit) to upright frame pixels.
    /// The preview shows the frame's crop rect (the CameraX viewport, or the whole buffer without one) scaled to cover
    /// the view and centred (PreviewView FILL_CENTER), so view point p shows frame point visibleTopLeft + p / scale.
    /// </summary>
    /// <param name="view">Size of the preview view.</param>
    /// <param name="guide">The guide, relative to the preview view's top left.</param>
    /// <param name="buffer">Size of the analysis buffer (<c>ImageProxy.Width/Height</c>, sensor orientation).</param>
    /// <param name="cropRect">The buffer's crop rect (<c>ImageProxy.CropRect</c>), in buffer pixels.</param>
    /// <param name="rotationDegrees">Clockwise rotation that makes the buffer upright (<c>ImageInfo.RotationDegrees</c>).</param>
    public static Rect GuideInFrame(Size view, Rect guide, Size buffer, Rect cropRect, int rotationDegrees)
    {
        var crop = ToUpright(cropRect, buffer.Width, buffer.Height, rotationDegrees);
        // FILL_CENTER: the larger of the two scales, so the crop covers the whole view; the overflow is cut off evenly.
        var scale = Math.Max(view.Width / crop.Width, view.Height / crop.Height);
        var visibleLeft = crop.Center.X - view.Width / scale / 2;
        var visibleTop = crop.Center.Y - view.Height / scale / 2;
        return new Rect(visibleLeft + guide.X / scale, visibleTop + guide.Y / scale, guide.Width / scale, guide.Height / scale);
    }

    /// <summary>
    /// Keeps the words whose centre lies inside the guide and moves them to guide-relative pixels (origin at the guide's
    /// top left, upright), so the server sees the same layout as from a photo cropped to the card.
    /// </summary>
    public static List<OcrLineDto> ToGuideSpace(IEnumerable<OcrLineDto> words, Rect guide) =>
        words.Where(w => guide.Contains(w.X + w.Width / 2, w.Y + w.Height / 2))
            .Select(w => w with { X = w.X - guide.X, Y = w.Y - guide.Y })
            .ToList();

    private static int Normalize(int degrees) => (degrees % 360 + 360) % 360;
}
