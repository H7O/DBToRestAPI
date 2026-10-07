using Com.H.Cache;
using Com.H.Data.Common;
using Com.H.Threading;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Hybrid;
using System.Buffers;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using DBToRestAPI.Services;
using DBToRestAPI.Settings;
using DBToRestAPI.Settings.Extensinos;
using Microsoft.AspNetCore.WebUtilities;

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
            var cacheInfo = IsCacheableMethod(context.Request.Method)
                ? GetCacheInfoForGateway(serviceSection, context, resolvedRoute)
                : null;
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

            // A factory that returns null has already streamed its response to its own caller,
            // because the upstream status is in exclude_status_codes_from_cache. HybridCache would
            // store that null like any value, and every later request would get an empty 200
            // without being forwarded, until the entry expired. So a null leaves the factory as an
            // exception, which HybridCache never stores.
            var ranOwnFactory = false;
            try
            {
                return await this._cache.GetOrCreateAsync<T?>(
                    cacheInfo.Key, // Unique key to the cache entry

                    async cancel =>
                    {
                        ranOwnFactory = true;
                        // Buffered mode, for caching
                        return await dataFactory(true) ?? throw new ResponseNotCachedException();
                    },
                    options: options,
                    cancellationToken: cancellationToken);
            }
            catch (ResponseNotCachedException) when (ranOwnFactory)
            {
                // This request's response was streamed by its own factory.
                return null;
            }
            catch (ResponseNotCachedException)
            {
                // This request waited on another request's factory, whose response was not cached
                // and went to that other caller. Forward this one on its own.
                return await dataFactory(false);
            }
        }

        /// <summary>
        /// Thrown out of a gateway cache factory whose response was streamed rather than buffered,
        /// so that HybridCache stores nothing for it.
        /// </summary>
        private sealed class ResponseNotCachedException : Exception
        {
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
        /// <param name="context">The HTTP context. Only GET and HEAD requests use the cache, and the request's route is part of the key.</param>
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
                HttpContext context,
                List<DbQueryParams> qParams,
                Func<bool, Task<T>> dataFactory,
                CancellationToken cancellationToken = default
            )
        {
            var cacheInfo = GetCacheInfo(serviceSection, context, qParams);
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
        /// <param name="context">The HTTP context. Only GET and HEAD requests use the cache, and the request's route is part of the key.</param>
        /// <param name="qParams">A list of query parameters used to identify the cache entry.</param>
        /// <param name="dataFactory">A function that generates the IActionResult. Receives a boolean
        /// for disableDeferredExecution (true = materialize for cache, false = stream).</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>The IActionResult either from cache or freshly generated.</returns>
        public async Task<IActionResult> GetQueryResultAsActionAsync(
                IConfigurationSection serviceSection,
                HttpContext context,
                List<DbQueryParams> qParams,
                Func<bool, Task<IActionResult>> dataFactory,
                CancellationToken cancellationToken = default
            )
        {
            var cacheInfo = GetCacheInfo(serviceSection, context, qParams);
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
        /// True for the verbs whose responses may be served from, or stored in, the cache.
        /// </summary>
        /// <remarks>
        /// Only GET and HEAD. An endpoint with no <c>&lt;verb&gt;</c> answers every verb, and the
        /// cache key never held the verb, so a POST, PUT or DELETE to a cached endpoint used to be
        /// answered with the cached GET response: the write never ran, yet the caller got a
        /// success. Caching a write by its verb instead would not help either, because the request
        /// body is not part of the key, so the second write would still be skipped. A write must
        /// always reach the database (or, for a gateway route, the upstream API).
        /// </remarks>
        internal static bool IsCacheableMethod(string? method)
            => HttpMethods.IsGet(method ?? string.Empty) || HttpMethods.IsHead(method ?? string.Empty);

        /// <summary>
        /// The names in a route's <c>invalidators</c>. They are split as mandatory_parameters is
        /// (<see cref="ParametersExt.SplitNames"/>), so a name may contain spaces (<c>sort by</c>)
        /// or, in a <c>|</c> list, commas.
        /// </summary>
        /// <remarks>
        /// Before 1.7.8 commas, spaces and <c>;</c> all separated names, so <c>tenant_id user_id</c>
        /// meant two names. Read as one name that matches no input, such a list would leave both
        /// inputs out of the key, and one caller's cached answer would be served to the others. So
        /// a name with a comma, a space or <c>;</c> stays whole only when one of the route's
        /// queries uses it in a marker (<c>{{sort by}}</c>, or the route's own delimiters);
        /// otherwise it is split on those characters, as before. A gateway route runs no query, so
        /// its names are always split so.
        /// </remarks>
        /// <param name="root">The whole configuration, for the global marker patterns.</param>
        internal static string[] GetInvalidators(IConfigurationSection serviceSection, string? list, IConfiguration? root = null)
            => GetInvalidators(list, QueryTexts(serviceSection), MarkerPatterns(serviceSection, root));

        // The same, from the route's query texts and its configured marker patterns.
        private static string[] GetInvalidators(string? list, string[] texts, string[] patterns)
        {
            var names = ParametersExt.SplitNames(list);
            if (!names.Any(HasOldSeparator))
                return names;

            // A name can only be a marker's if the SQL holds it, so most old lists (tenant_id
            // user_id) are settled by a text search, without the marker patterns.
            bool InSql(string name) => texts.Any(t => t.Contains(name, StringComparison.OrdinalIgnoreCase));
            var used = names.Any(name => HasOldSeparator(name) && InSql(name))
                ? MarkerNames(texts, patterns)
                : [];

            return names
                .SelectMany(name => HasOldSeparator(name) && !used.Contains(name)
                    ? name.Split(_oldInvalidatorSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    : [name])
                .ToArray();
        }

        private static readonly char[] _oldInvalidatorSeparators = [',', ' ', ';'];

        private static bool HasOldSeparator(string name) => name.IndexOfAny(_oldInvalidatorSeparators) >= 0;

        // The settings that change the delimiters of a request-input marker, as ParametersBuilder
        // reads them: on the route, else globally (some under regex:, some at the root).
        private static readonly (string Route, string Global)[] _markerPatternKeys =
        [
            ("json_variables_pattern", "regex:json_variables_pattern"),
            ("headers_variables_pattern", "regex:headers_variables_pattern"),
            ("auth_variables_pattern", "regex:auth_variables_pattern"),
            ("settings_variables_pattern", "regex:settings_variables_pattern"),
            ("form_data_variables_pattern", "form_data_variables_pattern"),
            ("query_string_variables_pattern", "query_string_variables_pattern"),
            ("route_variables_pattern", "route_variables_pattern"),
        ];

        // The marker patterns configured for the route and globally, one slot per setting (empty
        // where unset), so two reads of the same configuration give equal arrays.
        private static string[] MarkerPatterns(IConfigurationSection serviceSection, IConfiguration? root)
        {
            var patterns = new string[_markerPatternKeys.Length * 2];
            for (var i = 0; i < _markerPatternKeys.Length; i++)
            {
                // Through GetSection, as ParametersBuilder reads them (see QueryTexts).
                patterns[2 * i] = serviceSection.GetSection(_markerPatternKeys[i].Route).Value ?? string.Empty;
                patterns[2 * i + 1] = root?.GetSection(_markerPatternKeys[i].Global).Value ?? string.Empty;
            }
            return patterns;
        }

        // The names the route's queries use in markers, found with the default marker patterns
        // and any delimiters set on the route or globally, so ||sort by|| counts too. A name that
        // only appears elsewhere in the SQL, in a comment say, doesn't.
        private static HashSet<string> MarkerNames(string[] texts, string[] patterns)
        {
            var all = new HashSet<string>(_defaultMarkerPatterns, StringComparer.Ordinal);
            foreach (var pattern in patterns)
                if (pattern.Length > 0)
                    all.Add(pattern);

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pattern in all)
            {
                try
                {
                    foreach (var text in texts)
                        foreach (Match match in Regex.Matches(text, pattern))
                            if (match.Groups.TryGetValue("param", out var param) && param.Success)
                                names.Add(param.Value);
                }
                catch (ArgumentException)
                {
                    // Not a valid pattern: the engine can't use it as a marker either.
                }
            }
            return names;
        }

        // The answers of GetInvalidators, per route and list, each kept with the inputs it was
        // worked out from: the route's query texts and marker patterns. A lookup reads those inputs
        // again, the way the request reads them (through GetSection), and uses the answer only if
        // they are unchanged. So the answer always matches the SQL the request runs, whatever order
        // a reload's callbacks run in. A cached GET pays a few reads instead of running the marker
        // patterns over the SQL.
        private sealed record InvalidatorsAnswer(string[] Texts, string[] Patterns, string[] Names);
        private readonly ConcurrentDictionary<(string Path, string List), InvalidatorsAnswer> _invalidators = new();

        private string[] Invalidators(IConfigurationSection serviceSection, string? list)
        {
            var names = ParametersExt.SplitNames(list);
            if (!names.Any(HasOldSeparator))
                return names;

            var texts = QueryTexts(serviceSection);
            var patterns = MarkerPatterns(serviceSection, _configuration);
            var key = (serviceSection.Path, list!);
            if (_invalidators.TryGetValue(key, out var answer)
                && answer.Texts.AsSpan().SequenceEqual(texts)
                && answer.Patterns.AsSpan().SequenceEqual(patterns))
                return answer.Names;

            answer = new InvalidatorsAnswer(texts, patterns, GetInvalidators(list, texts, patterns));
            _invalidators[key] = answer;
            return answer.Names;
        }

        private static readonly string[] _defaultMarkerPatterns = typeof(DefaultRegex)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string))
            .Select(f => (string)f.GetValue(null)!)
            .Where(p => p.Contains("(?<param>", StringComparison.Ordinal))
            .ToArray();

        // The text of every query the route runs, read as QueryConfigurationParser reads it: the
        // query's value, or else the value of each child of <query> (a chain), plus the count_query.
        // Everything here goes through GetSection, as the rest of the request does: on the engine's
        // configuration wrappers an indexer reads the snapshot the section was built from, while
        // GetSection reads the current configuration, which is what the request runs.
        private static string[] QueryTexts(IConfigurationSection serviceSection)
        {
            var texts = new List<string>();
            var query = serviceSection.GetSection("query");
            if (!string.IsNullOrEmpty(query.Value))
                texts.Add(query.Value);
            else
                texts.AddRange(query.GetChildren().Select(c => c.Value).OfType<string>());
            if (serviceSection.GetSection("count_query").Value is { } countQuery)
                texts.Add(countQuery);
            return texts.ToArray();
        }

        /// <summary>
        /// Returns a cache mechanism along with the cache configuration details for a specific service section.
        /// </summary>
        /// <param name="serviceSection">The configuration section for the specific service.</param>
        /// <param name="context">The HTTP context; its verb decides whether the cache applies, and its route is part of the key.</param>
        /// <param name="qParams">A list of query parameters used to construct the cacheService key and to be used to evaluate cache invalidators</param>
        /// <returns>
        /// An instance of <see cref="CacheInfo"/> if caching is enabled and properly configured; otherwise, <c>null</c>.
        /// </returns>
        internal CacheInfo? GetCacheInfo(IConfigurationSection serviceSection, HttpContext context, List<DbQueryParams> qParams)
        {
            if (!IsCacheableMethod(context.Request.Method))
                return null;

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
            var invalidators = Invalidators(serviceSection, memorySection.GetValue<string?>("invalidators"));

            // Construct the cache key.
            // Each value is labelled with its source (the hash of that source's marker pattern):
            // the same name can arrive in a header, the query string, the body and so on. Keyed by
            // the name alone, the last source won, so a query reading one source with its own
            // marker ({h{tenant}}) could store a header's answer under a query-string value's key.
            SortedDictionary<string, string> invalidatorsValues = new(StringComparer.Ordinal);
            foreach (var qParam in qParams)
            {
                IDictionary<string, object>? model = qParam.DataModel?.GetDataModelParameters();
                if (model == null) continue;
                var source = (qParam.QueryParamsRegex ?? string.Empty).ToXxHash3();
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
                    invalidatorsValues[key + "@" + source] = strValue.ToXxHash3().ToString();
                }
            }

            // The key starts with the endpoint's full configuration path, the verb and the route
            // values, then the invalidators.
            // - Path, not Key: Key is only the last segment, which is `0`, `1`... for repeated
            //   sibling elements, so two endpoints could share entries.
            // - The verb keeps a HEAD from being answered with a GET's stored entry, or the reverse.
            // - The route values (`users/1/data` vs `users/2/data`). Without them, an endpoint
            //   whose author forgot to list a route value in <invalidators> served the first id's
            //   answer for every id. The values, not the raw path: routing ignores letter case in
            //   literal segments and extra slashes, so `items/1`, `Items//1` and `items/1/` run the
            //   same SQL. Keyed on the raw path, each spelling would store its own copy, and a
            //   caller could fill memory with copies of one answer.
            // Each value is hashed like an invalidator value, so a `|` or `=` in it cannot forge a
            // key segment. The names come from the configuration.
            var sb = new StringBuilder(serviceSection.Path);
            sb.Append('|').Append(context.Request.Method.ToUpperInvariant());
            if (context.Items["route_parameters"] is Dictionary<string, string> routeValues)
            {
                foreach (var kv in routeValues.OrderBy(x => x.Key, StringComparer.Ordinal))
                {
                    sb.Append("|route:").Append(kv.Key).Append('=').Append((kv.Value ?? string.Empty).ToXxHash3());
                }
            }
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

            // Retrieve cache invalidators. A gateway route runs no query, so a name with a comma, a
            // space or `;` is split on them, as before 1.7.8; `|` and line breaks separate names too.
            var invalidators = Invalidators(serviceSection, memorySection.GetValue<string?>("invalidators"));

            // Build cache key components: method + route + query params + headers.
            // Each part is labelled with its source, `q:` for the query string and `h:` for a
            // header. The upstream receives both, so neither may stand in for the other: keyed by
            // the name alone, `?category=toys` sent with a `category: books` header stored the
            // toys answer under the books key, for every later caller.
            SortedDictionary<string, string> invalidatorsValues = new(StringComparer.Ordinal);

            // Check query string parameters.
            // Read raw, pair by pair. Request.Query merges names that differ only in case
            // (`?Tag=a&tag=b` reads like `?tag=a&tag=b`), but the upstream receives the raw text, and
            // a case-sensitive upstream answers the two differently. So each matching pair's exact
            // name and value go into the key, in order, hashed (always hashed, never raw and never
            // dropped - see GetCacheInfo above). Hashing every value on its own also keeps
            // `?tag=a&tag=b` apart from `?tag=a,b`.
            var queryPairs = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in new QueryStringEnumerable(context.Request.QueryString.Value))
            {
                var name = pair.DecodeName().ToString();
                var invalidator = invalidators.FirstOrDefault(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
                if (invalidator is null)
                    continue;
                if (!queryPairs.TryGetValue(invalidator, out var pairs))
                    queryPairs[invalidator] = pairs = [];
                pairs.Add(name.ToXxHash3() + "=" + pair.DecodeValue().ToString().ToXxHash3());
            }
            foreach (var (invalidator, pairs) in queryPairs)
            {
                invalidatorsValues["q:" + invalidator.ToLowerInvariant()] =
                    pairs.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + string.Join(",", pairs);
            }

            // Check headers. Header names are case-insensitive in HTTP, so the name is lower-cased.
            foreach (var header in context.Request.Headers)
            {
                if (invalidators.Contains(header.Key, StringComparer.OrdinalIgnoreCase))
                {
                    // Always hashed, never raw and never dropped - see GetCacheInfo above.
                    // Hashing is also what makes an arbitrarily large header usable as an
                    // invalidator at all: a bearer token runs to ~1300 characters, and an author
                    // is free to nominate something far larger still.
                    invalidatorsValues["h:" + header.Key.ToLowerInvariant()] = header.Value.ToString().ToXxHash3().ToString();
                }
            }

            // Construct the cache key: section + method + route + invalidators.
            // Path, not Key, for the same reason as GetCacheInfo above. The route is hashed: it
            // comes from the request path, which may carry a decoded `|` or `=`, so appending it raw
            // let a path such as `x|category=<hash>` build the key of `x?category=...` and poison
            // that entry with the response for a different upstream path.
            var sb = new StringBuilder(serviceSection.Path);
            sb.Append('|').Append(context.Request.Method.ToUpperInvariant()); // Include HTTP method
            sb.Append('|').Append(resolvedRoute.ToXxHash3()); // Include resolved route path

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
