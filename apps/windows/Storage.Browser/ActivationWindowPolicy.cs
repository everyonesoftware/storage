namespace Storage.Browser;

internal static class ActivationWindowPolicy
{
    public static void BringForward(bool minimized, Action restore, Action activate, Action foreground)
    {
        if (minimized) restore();
        activate();
        foreground();
    }
}
