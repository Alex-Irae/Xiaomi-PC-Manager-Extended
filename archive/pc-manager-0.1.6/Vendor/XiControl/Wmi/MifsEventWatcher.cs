using System.Management;

namespace XiControl.Wmi;

/// <summary>
/// Подписка на события клавиш прошивки (HID_EVENT20). Отдаёт (code, value):
/// code = EventDetail[1], value = EventDetail[2]. События приходят на потоке пула —
/// подписчик сам маршалит в UI-поток. Переживает рестарт WMI-сервиса: при обрыве
/// потока событий переподключается с задержкой.
/// </summary>
public sealed class MifsEventWatcher : IKeyEventSource
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    private readonly ManagementEventWatcher _watcher;
    private readonly System.Threading.Timer _retry;
    private volatile bool _disposed;
    public bool Running { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>(code, value) при нажатии.</summary>
    public event Action<byte, byte>? KeyPressed;

    public MifsEventWatcher()
    {
        var scope = new ManagementScope(Mifs.Namespace);
        _watcher = new ManagementEventWatcher(scope, new WqlEventQuery($"SELECT * FROM {Mifs.EventClass}"));
        _watcher.EventArrived += OnArrived;
        _watcher.Stopped += OnStopped;
        _retry = new System.Threading.Timer(_ => Start());
    }

    public void Start()
    {
        if (_disposed) return;
        try
        {
            _watcher.Start();
            Running = true;
            LastError = null;
        }
        catch (Exception ex)
        {
            Log.Ex("MifsEventWatcher.Start", ex);
            Running = false;
            LastError = ex.Message;
            // если хоть раз стартовали — это обрыв (WMI перезапускается), пробуем снова;
            // если нет — класса событий скорее всего нет на этой машине, не спамим ретраями
            if (!_disposed) _retry.Change(RetryDelay, Timeout.InfiniteTimeSpan);
        }
    }

    // поток событий оборвался (например, рестарт winmgmt) — иначе клавиши молча умрут
    private void OnStopped(object sender, StoppedEventArgs e)
    {
        if (_disposed) return;
        Running = false;
        Log.Write("MifsEventWatcher: поток событий остановлен, переподключение");
        _retry.Change(RetryDelay, Timeout.InfiniteTimeSpan);
    }

    private void OnArrived(object sender, EventArrivedEventArgs e)
    {
        try
        {
            if (e.NewEvent["EventDetail"] is byte[] d && d.Length > 2)
                KeyPressed?.Invoke(d[1], d[2]);
        }
        catch { /* игнор битых событий */ }
    }

    public void Dispose()
    {
        _disposed = true;
        _retry.Dispose();
        try { _watcher.Stop(); } catch { /* WMI мог уже умереть при выходе — не критично */ }
        _watcher.Dispose();
    }
}
