namespace DashboardAPI.Services;

/// <summary>
/// Per-directory semaphore that serializes Playwright persistent-context usage.
/// Playwright locks the user-data directory, so two concurrent callers on the
/// same directory would throw "User data directory is already in use".
/// </summary>
internal static class PlaywrightDirLock
{
    private static readonly Dictionary<string, SemaphoreSlim> _locks = new();
    private static readonly object _sync = new();

    public static SemaphoreSlim For(string dirName)
    {
        lock (_sync)
        {
            if (!_locks.TryGetValue(dirName, out var sem))
                _locks[dirName] = sem = new SemaphoreSlim(1, 1);
            return sem;
        }
    }
}
