namespace WinTube.App;

/// Bridges MainWindow's root PointerPressed handler (the only place an X-button press is ever
/// seen — see MainWindow's mouse-back-button comment for why: WinUI 3's lifted input travels the
/// compositor's own channel, not a Win32 message queue a WndProc could intercept) to the various
/// Tapped handlers across VideoCard and PlayerPage that WinUI itself still raises for an X-button
/// press — a known WinUI behavior: the side buttons raise Tapped but never Click, so a Button is
/// naturally immune but a Tapped-driven "card" or video-surface handler isn't. Without this, mouse
/// back both navigates back (root PointerPressed) AND activates whatever was under the pointer
/// (the resulting Tapped).
internal static class MouseBackGuard
{
    private static readonly TimeSpan SuppressWindow = TimeSpan.FromMilliseconds(400);

    private static DateTime lastXButtonPress = DateTime.MinValue;

    /// True once a non-X button has pressed since the last X-button press — set back to false on
    /// the next X-button press. Guards against an X press, then a deliberate left-click shortly
    /// after (e.g. mouse-back, then immediately clicking a different card) being suppressed too.
    private static bool clearedByOtherPress;

    /// Called from MainWindow's root PointerPressed for an XButton1/XButton2 press.
    public static void RecordXButtonPress()
    {
        lastXButtonPress = DateTime.UtcNow;
        clearedByOtherPress = false;
    }

    /// Called from the same handler for any other button (left/right/middle).
    public static void RecordOtherButtonPress() => clearedByOtherPress = true;

    /// Checked at the top of every Tapped-driven activation handler that an X-button press can
    /// reach; true means "this Tapped is really the tail end of an X-button press — ignore it".
    public static bool SuppressTap() =>
        !clearedByOtherPress && DateTime.UtcNow - lastXButtonPress < SuppressWindow;
}
