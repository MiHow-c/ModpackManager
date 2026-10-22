using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Tomlyn;
using Tomlyn.Model;

namespace ModpackManager.Core.Packs;

public enum ConfigIssueKind { TomlSyntax, JsonSyntax, UnexpectedBracket, UnclosedBracket, UnclosedString }

/// <summary>A problem in a config file; Line and Column are 1-based. Errors block saving, warnings do not.</summary>
public sealed record ConfigIssue(ConfigIssueKind Kind, int Line, int Column, string Detail)
{
    public bool IsError => Kind is ConfigIssueKind.TomlSyntax or ConfigIssueKind.JsonSyntax;
}

public static class ConfigValidator
{
    public static readonly string[] EditableExtensions = [".toml", ".json", ".json5", ".cfg", ".properties", ".snbt"];

    // many mod configs carry comments and trailing commas, which the game's parsers accept
    static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static bool IsEditable(string path) => EditableExtensions.Contains(Ext(path));

    /// <summary>Whether the format gets any check at all (.properties is free-form).</summary>
    public static bool IsChecked(string path) => Ext(path) is not ".properties";

    /// <summary>Syntax error that must block saving (TOML/JSON), or null.</summary>
    public static string? Validate(string path, string text) => Check(path, text) is { IsError: true } issue ? issue.Detail : null;

    /// <summary>
    /// TOML and JSON go through real parsers. JSON5, Forge .cfg and SNBT have no parser here, so they only get a
    /// bracket/quote balance check, reported as a warning.
    /// </summary>
    public static ConfigIssue? Check(string path, string text)
    {
        switch (Ext(path))
        {
            case ".toml":
                try
                {
                    TomlSerializer.Deserialize<TomlTable>(text);
                    return null;
                }
                catch (TomlException e)
                {
                    return new ConfigIssue(ConfigIssueKind.TomlSyntax, Math.Max(e.Line ?? 1, 1), Math.Max(e.Column ?? 1, 1), e.Message);
                }
            case ".json":
                try
                {
                    JsonDocument.Parse(text, Lenient).Dispose();
                    return null;
                }
                catch (JsonException e)
                {
                    return new ConfigIssue(ConfigIssueKind.JsonSyntax, (int)(e.LineNumber ?? 0) + 1, (int)(e.BytePositionInLine ?? 0) + 1, e.Message);
                }
            case ".json5": return CheckBrackets(text, "//", "\"'");
            case ".cfg": return CheckBrackets(text, "#", "\"");
            case ".snbt": return CheckBrackets(text, null, "\"'");
            default: return null;
        }
    }

    /// <summary>First unbalanced {} / [] or unterminated string, skipping strings and line comments.</summary>
    static ConfigIssue? CheckBrackets(string text, string? lineComment, string quotes)
    {
        var open = new Stack<(char Bracket, int Line, int Column)>();
        int line = 1, column = 0;
        char? quote = null;
        (int Line, int Column) quoteStart = default;
        var escaped = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\n')
            {
                // strings in these formats never span lines, so a newline means the quote was never closed
                if (quote is { } q) return new ConfigIssue(ConfigIssueKind.UnclosedString, quoteStart.Line, quoteStart.Column, q.ToString());
                (line, column) = (line + 1, 0);
                continue;
            }
            column++;
            if (quote is { } inside)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == inside) quote = null;
                continue;
            }
            if (lineComment is not null && string.CompareOrdinal(text, i, lineComment, 0, lineComment.Length) == 0)
            {
                var end = text.IndexOf('\n', i);
                i = (end < 0 ? text.Length : end) - 1;
                continue;
            }
            if (quotes.Contains(c))
            {
                (quote, quoteStart) = (c, (line, column));
            }
            else if (c is '{' or '[')
            {
                open.Push((c, line, column));
            }
            else if (c is '}' or ']')
            {
                if (open.Count == 0 || open.Pop().Bracket != (c == '}' ? '{' : '['))
                    return new ConfigIssue(ConfigIssueKind.UnexpectedBracket, line, column, c.ToString());
            }
        }
        if (quote is { } unclosed) return new ConfigIssue(ConfigIssueKind.UnclosedString, quoteStart.Line, quoteStart.Column, unclosed.ToString());
        return open.TryPeek(out var top) ? new ConfigIssue(ConfigIssueKind.UnclosedBracket, top.Line, top.Column, top.Bracket.ToString()) : null;
    }

    /// <summary>
    /// Re-indents JSON. Returns null when the file has comments: the writer could not keep them where they were,
    /// and silently dropping a config's comments is worse than not formatting. Throws JsonException on invalid JSON.
    /// </summary>
    public static string? FormatJson(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Allow, AllowTrailingCommas = true });
        while (reader.Read())
            if (reader.TokenType == JsonTokenType.Comment) return null;

        using var doc = JsonDocument.Parse(bytes, Lenient);
        using var stream = new MemoryStream();
        // WriteTo keeps numbers exactly as written (1.0 stays 1.0); relaxed escaping keeps non-ASCII text readable
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            doc.RootElement.WriteTo(writer);
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    static string Ext(string path) => Path.GetExtension(path).ToLowerInvariant();
}
