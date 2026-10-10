using System.Globalization;
using System.Text;

namespace DBToRestAPI.Services
{
    /// <summary>
    /// Values made safe to write to the log.
    /// </summary>
    internal static class LogText
    {
        /// <summary>What a value that isn't a network URL is logged as.</summary>
        public const string NotANetworkUrl = "(not a network URL)";

        /// <summary>
        /// An http or https URL without the parts that may be secret: the query string, the fragment, and
        /// any user name or password. The path is kept. Anything else (a relative or scheme-relative value,
        /// a file path, another scheme, or text that doesn't parse) could hold a key or a password anywhere:
        /// .NET reads a file path, or an ftp URL, with `?` as an ordinary character. So it is logged as
        /// <see cref="NotANetworkUrl"/>.
        /// </summary>
        public static string Url(string? url)
        {
            if (string.IsNullOrEmpty(url))
                return string.Empty;
            return Uri.TryCreate(url, UriKind.Absolute, out var uri)
                   && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                ? uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped)
                : NotANetworkUrl;
        }

        /// <summary>
        /// Text from a request, such as its route, written so it can't start a new log line: control
        /// characters, the Unicode line and paragraph separators and <c>%</c> are percent-encoded as UTF-8
        /// (<c>%0A</c>, <c>%25</c>). Encoding <c>%</c> as well keeps two different texts from coming out
        /// the same.
        /// </summary>
        public static string Escape(string? text)
        {
            if (string.IsNullOrEmpty(text) || !text.Any(NeedsEscape))
                return text ?? string.Empty;

            var escaped = new StringBuilder(text.Length + 8);
            foreach (var c in text)
            {
                if (!NeedsEscape(c))
                {
                    escaped.Append(c);
                    continue;
                }
                foreach (var b in Encoding.UTF8.GetBytes(c.ToString()))
                    escaped.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
            return escaped.ToString();
        }

        private static bool NeedsEscape(char c)
            => c == '%' || char.IsControl(c) || c == '\u2028' || c == '\u2029';
    }
}
