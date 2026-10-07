namespace O2CloudDrive.Startup;

public enum AutoMountStatus
{
    Disabled,
    SessionNotValidated,
    AlreadyMounted,
    AlreadyAttempted,
    Mounted,
    Failed,
}

public sealed record AutoMountResult(AutoMountStatus Status, string? ErrorType = null);

/// <summary>Guards automatic mount requests against duplicate startup/login callbacks.</summary>
public sealed class AutoMountCoordinator
{
    private int _attemptedForSession;

    public async Task<AutoMountResult> TryMountAsync(
        bool enabled,
        bool sessionValidated,
        bool alreadyMounted,
        Func<Task<bool>> mount)
    {
        ArgumentNullException.ThrowIfNull(mount);

        if (!enabled)
        {
            return new AutoMountResult(AutoMountStatus.Disabled);
        }

        if (!sessionValidated)
        {
            return new AutoMountResult(AutoMountStatus.SessionNotValidated);
        }

        if (alreadyMounted)
        {
            return new AutoMountResult(AutoMountStatus.AlreadyMounted);
        }

        if (Interlocked.CompareExchange(ref _attemptedForSession, 1, 0) != 0)
        {
            return new AutoMountResult(AutoMountStatus.AlreadyAttempted);
        }

        try
        {
            return await mount().ConfigureAwait(false)
                ? new AutoMountResult(AutoMountStatus.Mounted)
                : new AutoMountResult(AutoMountStatus.Failed, "MountOperationFailed");
        }
        catch (Exception exception)
        {
            return new AutoMountResult(AutoMountStatus.Failed, exception.GetType().Name);
        }
    }

    public void ResetForNewSession()
    {
        Interlocked.Exchange(ref _attemptedForSession, 0);
    }
}
