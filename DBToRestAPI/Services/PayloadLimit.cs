using System.Globalization;
using Microsoft.AspNetCore.Http.Features;

namespace DBToRestAPI.Services
{
    /// <summary>
    /// The request body limit, <c>max_payload_size_in_bytes</c>, read from the current configuration for
    /// each request, so a change takes effect without a restart. A value that isn't a positive whole
    /// number is treated as not set.
    /// </summary>
    internal static class PayloadLimit
    {
        /// <summary>300 MiB.</summary>
        internal const long Default = 300L * 1024 * 1024;

        internal static long Read(IConfiguration configuration) =>
            long.TryParse(configuration["max_payload_size_in_bytes"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit)
            && limit > 0
                ? limit
                : Default;

        /// <summary>
        /// Sets the limit on a request that hasn't started reading its body: the server's body size limit
        /// (a larger body is a 413) and the multipart form limit.
        /// </summary>
        internal static void Apply(HttpContext context, IConfiguration configuration)
        {
            var limit = Read(configuration);
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySize)
                bodySize.MaxRequestBodySize = limit;
            if (context is DefaultHttpContext defaultContext && defaultContext.FormOptions?.MultipartBodyLengthLimit != limit)
                defaultContext.FormOptions = new FormOptions { MultipartBodyLengthLimit = limit };
        }
    }
}
