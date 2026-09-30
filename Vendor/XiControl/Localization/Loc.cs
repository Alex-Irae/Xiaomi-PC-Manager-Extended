using System.Text.Json;

namespace XiControl.Localization;

/// <summary>Язык интерфейса: культурный код (напр. «ru») + родное название (из файла перевода).</summary>
public sealed record LangInfo(string Culture, string Name);

/// <summary>
/// Локализация приложения. Строки грузятся из встроенных JSON — по одному файлу на язык
/// (<c>Localization/lang/&lt;культура&gt;.json</c>: плоская карта «ключ → перевод» + мета
/// <c>_culture</c>/<c>_name</c>/<c>_order</c>). Список языков — data-driven: добавить перевод =
/// положить новый JSON, править код не нужно (см. CONTRIBUTING.md «Как добавить перевод»).
/// Непереведённый ключ откатывается на базовый язык, затем на сам ключ — приложение не падает.
/// </summary>
public static class Loc
{
    private const string BaseCulture = "en"; // фолбэк для ключа, отсутствующего в текущем языке

    // культура → (ключ → строка); список языков — упорядоченный для меню/настроек
    private static readonly Dictionary<string, Dictionary<string, string>> Strings = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<LangInfo> Langs = [];

    private static string _current = BaseCulture;

    static Loc() => LoadEmbedded();

    /// <summary>Текущий язык (культурный код). Неизвестный/пустой автоматически → базовый.</summary>
    public static string Current
    {
        get => _current;
        set => _current = Resolve(value);
    }

    /// <summary>Доступные языки в порядке для комбо/меню (по <c>_order</c>, затем по названию).</summary>
    public static IReadOnlyList<LangInfo> Available => Langs;

    /// <summary>Культура, если такой перевод есть; иначе базовый язык (для DetectOsLanguage и т.п.).</summary>
    public static string Resolve(string? culture)
        => !string.IsNullOrEmpty(culture) && Strings.ContainsKey(culture) ? culture
           : Strings.ContainsKey(BaseCulture) ? BaseCulture
           : Langs.Count > 0 ? Langs[0].Culture : BaseCulture;

    /// <summary>Строка по ключу: текущий язык → базовый язык → сам ключ.</summary>
    public static string T(string key)
    {
        if (Strings.TryGetValue(_current, out var cur) && cur.TryGetValue(key, out var v)) return v;
        if (Strings.TryGetValue(BaseCulture, out var bas) && bas.TryGetValue(key, out var b)) return b;
        return key;
    }

    /// <summary>Строка по ключу + string.Format (числа — в формате локали пользователя).</summary>
    public static string T(string key, params object[] args)
        => string.Format(System.Globalization.CultureInfo.CurrentCulture, T(key), args);

    /// <summary>Полный набор «культура → (ключ → значение)» — для теста паритета переводов.</summary>
    internal static IReadOnlyDictionary<string, Dictionary<string, string>> All => Strings;

    // Загрузка всех встроенных lang.<культура>.json (LogicalName задан в csproj).
    private static void LoadEmbedded()
    {
        var asm = typeof(Loc).Assembly;
        var found = new List<(LangInfo info, int order)>();
        foreach (var res in asm.GetManifestResourceNames())
        {
            if (!res.StartsWith("lang.", StringComparison.Ordinal) || !res.EndsWith(".json", StringComparison.Ordinal))
                continue;
            try
            {
                using var stream = asm.GetManifestResourceStream(res)!;
                var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(stream);
                if (raw is null) continue;

                string culture = Meta(raw, "_culture");
                if (string.IsNullOrEmpty(culture)) continue;
                string name = Meta(raw, "_name") is { Length: > 0 } nm ? nm : culture;
                int order = raw.TryGetValue("_order", out var o) && o.ValueKind == JsonValueKind.Number
                    ? o.GetInt32() : int.MaxValue;

                var map = new Dictionary<string, string>(raw.Count);
                foreach (var kv in raw)
                    if (!kv.Key.StartsWith('_') && kv.Value.ValueKind == JsonValueKind.String)
                        map[kv.Key] = kv.Value.GetString()!;

                Strings[culture] = map;
                found.Add((new LangInfo(culture, name), order));
            }
            catch (Exception ex) { Log.Ex($"Loc.Load({res})", ex); } // битый перевод не роняет остальные
        }
        // порядок предсказуем: сначала _order, при равенстве — по названию
        found.Sort((a, b) => a.order != b.order ? a.order.CompareTo(b.order)
            : string.Compare(a.info.Name, b.info.Name, StringComparison.OrdinalIgnoreCase));
        foreach (var f in found) Langs.Add(f.info);
    }

    private static string Meta(Dictionary<string, JsonElement> raw, string key)
        => raw.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
