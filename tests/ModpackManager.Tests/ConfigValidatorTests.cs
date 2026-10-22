using ModpackManager.Core.Packs;

namespace ModpackManager.Tests;

public class ConfigValidatorTests
{
    [Fact]
    public void Toml_and_json_errors_point_at_the_broken_line_and_block_saving()
    {
        var toml = Assert.IsType<ConfigIssue>(ConfigValidator.Check("config/a.toml", "a = 1\nb = = 2\n"));
        Assert.Equal((ConfigIssueKind.TomlSyntax, 2, true), (toml.Kind, toml.Line, toml.IsError));
        Assert.NotNull(ConfigValidator.Validate("config/a.toml", "a = 1\nb = = 2\n"));

        var json = Assert.IsType<ConfigIssue>(ConfigValidator.Check("config/a.json", "{\n  \"a\": 1,\n  \"b\": }\n"));
        Assert.Equal((ConfigIssueKind.JsonSyntax, 3), (json.Kind, json.Line));

        Assert.Null(ConfigValidator.Check("config/a.toml", "[general]\nenabled = true # comment\n"));
        Assert.Null(ConfigValidator.Check("config/a.json", "{ \"a\": 1, // comment\n }"));
    }

    [Theory]
    [InlineData("a.cfg", "general {\n  # a { in a comment is fine\n  S:name=It's \"quoted\" {\n  }\n}\n", null, 0, 0)]
    [InlineData("a.cfg", "general {\n  I:size=5\n", ConfigIssueKind.UnclosedBracket, 1, 9)]
    [InlineData("a.snbt", "{a:\"}{\",b:'[',c:[1,2]}", null, 0, 0)]
    [InlineData("a.snbt", "{a:[1,2}", ConfigIssueKind.UnexpectedBracket, 1, 8)]
    [InlineData("a.json5", "{ a: 'x', // }\n }", null, 0, 0)]
    [InlineData("a.json5", "{ a: \"x }\n", ConfigIssueKind.UnclosedString, 1, 6)]
    public void Formats_without_a_parser_get_bracket_and_quote_warnings(string path, string text, ConfigIssueKind? kind, int line, int column)
    {
        var issue = ConfigValidator.Check(path, text);

        Assert.Equal(kind, issue?.Kind);
        if (issue is null) return;
        Assert.Equal((line, column, false), (issue.Line, issue.Column, issue.IsError));
        Assert.Null(ConfigValidator.Validate(path, text)); // warnings never block saving
    }

    [Fact]
    public void Json_formatting_indents_keeps_numbers_and_text_and_refuses_files_with_comments()
    {
        var formatted = ConfigValidator.FormatJson("{\"name\":\"Zażółć\",\"scale\":1.0,\"list\":[1,2],}");

        Assert.Equal("{\n  \"name\": \"Zażółć\",\n  \"scale\": 1.0,\n  \"list\": [\n    1,\n    2\n  ]\n}\n", formatted!.ReplaceLineEndings("\n"));
        Assert.Null(ConfigValidator.FormatJson("{ \"a\": 1 // keep me\n}"));
    }
}
