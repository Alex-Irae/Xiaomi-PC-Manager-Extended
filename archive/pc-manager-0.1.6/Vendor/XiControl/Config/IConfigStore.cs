namespace XiControl.Config;

/// <summary>
/// Persistence конфига за интерфейсом (шов для тестов и DI). POCO AppConfig остаётся;
/// удобный вызов cfg.Save() сохраняется — экземпляр помнит свой store (ставится при Load).
/// </summary>
public interface IConfigStore
{
    /// <summary>Загрузить конфиг (битый/отсутствующий файл → дефолты) и привязать его к стору.</summary>
    AppConfig Load();

    /// <summary>Сохранить конфиг на носитель. Ошибки не критичны — логируются, не бросают.</summary>
    void Save(AppConfig cfg);
}
