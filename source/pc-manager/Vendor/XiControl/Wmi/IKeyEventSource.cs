namespace XiControl.Wmi;

/// <summary>
/// Источник событий клавиш прошивки (code, value) — шов для тестов роутинга
/// клавиш без железа. Прод-реализация — MifsEventWatcher (WMI HID_EVENT20).
/// </summary>
public interface IKeyEventSource : IDisposable
{
    /// <summary>(code, value) при нажатии. Приходит на потоке пула — подписчик сам маршалит в UI.</summary>
    event Action<byte, byte>? KeyPressed;

    /// <summary>Начать слушать (безопасно, если класса событий нет — см. реализацию).</summary>
    void Start();
}
