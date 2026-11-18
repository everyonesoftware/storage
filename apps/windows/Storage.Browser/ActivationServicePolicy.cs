namespace Storage.Browser;

// This is only local process-start policy, never application authorization.
internal sealed class ActivationServicePolicy(bool fromTray)
{
    private bool suppressAutoStart = fromTray;
    public bool CanAutoStart(bool alreadyLaunched, bool hostPresent, bool executableExists) =>
        !suppressAutoStart && !alreadyLaunched && !hostPresent && executableExists;
    public void SuppressAutoStart() => suppressAutoStart = true;
}
