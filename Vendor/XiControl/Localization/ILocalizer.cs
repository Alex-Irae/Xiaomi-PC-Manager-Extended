namespace XiControl.Localization;

/// <summary>
/// Локализация как зависимость (DI) — шов над статическим Loc. Язык задаётся культурным
/// кодом (напр. «ru»); список доступных языков — data-driven из встроенных JSON.
/// </summary>
public interface ILocalizer
{
    /// <summary>Текущий язык интерфейса (культурный код). Неизвестный → базовый.</summary>
    string Current { get; set; }

    /// <summary>Доступные языки в порядке для UI (культура + родное название).</summary>
    IReadOnlyList<LangInfo> Available { get; }

    /// <summary>Строка по ключу; неизвестный ключ возвращается как есть.</summary>
    string T(string key);

    /// <summary>Строка по ключу + string.Format по аргументам.</summary>
    string T(string key, params object[] args);
}

/// <summary>Прод-реализация поверх статического Loc (встроенные JSON-переводы).</summary>
public sealed class Localizer : ILocalizer
{
    public string Current { get => Loc.Current; set => Loc.Current = value; }
    public IReadOnlyList<LangInfo> Available => Loc.Available;
    public string T(string key) => Loc.T(key);
    public string T(string key, params object[] args) => Loc.T(key, args);
}
