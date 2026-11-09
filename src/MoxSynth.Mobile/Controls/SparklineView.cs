namespace MoxSynth.Mobile.Controls;

/// <summary>Small price history line (days without a price keep the previous price).</summary>
public sealed class SparklineView : GraphicsView
{
    public static readonly BindableProperty ValuesProperty = BindableProperty.Create(
        nameof(Values), typeof(IReadOnlyList<double?>), typeof(SparklineView), null,
        propertyChanged: (b, _, _) => ((SparklineView)b).Invalidate());

    public static readonly BindableProperty LineColorProperty = BindableProperty.Create(
        nameof(LineColor), typeof(Color), typeof(SparklineView), Colors.Gray,
        propertyChanged: (b, _, _) => ((SparklineView)b).Invalidate());

    public SparklineView()
    {
        Drawable = new SparklineDrawable(this);
        InputTransparent = true;
    }

    public IReadOnlyList<double?>? Values
    {
        get => (IReadOnlyList<double?>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Color LineColor
    {
        get => (Color)GetValue(LineColorProperty);
        set => SetValue(LineColorProperty, value);
    }

    private sealed class SparklineDrawable(SparklineView view) : IDrawable
    {
        public void Draw(ICanvas canvas, RectF rect)
        {
            var values = view.Values;
            if (values is null || values.Count < 2) return;

            var filled = new double?[values.Count];
            double? last = null;
            for (var i = 0; i < values.Count; i++)
            {
                if (values[i] is { } v) last = v;
                filled[i] = last;
            }
            var known = filled.Where(v => v is not null).Select(v => v!.Value).ToList();
            if (known.Count < 2) return;
            double min = known.Min(), max = known.Max(), range = max - min;

            var path = new PathF();
            var started = false;
            for (var i = 0; i < filled.Length; i++)
            {
                if (filled[i] is not { } v) continue;
                var x = rect.Left + 1 + i * (rect.Width - 2) / (filled.Length - 1);
                var y = range < 1e-9
                    ? rect.Center.Y
                    : rect.Bottom - 2 - (float)((v - min) / range) * (rect.Height - 4);
                if (!started) path.MoveTo(x, y);
                else path.LineTo(x, y);
                started = true;
            }

            canvas.StrokeColor = view.LineColor;
            canvas.StrokeSize = 1.5f;
            canvas.StrokeLineJoin = LineJoin.Round;
            canvas.DrawPath(path);
        }
    }
}
