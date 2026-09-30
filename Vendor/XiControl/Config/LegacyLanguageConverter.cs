using System.Text.Json;
using System.Text.Json.Serialization;

namespace XiControl.Config;

/// <summary>
/// Совместимость config.json: раньше язык хранился индексом enum (0=Ru, 1=En, 2=Zh),
/// теперь — культурным кодом («ru»/«en»/«zh»/…). Читает и старый int, и новую строку;
/// пишет всегда строку. Неизвестный индекс → null (определится по языку ОС при старте).
/// </summary>
public sealed class LegacyLanguageConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.GetInt32() switch { 0 => "ru", 1 => "en", 2 => "zh", _ => null },
            _ => null,
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }
}
