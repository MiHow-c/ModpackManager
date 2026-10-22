using System.Text.Json;
using Tomlyn;
using Tomlyn.Model;

namespace ModpackManager.Core.Packs;

public static class ConfigValidator
{
    public static readonly string[] EditableExtensions = [".toml", ".json", ".json5", ".cfg", ".properties", ".snbt"];

    // many mod configs carry comments and trailing commas, which the game's parsers accept
    static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static bool IsEditable(string path) => EditableExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Syntax error for .toml/.json content; null when valid or when the format is not validated.</summary>
    public static string? Validate(string path, string text)
    {
        try
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".toml":
                    TomlSerializer.Deserialize<TomlTable>(text);
                    break;
                case ".json":
                    JsonDocument.Parse(text, Lenient).Dispose();
                    break;
            }
            return null;
        }
        catch (TomlException e) { return e.Message; }
        catch (JsonException e) { return e.Message; }
    }
}
