using Foundation;

namespace MoxSynth.Mobile;

/// <summary>
/// Scene-based startup (UIScene life cycle), which apps built with the iOS 27 SDK must use; registered in Info.plist
/// under UIApplicationSceneManifest.
/// </summary>
[Register("SceneDelegate")]
public class SceneDelegate : MauiUISceneDelegate
{
}
