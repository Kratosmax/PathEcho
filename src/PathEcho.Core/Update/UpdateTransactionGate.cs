namespace PathEcho.Core.Update;

public sealed class UpdateTransactionGate : IDisposable
{
    public const string UpdaterMutexName = "Local\\PathEcho.UpdateInstaller";

    private readonly ManualResetEventSlim _stop = new();
    private readonly ManualResetEventSlim _acquisitionCompleted = new();
    private readonly Thread _ownerThread;
    private bool _acquired;
    private bool _disposed;

    private UpdateTransactionGate(string name, TimeSpan timeout)
    {
        var initialized = new ManualResetEventSlim();
        Exception? ownerException = null;
        _ownerThread = new Thread(() =>
        {
            try
            {
                using var mutex = new Mutex(false, name);
                initialized.Set();
                try
                {
                    var index = WaitHandle.WaitAny(
                        new[] { mutex, _stop.WaitHandle },
                        timeout == Timeout.InfiniteTimeSpan ? Timeout.InfiniteTimeSpan : timeout);
                    _acquired = index == 0;
                }
                catch (AbandonedMutexException exception) when (exception.MutexIndex == 0)
                {
                    _acquired = true;
                }

                _acquisitionCompleted.Set();
                if (_acquired)
                {
                    _stop.Wait();
                    mutex.ReleaseMutex();
                }
            }
            catch (Exception exception)
            {
                ownerException = exception;
                initialized.Set();
                _acquisitionCompleted.Set();
            }
        })
        {
            IsBackground = true,
            Name = "PathEcho update transaction owner",
        };
        _ownerThread.Start();
        initialized.Wait();
        initialized.Dispose();
        if (ownerException is not null)
        {
            Dispose();
            throw new InvalidOperationException("无法初始化更新事务锁。", ownerException);
        }
    }

    public bool IsAcquired
    {
        get
        {
            _acquisitionCompleted.Wait();
            return _acquired;
        }
    }

    public static UpdateTransactionGate BeginAcquire(string name, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        return new UpdateTransactionGate(name, timeout);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop.Set();
        _ownerThread.Join();
        _acquisitionCompleted.Dispose();
        _stop.Dispose();
    }
}
