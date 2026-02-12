using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace DBToRestAPI.Cache
{
    /// <summary>
    /// A serializable container for caching query results.
    /// HybridCache requires concrete, serializable types - IActionResult/ObjectResult
    /// cannot be serialized/deserialized by System.Text.Json.
    /// This class stores the HTTP status code and the response data as a JsonElement,
    /// which can then be converted back to an ObjectResult after cache retrieval.
    /// </summary>
    public class CachableQueryResult
    {
        public int StatusCode { get; set; }
        public JsonElement Data { get; set; }

        /// <summary>
        /// Creates a <see cref="CachableQueryResult"/> from an <see cref="IActionResult"/>.
        /// Extracts the status code and serializes the value from ObjectResult types.
        /// </summary>
        public static CachableQueryResult FromActionResult(IActionResult actionResult)
        {
            if (actionResult is ObjectResult objectResult)
            {
                return new CachableQueryResult
                {
                    StatusCode = objectResult.StatusCode ?? 200,
                    Data = JsonSerializer.SerializeToElement(objectResult.Value)
                };
            }

            // Fallback for other IActionResult types (shouldn't happen for cached db queries)
            return new CachableQueryResult
            {
                StatusCode = 200,
                Data = default
            };
        }

        /// <summary>
        /// Converts this cached result back to an <see cref="IActionResult"/>.
        /// </summary>
        public IActionResult ToActionResult()
        {
            return new ObjectResult(Data)
            {
                StatusCode = StatusCode
            };
        }
    }
}
