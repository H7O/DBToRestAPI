using Com.H.Cache;
using Com.H.Data.Common;
using Com.H.Threading;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Hybrid;
using System.Buffers;
using System.Text;
using DBToRestAPI.Services;

namespace DBToRestAPI.Cache
{
    public class CacheService(
        IEncryptedConfiguration configuration,
        IServiceProvider provider,
        HybridCache cache
        )
    {
        private readonly IEncryptedConfiguration _configuration = configuration;
        private readonly IServiceProvider _provider = provider;
        private readonly HybridCache _cache = cache;

        /// <summary>
        /// Retrieves an item from the cache or generates it using the specified data factory function.
        /// This method is specifically designed for API Gateway caching.
        /// </summary>
        /// <typeparam name="T">The type of the item to retrieve or generate.</typeparam>
        /// <param name="serviceSection">The configuration section containing cache settings.</param>
        /// <param name="context">The HTTP context containing request information.</param>
        /// <param name="resolvedRoute">The resolved route path after wildcard matching.</param>
        /// <param name="dataFactory">A function that generates the item if it is not found in the cache.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        public async Task<T?> GetForGateway<T>(
                IConfigurationSection serviceSection,
                HttpContext context,
                string resolvedRoute,
                Func<bool, Task<T?>> dataFactory,
                CancellationToken cancellationToken = default
            ) where T : class
        {
            var cacheInfo = GetCacheInfoForGateway(serviceSection, context, resolvedRoute);
            if (cacheInfo == null)
            {
                // if there is no cache configuration, just return the data by calling
                // the dataFactory with disableDeferredExecution = false (streaming mode)
                return await dataFactory(false);
            }

            var options = new HybridCacheEntryOptions
            {
                Expiration = cacheInfo.Duration,
                LocalCacheExpiration = cacheInfo.Duration,
            };
            return await this._cache.GetOrCreateAsync<T?>(
                cacheInfo.Key, // Unique key to the cache entry

                async cancel => await dataFactory(true),
                // ^ Data factory to generate the item (buffered mode for caching)
                options: options,
                cancellationToken: cancellationToken);
        }


        public async Task<T> GetAsync<T>(
                string key,
                TimeSpan duration,
                Func<CancellationToken, Task<T>> factory,
                CancellationToken cancellationToken = default)
        {
            return await this._cache.GetOrCreateAsync<T>(
                key,
                async cancel => await factory(cancel),
                new HybridCacheEntryOptions
                {
                    Expiration = duration,
                    LocalCacheExpiration = duration,
                },
                cancellationToken: cancellationToken);
        }


        /// <summary>
        /// Retrieves an item from the cache or generates it using the specified data factory function.
        /// </summary>
        /// <remarks>If the cache information cannot be determined from the provided configuration section
        /// and query parameters, the data factory is invoked without caching the result.</remarks>
        /// <typeparam name="T">The type of the item to retrieve or generate.</typeparam>
        /// <param name="serviceSection">The configuration section containing cache settings.</param>
        /// <param name="qParams">A list of query parameters used to identify the cache entry.</param>
        /// <param name="dataFactory">A function that generates the item if it is not found in the cache. The function receives a boolean
        /// indicating whether the data for the cache to be generated in deffered fashion and returned as an iterator (yet to be triggered) 
        /// or the whole data to be generated in memory and returned directly.
        /// This is helpful as the dataFactory function needs to tell the downstream functions how to handle the data generation accordingly.
        /// If the data is meant for streaming back to the client, then it should be generated in deffered fashion.
        /// And if the data is meant to be cached in memory, then it should be generated directly.
        /// The boolean value represents `disableDefferedExecution`, this means if it's true, then the data should be generated directly,
        /// and if it's false, then the data should be generated in deffered fashion.
        /// </param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests. The default value is <see cref="CancellationToken.None"/>.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the item retrieved from the
        /// cache or generated by the data factory.</returns>
        public async Task<T> GetQueryResultAsync<T>(
                IConfigurationSection serviceSection,
                List<DbQueryParams> qParams,
                Func<bool, Task<T>> dataFactory,
                CancellationToken cancellationToken = default
            )
        {
            var cacheInfo = GetCacheInfo(serviceSection, qParams);
            if (cacheInfo == null)
            {
                // if there is no cache configuration, just return the data by calling
                // the dataFactory with disableDefferedExecution = false (which means the data should be generated in deffered fashion)
                return await dataFactory(false);
            }

            var options = new HybridCacheEntryOptions
            {
                Expiration = cacheInfo.Duration,
                LocalCacheExpiration = cacheInfo.Duration,
            };
            return await this._cache.GetOrCreateAsync<T>(
                cacheInfo.Key, // Unique key to the cache entry

                async cancel => await dataFactory(true),
                // ^ Data factory to generate the item (in direct fashion for caching) if not found in cache
                options: options,
                cancellationToken: cancellationToken);
        }


        /// <summary>
        /// Retrieves a cached query result as an IActionResult, or generates and caches it.
        /// This method handles the IActionResult serialization problem by converting to/from
        /// a serializable <see cref="CachableQueryResult"/> container for cache storage.
        /// HybridCache cannot serialize/deserialize IActionResult (an interface), so this method
        /// converts the IActionResult to a CachableQueryResult before caching and back after retrieval.
        /// </summary>
        /// <param name="serviceSection">The configuration section containing cache settings.</param>
        /// <param name="qParams">A list of query parameters used to identify the cache entry.</param>
        /// <param name="dataFactory">A function that generates the IActionResult. Receives a boolean
        /// for disableDeferredExecution (true = materialize for cache, false = stream).</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>The IActionResult either from cache or freshly generated.</returns>
        public async Task<IActionResult> GetQueryResultAsActionAsync(
                IConfigurationSection serviceSection,
                List<DbQueryParams> qParams,
                Func<bool, Task<IActionResult>> dataFactory,
                CancellationToken cancellationToken = default
            )
        {
            var cacheInfo = GetCacheInfo(serviceSection, qParams);
            if (cacheInfo == null)
            {
                // No cache configured - return streaming IActionResult directly
                return await dataFactory(false);
            }

            var options = new HybridCacheEntryOptions
            {
                Expiration = cacheInfo.Duration,
                LocalCacheExpiration = cacheInfo.Duration,
            };

            // Cache a serializable CachableQueryResult instead of IActionResult
            var cachedResult = await this._cache.GetOrCreateAsync<CachableQueryResult>(
                cacheInfo.Key,
                async cancel =>
                {
                    // Execute the data factory in materialized (non-deferred) mode
                    var actionResult = await dataFactory(true);
                    // Convert IActionResult to a serializable container
                    return CachableQueryResult.FromActionResult(actionResult);
                },
                options: options,
                cancellationToken: cancellationToken);

            // Convert the cached container back to an IActionResult
            return cachedResult.ToActionResult();
        }


        /// <summary>
        /// Returns a cache mechanism along with the cache configuration details for a specific service section.
        /// </summary>
        /// <param name="serviceSection">The configuration section for the specific service.</param>
        /// <param name="qParams">A list of query parameters used to construct the cacheService key and to be used to evaluate cache invalidators</param>
        /// <returns>
        /// An instance of <see cref="CacheInfo"/> if caching is enabled and properly configured; otherwise, <c>null</c>.
        /// </returns>
        private CacheInfo? GetCacheInfo(IConfigurationSection serviceSection, List<DbQueryParams> qParams)
        {
            // Retrieve the memory cache section directly
            var memorySection = serviceSection.GetSection("cache:memory");
            if (!memorySection.Exists())
                return null;

            // Determine the cache duration
            int duration = memorySection.GetValue<int?>("duration_in_milliseconds") ??
                this._configuration.GetValue<int?>("cache:memory:duration_in_milliseconds") ?? -1;
            if (duration < 1)
                return null;

            // Retrieve cache invalidators
            var invalidatorsCsv = memorySection.GetValue<string?>("invalidators") ?? string.Empty;
            var invalidators = invalidatorsCsv.Split([',', ' ', '\n', '\r', ';'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            // Construct the cache key
            SortedDictionary<string, string> invalidatorsValues = [];
            foreach (var qParam in qParams)
            {
                IDictionary<string, object>? model = qParam.DataModel?.GetDataModelParameters();
                if (model == null) continue;
                foreach (var key in invalidators.Where(x => model.ContainsKey(x)))
                {
                    var value = model[key];
                    string strValue = value is string s ? s : value?.ToString() ?? string.Empty;

                    // EVERY value is hashed — never dropped, never embedded raw. Both alternatives
                    // let two different requests collide onto one cache entry:
                    // - DROPPING an over-long value (the original behaviour) left the parameter out
                    //   of the key altogether, so two requests differing only in that value shared
                    //   an entry and the second caller was served the first one's response.
                    // - EMBEDDING a value raw let a caller forge a key segment, because `|` and `=`
                    //   are legal inside header and query-string values: `?tenant=a|user=victim`
                    //   built the same key string as `?tenant=a&user=victim`.
                    // A fixed-width hash is bounded, distinct, and delimiter-free, closing both.
                    // Cost is a few hundred nanoseconds per value — noise next to the DB round-trip
                    // this cache exists to avoid.
                    invalidatorsValues[key] = strValue.ToXxHash3().ToString();
                }
            }

            var sb = new StringBuilder(serviceSection.Key);
            if (invalidatorsValues.Count > 0)
            {
                foreach (var kv in invalidatorsValues)
                {
                    sb.Append('|').Append(kv.Key).Append('=').Append(kv.Value);
                }
            }

            var cacheKey = sb.ToString().ToXxHash3().ToString();

            return new CacheInfo()
            {
                Duration = TimeSpan.FromMilliseconds(duration),
                Key = cacheKey
            };
        }

        /// <summary>
        /// Returns cache configuration details for API Gateway routes.
        /// Builds cache key from HTTP method, resolved route, query parameters, and headers.
        /// </summary>
        /// <param name="serviceSection">The configuration section for the API gateway route.</param>
        /// <param name="context">The HTTP context containing request information.</param>
        /// <param name="resolvedRoute">The resolved route path after wildcard matching.</param>
        /// <returns>
        /// An instance of <see cref="CacheInfo"/> if caching is enabled and properly configured; otherwise, <c>null</c>.
        /// </returns>
        private CacheInfo? GetCacheInfoForGateway(
            IConfigurationSection serviceSection,
            HttpContext context,
            string resolvedRoute)
        {
            // Retrieve the memory cache section directly
            var memorySection = serviceSection.GetSection("cache:memory");
            if (!memorySection.Exists())
                return null;

            // Determine the cache duration
            int duration = memorySection.GetValue<int?>("duration_in_milliseconds") ??
                this._configuration.GetValue<int?>("cache:memory:duration_in_milliseconds") ?? -1;
            if (duration < 1)
                return null;

            // Retrieve cache invalidators
            var invalidatorsCsv = memorySection.GetValue<string?>("invalidators") ?? string.Empty;
            var invalidators = invalidatorsCsv.Split([',', ' ', '\n', '\r', ';'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            // Build cache key components: method + route + query params + headers
            SortedDictionary<string, string> invalidatorsValues = [];

            // Check query string parameters
            foreach (var queryParam in context.Request.Query)
            {
                if (invalidators.Contains(queryParam.Key, StringComparer.OrdinalIgnoreCase))
                {
                    // Always hashed, never raw and never dropped - see GetCacheInfo above.
                    invalidatorsValues[queryParam.Key] = queryParam.Value.ToString().ToXxHash3().ToString();
                }
            }

            // Check headers
            foreach (var header in context.Request.Headers)
            {
                if (invalidators.Contains(header.Key, StringComparer.OrdinalIgnoreCase))
                {
                    // Always hashed, never raw and never dropped - see GetCacheInfo above.
                    // Hashing is also what makes an arbitrarily large header usable as an
                    // invalidator at all: a bearer token runs to ~1300 characters, and an author
                    // is free to nominate something far larger still.
                    invalidatorsValues[header.Key] = header.Value.ToString().ToXxHash3().ToString();
                }
            }

            // Construct the cache key: section + method + route + invalidators
            var sb = new StringBuilder(serviceSection.Key);
            sb.Append('|').Append(context.Request.Method); // Include HTTP method
            sb.Append('|').Append(resolvedRoute); // Include resolved route path

            if (invalidatorsValues.Count > 0)
            {
                foreach (var kv in invalidatorsValues)
                {
                    sb.Append('|').Append(kv.Key).Append('=').Append(kv.Value);
                }
            }

            var cacheKey = sb.ToString().ToXxHash3().ToString();

            return new CacheInfo()
            {
                Duration = TimeSpan.FromMilliseconds(duration),
                Key = cacheKey
            };
        }



    }


    internal static class StringExtensions
    {

        /// <summary>
        /// 64-bit xxHash3 of a string. Used to tell one cache entry from another, so speed and
        /// distinctness are what matter here — xxHash3 is deliberately NOT a cryptographic hash.
        /// </summary>
        /// <remarks>
        /// Deterministic across processes and restarts, unlike <c>string.GetHashCode()</c>, which
        /// is randomised per process and would make a cache miss after every restart.
        /// </remarks>
        internal static ulong ToXxHash3(this string text)
        {
            // Encoding.UTF8.GetMaxByteCount(n) is 3n + 3. Stack-allocating that for an arbitrary
            // string exhausts the thread's stack somewhere in the low hundreds of thousands of
            // characters (request threads get 1 MB on Windows, and .NET does not commit more than
            // a couple of MB per thread on Linux either), and a StackOverflowException cannot be
            // caught — it takes the whole process down, not just the request. So the stack is used
            // only for short strings; longer ones rent a buffer.
            //
            // 1 KB covers a whole assembled cache key (~340 characters) plus every ordinary
            // invalidator value in one stack frame, while staying far below any platform's stack
            // budget. Stack pages are committed on demand, so this costs nothing until it is used.
            const int stackAllocLimit = 1024;
            int maxByteCount = Encoding.UTF8.GetMaxByteCount(text.Length);

            if (maxByteCount <= stackAllocLimit)
            {
                Span<byte> buffer = stackalloc byte[stackAllocLimit];
                int bytesWritten = Encoding.UTF8.GetBytes(text, buffer);
                return System.IO.Hashing.XxHash3.HashToUInt64(buffer[..bytesWritten]);
            }

            byte[] rented = ArrayPool<byte>.Shared.Rent(maxByteCount);
            try
            {
                int bytesWritten = Encoding.UTF8.GetBytes(text, rented.AsSpan());
                return System.IO.Hashing.XxHash3.HashToUInt64(rented.AsSpan(0, bytesWritten));
            }
            finally
            {
                // clearArray: the long values that reach this branch are exactly the ones an author
                // is most likely to have nominated because they identify a caller — a bearer token,
                // a session blob. A pooled array keeps its contents after Return, so without this
                // those bytes stay readable to whoever rents the array next, and to anything that
                // dumps the heap. A memset of a few KB is nothing beside the hash we just ran.
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            }
        }

    }
}
