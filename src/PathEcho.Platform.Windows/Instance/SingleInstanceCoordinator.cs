namespace PathEcho.Platform.Windows.Instance;

public sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly EventWaitHandle _activationEvent;
    private readonly ManualResetEventSlim _stopOwnerThread;
    private readonly Thread _ownerThread;
    private readonly bool _isPrimary;
    private RegisteredWaitHandle? _activationRegistration;
    private int _stopping;
    private bool _disposed;

    private SingleInstanceCoordinator(
        EventWaitHandle activationEvent,
        ManualResetEventSlim stopOwnerThread,
        Thread ownerThread,
        bool isPrimary,
        Action activate)
    {
        _activationEvent = activationEvent;
        _stopOwnerThread = stopOwnerThread;
        _ownerThread = ownerThread;
        _isPrimary = isPrimary;
        if (isPrimary)
        {
            _activationRegistration = ThreadPool.RegisterWaitForSingleObject(
                activationEvent,
                (_, timedOut) =>
                {
                    if (!timedOut && Volatile.Read(ref _stopping) == 0)
                    {
                        try
                        {
                            activate();
                        }
                        catch
                        {
                            // Activation is best-effort during shutdown; the primary instance remains authoritative.
                        }
                    }
                },
                null,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }
        else
        {
            activationEvent.Set();
        }
    }

    public bool IsPrimary => _isPrimary;

    public static SingleInstanceCoordinator Create(string name, Action activate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(activate);

        var activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, $"{name}.Activate");
        var ownerReady = new ManualResetEventSlim();
        var stopOwnerThread = new ManualResetEventSlim();
        var isPrimary = false;
        Exception? ownerException = null;
        var ownerThread = new Thread(() =>
        {
            try
            {
                using var mutex = new Mutex(false, name);
                try
                {
                    isPrimary = mutex.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    isPrimary = true;
                }

                ownerReady.Set();
                if (isPrimary)
                {
                    stopOwnerThread.Wait();
                    mutex.ReleaseMutex();
                }
            }
            catch (Exception exception)
            {
                ownerException = exception;
                ownerReady.Set();
            }
        })
        {
            IsBackground = true,
            Name = "PathEcho single-instance owner",
        };
        ownerThread.Start();
        ownerReady.Wait();
        ownerReady.Dispose();
        if (ownerException is not null)
        {
            stopOwnerThread.Dispose();
            activationEvent.Dispose();
            throw new InvalidOperationException("无法初始化 PathEcho 单实例锁。", ownerException);
        }

        return new SingleInstanceCoordinator(
            activationEvent,
            stopOwnerThread,
            ownerThread,
            isPrimary,
            activate);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Volatile.Write(ref _stopping, 1);
        _activationRegistration?.Unregister(null);
        _activationRegistration = null;
        _activationEvent.Dispose();
        _stopOwnerThread.Set();
        _ownerThread.Join();
        _stopOwnerThread.Dispose();
    }
}
