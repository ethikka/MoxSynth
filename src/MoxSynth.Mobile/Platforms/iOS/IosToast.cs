using UIKit;

namespace MoxSynth.Mobile.Services;

/// <summary>A short message near the bottom of the screen that fades away (iOS has no built-in toast).</summary>
internal static class IosToast
{
    private const double VisibleSeconds = 2;

    private static UIView? _current;

    /// <summary>Main thread only.</summary>
    public static void Show(string message)
    {
        var window = UIApplication.SharedApplication.ConnectedScenes
            .OfType<UIWindowScene>()
            .SelectMany(s => s.Windows)
            .FirstOrDefault(w => w.IsKeyWindow);
        if (window is null) return;

        _current?.RemoveFromSuperview();

        var label = new UILabel
        {
            Text = message,
            TextColor = UIColor.White,
            Font = UIFont.SystemFontOfSize(14)!,
            Lines = 0,
            TextAlignment = UITextAlignment.Center,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        var toast = new UIView
        {
            BackgroundColor = UIColor.FromWhiteAlpha(0.1f, 0.88f),
            Alpha = 0,
            TranslatesAutoresizingMaskIntoConstraints = false,
            UserInteractionEnabled = false,
        };
        toast.Layer.CornerRadius = 18;
        toast.AddSubview(label);
        window.AddSubview(toast);

        var guide = window.SafeAreaLayoutGuide;
        NSLayoutConstraint.ActivateConstraints(
        [
            label.LeadingAnchor.ConstraintEqualTo(toast.LeadingAnchor, 16),
            label.TrailingAnchor.ConstraintEqualTo(toast.TrailingAnchor, -16),
            label.TopAnchor.ConstraintEqualTo(toast.TopAnchor, 10),
            label.BottomAnchor.ConstraintEqualTo(toast.BottomAnchor, -10),
            toast.CenterXAnchor.ConstraintEqualTo(guide.CenterXAnchor),
            toast.BottomAnchor.ConstraintEqualTo(guide.BottomAnchor, -72), // above the tab bar
            toast.WidthAnchor.ConstraintLessThanOrEqualTo(guide.WidthAnchor, 1, -48),
        ]);
        _current = toast;

        UIView.Animate(0.2, () => toast.Alpha = 1, () =>
            UIView.Animate(0.3, VisibleSeconds, UIViewAnimationOptions.CurveEaseIn, () => toast.Alpha = 0, () =>
            {
                toast.RemoveFromSuperview();
                if (_current == toast) _current = null;
            }));
    }
}
