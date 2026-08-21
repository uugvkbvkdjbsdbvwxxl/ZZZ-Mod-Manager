namespace ZZZModManager.Infrastructure;

public sealed class SingleInstanceCoordinator : IDisposable
{
    private const string DefaultName = "ZZZModManager.SingleInstance.v1";

    private readonly string _mutexName;
    private readonly string _activationEventName;
    private Mutex? _mutex;
    private EventWaitHandle? _activationEvent;
    private CancellationTokenSource? _listenerCancellation;
    private Task? _listenerTask;
    private bool _ownsMutex;
    private bool _disposed;

    public SingleInstanceCoordinator(string? name = null)
    {
        var instanceName = string.IsNullOrWhiteSpace(name) ? DefaultName : name;
        _mutexName = $"Local\\{instanceName}.Mutex";
        _activationEventName = $"Local\\{instanceName}.Activate";
    }

    public bool TryAcquire()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_mutex is not null || _ownsMutex)
        {
            throw new InvalidOperationException("Single-instance ownership has already been checked.");
        }

        _mutex = new Mutex(initiallyOwned: true, _mutexName, out var createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
            return false;
        }

        _ownsMutex = true;
        _activationEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            _activationEventName);
        return true;
    }

    public void SignalPrimary()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var activationEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            _activationEventName);
        activationEvent.Set();
    }

    public void StartListening(Action activationRequested)
    {
        ArgumentNullException.ThrowIfNull(activationRequested);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_ownsMutex || _activationEvent is null)
        {
            throw new InvalidOperationException("The primary instance must be acquired before listening.");
        }

        if (_listenerTask is not null)
        {
            throw new InvalidOperationException("The activation listener has already been started.");
        }

        _listenerCancellation = new CancellationTokenSource();
        var cancellation = _listenerCancellation;
        var activationEvent = _activationEvent;
        _listenerTask = Task.Run(() =>
        {
            var handles = new WaitHandle[] { activationEvent, cancellation.Token.WaitHandle };
            while (WaitHandle.WaitAny(handles) == 0)
            {
                activationRequested();
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _listenerCancellation?.Cancel();
        _activationEvent?.Set();
        try
        {
            _listenerTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The process is exiting; cancellation-related listener errors are not actionable.
        }

        _listenerCancellation?.Dispose();
        _activationEvent?.Dispose();
        if (_ownsMutex && _mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Ownership can already be released during abnormal application shutdown.
            }
        }

        _mutex?.Dispose();
        _ownsMutex = false;
    }
}
