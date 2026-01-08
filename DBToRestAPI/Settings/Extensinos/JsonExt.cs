using Com.H.Text.Json;
using Microsoft.AspNetCore.Mvc;
namespace DBToRestAPI.Settings.Extensinos
{
    public static class JsonExt
    {
        /// <summary>
        /// Writes an ObjectResult as JSON to the response.
        /// Returns false if the response has already started and cannot be written to.
        /// </summary>
        /// <param name="response">The HTTP response to write to</param>
        /// <param name="result">The ObjectResult containing the value and status code</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>True if the response was written successfully, false if the response had already started</returns>
        public static async Task<bool> DeferredWriteAsJsonAsync(
            this HttpResponse response,
            ObjectResult result,
            CancellationToken cancellationToken = default
            )
        {
            // Check if response has already started - cannot modify headers or status code
            if (response.HasStarted)
            {
                return false;
            }

            // if value is null, return 204
            if (result.Value == null)
            {
                response.StatusCode = result.StatusCode ?? 204;
                return true;
            }

            response.StatusCode = result.StatusCode ?? 200;
            response.ContentType = "application/json";

            await result.Value.JsonSerializeAsync(response.BodyWriter, cancellationToken: cancellationToken);
            return true;
        }
    }
}
