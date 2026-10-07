using System.Threading;

namespace O2CloudDrive.Startup;

public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private int _disposed;

    private SingleInstanceGuard(Mutex mutex)
    {
        _mutex = mutex;
    }

    public static SingleInstanceGuard? TryAcquire(string mutexName = @"Local\O2CloudDrive")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);
        var mutex = new Mutex(initiallyOwned: false, mutexName);
        try
        {
            if (mutex.WaitOne(0))
            {
                return new SingleInstanceGuard(mutex);
            }
        }
        catch (AbandonedMutexException)
        {
            return new SingleInstanceGuard(mutex);
        }

        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // The current thread no longer owns the mutex.
        }
        finally
        {
            _mutex.Dispose();
        }
    }
}
