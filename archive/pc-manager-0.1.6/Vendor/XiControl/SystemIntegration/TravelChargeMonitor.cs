using XiControl.Config;

namespace XiControl.SystemIntegration;

/// <summary>
/// Наблюдение режима «В дорогу»: пока режим активен и ноутбук на зарядке — опрос
/// батареи (раз в 5 с) до ровно 100%, затем колбэк Ready, один раз за сессию режима.
/// Только наблюдение: включение/выключение самого режима (конфиг + прошивка + OSD) —
/// дело вызывающего; тот после любой смены состояния зовёт Rearm().
/// </summary>
public sealed class TravelChargeMonitor : IDisposable
{
    private readonly AppConfig _cfg;
    private readonly IPowerEvents _power;
    private readonly IAppTimer _timer;
    private bool _notified;

    /// <summary>Батарея дозарядилась до 100% (раз за сессию режима) — OSD/джингл вешает вызывающий.</summary>
    public Action? Ready;

    public TravelChargeMonitor(AppConfig cfg, IPowerEvents power, IAppTimer? timer = null)
    {
        _cfg = cfg;
        _power = power;
        _timer = timer ?? new UiTimer();
        _timer.Interval = 5000;
        _timer.Tick += CheckTravelFull;
    }

    /// <summary>
    /// Перевзвести наблюдение по текущему состоянию — звать после включения/выключения
    /// режима (меню/панель/клавиша), смены питания и на старте приложения.
    /// </summary>
    public void Rearm()
    {
        _notified = false;
        if (_cfg.TravelMode && _power.IsOnline) _timer.Start();
        else _timer.Stop();
    }

    // Батарея дозарядилась до 100% в режиме «В дорогу» → Ready, один раз за сессию режима.
    private void CheckTravelFull()
    {
        if (!_cfg.TravelMode) { _timer.Stop(); return; }
        if (_notified) return;
        if (!_power.IsOnline) return; // не на зарядке — ждём
        float f = _power.BatteryLifePercent;
        if (f < 0.999f || f > 1.001f) return; // ждём ровно 100% (значение вне [0..1] = «неизвестно»)
        _notified = true;
        _timer.Stop();
        Ready?.Invoke();
    }

    public void Dispose() => _timer.Dispose();
}
