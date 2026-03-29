using Azure;
using Com.H.Collections.Generic;
using Com.H.Data.Common;
using Com.H.IO;
using DBToRestAPI.Cache;
using DBToRestAPI.Services;
using DBToRestAPI.Services.HttpExecutor;
using DBToRestAPI.Services.HttpExecutor.Models;
using DBToRestAPI.Services.QueryParser;
using DBToRestAPI.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding.Binders;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Http;
using Microsoft.IdentityModel.Tokens;
using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using System.Dynamic;
using System.Net.Http;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Text.Json;
using static System.Net.Mime.MediaTypeNames;

namespace DBToRestAPI.Controllers
{

    /// <summary>
    /// route, section, service_type, parameters, and payload should already be available  
    /// in the context.Items when this middleware is called.
    /// `route` is a string representing the route of the request.
    /// `section` is an IConfigurationSection representing the configuration section of the route.
    /// `service_type` is a string representing the type of service. Current supported services 
    /// are `api_gateway` and `db_query`, however `db_query` is the only one that is currently acceptable 
    /// for this controller since if the `service_type` is `api_gateway` then 
    /// the request should have been handled by the `Step4APIGatewayProcess` middleware.
    /// `parameters` is a List<Com.H.Data.Common.QueryParams> representing the parameters of the request.
    /// Which includes the query string parameters, the body parameters, the route parameters and headers parameters.
    /// `payload` is a JsonElement representing the body of the request.
    /// </summary>

    public class ApiController(
        IEncryptedConfiguration configuration,
        DbConnectionFactory dbConnectionFactory,
        SettingsService settingsService,
        IQueryConfigurationParser queryConfigurationParser,
        IHttpRequestExecutor httpRequestExecutor,
        ILogger<ApiController> logger,
        IHostApplicationLifetime appLifetime
            ) : ControllerBase
    {
        private readonly IEncryptedConfiguration _configuration = configuration;
        private readonly DbConnectionFactory _dbConnectionFactory = dbConnectionFactory;

        private readonly IQueryConfigurationParser _queryConfigurationParser = queryConfigurationParser;
        private readonly IHttpRequestExecutor _httpRequestExecutor = httpRequestExecutor;

        private readonly ILogger<ApiController> _logger = logger;

        private readonly IHostApplicationLifetime _appLifetime = appLifetime;


        private readonly SettingsService _settings = settingsService;

        private static readonly string _errorCode = "API Controller Error";
        private static readonly HashSet<string> _responseStructures = new HashSet<string>
        {
            "array",
            "single",
            "auto",
            "file"
        };

        /// <summary>
        /// Attempts to extract custom error information from a database exception.
        /// Supports multiple database providers (SQL Server, PostgreSQL, MySQL, Oracle, SQLite, DB2).
        /// Looks for error codes in the range 50000-51000 which map to HTTP status codes 0-1000.
        /// </summary>
        /// <param name="exception">The exception to analyze (checks both the exception and its InnerException)</param>
        /// <param name="errorNumber">The extracted error number (50000-51000 range)</param>
        /// <param name="errorMessage">The extracted error message</param>
        /// <returns>True if a custom error was found in the valid range; otherwise false</returns>
        private static bool TryGetCustomDbError(Exception exception, out int errorNumber, out string errorMessage)
        {
            errorNumber = 0;
            errorMessage = string.Empty;

            // Check the exception itself first, then its InnerException
            var exceptionsToCheck = new[] { exception, exception?.InnerException };

            foreach (var ex in exceptionsToCheck)
            {
                if (ex == null) continue;

                int? extractedNumber = null;
                string? extractedMessage = null;

                // SQL Server: Microsoft.Data.SqlClient.SqlException
                if (ex is Microsoft.Data.SqlClient.SqlException sqlEx)
                {
                    extractedNumber = sqlEx.Number;
                    extractedMessage = sqlEx.Message;
                }
                // PostgreSQL: Npgsql.PostgresException - uses SqlState codes (strings like "P0001" for RAISE EXCEPTION)
                // For PostgreSQL, users should use RAISE EXCEPTION with ERRCODE and we check MessageText
                // PostgreSQL custom errors: RAISE EXCEPTION 'message' USING ERRCODE = '50404' won't work directly
                // Instead, we'll check if the exception message contains a pattern like [50xxx]
                else if (ex.GetType().FullName == "Npgsql.PostgresException")
                {
                    // Try to get the error code from the message or use reflection to get properties
                    extractedMessage = ex.Message;
                    // PostgresException doesn't have a numeric error code like SQL Server
                    // Users can embed the error code in the message: RAISE EXCEPTION '[50404] Not found'
                    var match = System.Text.RegularExpressions.Regex.Match(ex.Message, @"\[(\d{5})\]");
                    if (match.Success && int.TryParse(match.Groups[1].Value, out int pgCode))
                    {
                        extractedNumber = pgCode;
                        // Remove the error code prefix from the message for cleaner output
                        extractedMessage = ex.Message.Replace(match.Value, "").Trim();
                    }
                }
                // MySQL: MySqlConnector.MySqlException
                else if (ex.GetType().FullName == "MySqlConnector.MySqlException")
                {
                    // Use reflection to get the Number property to avoid hard dependency
                    var numberProp = ex.GetType().GetProperty("Number");
                    if (numberProp?.GetValue(ex) is int mysqlNumber)
                    {
                        extractedNumber = mysqlNumber;
                        extractedMessage = ex.Message;
                    }
                }
                // Oracle: Oracle.ManagedDataAccess.Client.OracleException
                // Oracle uses RAISE_APPLICATION_ERROR with codes -20000 to -20999
                // We map these to our 50000-51000 range: -20404 → 50404 (HTTP 404)
                else if (ex.GetType().FullName == "Oracle.ManagedDataAccess.Client.OracleException")
                {
                    var numberProp = ex.GetType().GetProperty("Number");
                    if (numberProp?.GetValue(ex) is int oracleNumber)
                    {
                        // Oracle error codes are negative: -20000 to -20999
                        // Convert to our range: -20404 → 50404
                        if (oracleNumber >= -20999 && oracleNumber <= -20000)
                        {
                            extractedNumber = 50000 + ((-oracleNumber) - 20000);
                        }
                        extractedMessage = ex.Message;
                    }
                }
                // SQLite: Microsoft.Data.Sqlite.SqliteException
                else if (ex is Microsoft.Data.Sqlite.SqliteException sqliteEx)
                {
                    // SQLite uses string error codes; for custom errors, users can use the message pattern
                    extractedMessage = sqliteEx.Message;
                    var match = System.Text.RegularExpressions.Regex.Match(ex.Message, @"\[(\d{5})\]");
                    if (match.Success && int.TryParse(match.Groups[1].Value, out int sqliteCode))
                    {
                        extractedNumber = sqliteCode;
                        extractedMessage = ex.Message.Replace(match.Value, "").Trim();
                    }
                }
                // DB2: IBM.Data.Db2.DB2Exception
                else if (ex.GetType().FullName == "IBM.Data.Db2.DB2Exception")
                {
                    // Try to get error code via reflection
                    // DB2Exception may have errors collection; check for native error
                    var messageProp = ex.GetType().GetProperty("Message");
                    extractedMessage = ex.Message;
                    // DB2 might embed error codes in message or have separate property
                    var match = System.Text.RegularExpressions.Regex.Match(ex.Message, @"\[(\d{5})\]");
                    if (match.Success && int.TryParse(match.Groups[1].Value, out int db2Code))
                    {
                        extractedNumber = db2Code;
                        extractedMessage = ex.Message.Replace(match.Value, "").Trim();
                    }
                }

                // Check if we found a valid custom error in the 50000-51000 range
                if (extractedNumber.HasValue && extractedNumber.Value >= 50000 && extractedNumber.Value < 51000)
                {
                    errorNumber = extractedNumber.Value;
                    errorMessage = extractedMessage ?? ex.Message;
                    return true;
                }
            }

            return false;
        }



        ///// <summary>
        ///// Similar to Index method but expects its endpoint to have a prefix of `json/`
        ///// Ignores the `Content-Type` header and processes the request payload as JSON
        ///// by default. The request is passed to the Index method for processing.
        ///// </summary>
        ///// <param name="payload">Payload to be passed to Index method under the hood</param>
        ///// <returns>Payload return from Index method</returns>
        //[HttpGet]
        //[HttpPost]
        //[HttpDelete]
        //[HttpPut]
        //[Route("json/{*route}")]
        //public async Task<IActionResult> JsonProxy(
        //    [ModelBinder(BinderType = typeof(BodyModelBinder))] JsonElement payload,
        //    CancellationToken cancellationToken
        //    )
        //{
        //    return await Index();
        //}





        [Produces("application/json")]
        [HttpGet]
        [HttpPost]
        [HttpDelete]
        [HttpPut]
        [Route("{*route}")]
        public async Task<IActionResult> Index()
        {
            #region log the time and the method name
            this._logger.LogDebug("{time}: in ApiController.Index method",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"));
            #endregion

            #region if no section passed from the previous middlewares, return 500
            IConfigurationSection? section = HttpContext.Items.ContainsKey("section")
                ? HttpContext.Items["section"] as IConfigurationSection
                : null;

            if (section == null || !section.Exists())
            {
                return StatusCode(500, new
                {
                    success = false,
                    message = $"Improper service setup. (Contact your service provider support and provide them with error code `{_errorCode}`)"
                });
                ;
            }
            #endregion

            #region if service_type is not db_query, return 500
            if (HttpContext.Items.ContainsKey("service_type")
                && HttpContext.Items["service_type"] as string != "db_query")
            {
                return StatusCode(500,
                        new
                        {
                            success = false,
                            message = $"Improper service setup. (Contact your service provider support and provide them with error code `{_errorCode}`)"
                        });
            }
            #endregion

            #region parse queries and get parameters

            // Parse all queries from the configuration section
            var queries = _queryConfigurationParser.Parse(section);

            if (queries == null || queries.Count < 1)
            {
                return StatusCode(500,
                    new
                    {
                        success = false,
                        message = $"No query defined for route `{HttpContext.Items["route"]}` (Contact your service provider support and provide them with error code `{_errorCode}`)"
                    });
            }

            var qParams = HttpContext.Items["parameters"] as List<DbQueryParams>;
            // If the parameters are not available, then there is a misconfiguration in the middleware chain
            // as even if the request does not have any parameters, the middleware chain should
            // have provided a default set of parameters for each parameter category (i.e., route, query string, body, headers)
            if (qParams == null ||
                qParams.Count < 1)
            {
                return StatusCode(500,
                    new
                    {
                        success = false,
                        message = $"No default parameters defined for route `{HttpContext.Items["route"]}` (Contact your service provider support and provide them with error code `{_errorCode}`)"
                    });
            }
            #endregion


            #region get the data from DB and return it
            try
            {
                var response = await _settings.CacheService
                    .GetQueryResultAsActionAsync(
                    section,
                    qParams,
                    disableDiffered => GetResultFromDbMultipleQueriesAsync(section, queries, qParams, disableDiffered),
                    HttpContext.RequestAborted
                    );

                return response;

            }
            catch (Exception ex)
            {
                // Log the exception with timestamp for easier debugging
                _logger.LogError(ex,
                    "{Time}: Exception in ApiController.Index. Route: {Route}, ResponseHasStarted: {HasStarted}",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    HttpContext.Items.TryGetValue("route", out var r) ? r : "unknown",
                    Response.HasStarted);

                // If response has already started, we cannot return any IActionResult
                // Just log and return an empty result to avoid the "StatusCode cannot be set" exception
                if (Response.HasStarted)
                {
                    _logger.LogWarning(
                        "{Time}: Cannot send error response - response already started. Route: {Route}",
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                        HttpContext.Items.TryGetValue("route", out var route) ? route : "unknown");
                    return new EmptyResult();
                }

                // Check for custom database errors (50000-51000 range) from any supported database vendor
                // This handles SQL Server, PostgreSQL, MySQL, Oracle, SQLite, and DB2
                if (TryGetCustomDbError(ex, out int dbErrorNumber, out string dbErrorMessage))
                {
                    int httpStatusCode = dbErrorNumber - 50000;
                    return new ObjectResult(dbErrorMessage)
                    {
                        StatusCode = httpStatusCode,
                        Value = new
                        {
                            success = false,
                            message = dbErrorMessage,
                            error_number = httpStatusCode
                        }
                    };
                }

                // check if user passed `debug-mode` header in 
                // the request and if it has a value that 
                // corresponds to the value defined in the config file
                // under `debug_mode_header_value` key
                // if so, return the full error message and stack trace
                // else return a generic error message
                if (_settings.IsDebugMode(Request))
                {
                    return StatusCode(500, new
                    {
                        success = false,
                        message = ex.Message,
                        stack_trace = ex.StackTrace,
                        inner_exception = ex.InnerException?.Message
                    });
                }

                return BadRequest(new { success = false, message = _settings.GetDefaultGenericErrorMessage() });

            }

            #endregion

        }

        private async Task<string> PrepareEmbeddedHttpCallsParamsIfAny(
            string query,
            List<DbQueryParams> qParams,
            IConfigurationSection section)
        {
            var overallStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var route = HttpContext.Items.TryGetValue("route", out var r) ? r?.ToString() : "unknown";

            // Check if there is an embedded http call within the query to resolve it before executing the main query.
            // Look for http regex in `http_variable_pattern` within the section to use for identifying http call variables.
            // If not found, try to get it from the global configuration, and if not found there, use the default pattern.
            var httpVariablePattern = section.GetValue<string?>("http_variable_pattern")
                ?? this._configuration.GetValue<string?>("regex:http_variable_pattern")
                ?? Settings.DefaultRegex.DefaultHttpVariablesPattern;

            // Use Distinct to avoid executing the same HTTP request multiple times when the same
            // marker appears multiple times in the query. String.Replace will naturally replace
            // all occurrences of the same marker with the result.
            // Note: No RegexOptions are passed here — matching the behavior of Com.H.Data.Common.
            // Singleline (dot-matches-newline) is controlled via inline (?s) in the pattern itself,
            // giving users the choice when overriding http_variable_pattern.
            var matches = System.Text.RegularExpressions.Regex.Matches(
                query,
                httpVariablePattern);

            // Get distinct matches by their full value (includes markers + param)
            // This ensures identical HTTP call definitions are only executed once
            // DistinctBy is more efficient than GroupBy().Select(g => g.First()) as it uses
            // an internal HashSet without allocating intermediate IGrouping objects
            var distinctMatches = matches
                .Cast<System.Text.RegularExpressions.Match>()
                .Where(m => m.Groups["param"] != null && !string.IsNullOrWhiteSpace(m.Groups["param"].Value))
                .DistinctBy(m => m.Value)
                .ToList();

            _logger.LogDebug(
                "{Time}: [EmbeddedHTTP] Route: {Route} — Found {Count} distinct {{http{{...}}http}} markers in query (regex match took {ElapsedMs}ms)",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), route, distinctMatches.Count, overallStopwatch.ElapsedMilliseconds);

            if (distinctMatches.Count < 1)
            {
                return query;
            }

            // Register embedded HTTP marker pattern so unresolved markers (if any)
            // are parameterized as DbNull by Com.H.Data.Common.
            // Safe to add even when all calls succeed because no markers remain in the query.
            if (!qParams.Any(x => string.Equals(x.QueryParamsRegex, httpVariablePattern, StringComparison.Ordinal)))
            {
                qParams.Add(new DbQueryParams
                {
                    DataModel = null,
                    QueryParamsRegex = httpVariablePattern
                });
            }

            var internallyReplacedMarkerPattern = @"(?<open_marker>\{http_internally_replaced\{)(?<param>.*?)?(?<close_marker>\}\})";
            qParams.Add(new DbQueryParams
            {
                DataModel = null,
                QueryParamsRegex = internallyReplacedMarkerPattern
            });

            // Phase 1: Prepare all requests (sequential — reads qParams, no mutation)
            var preparedCalls = distinctMatches.Select((matched, index) =>
            {
                var httpRequestDetails = matched.Groups["param"].Value;
                httpRequestDetails = httpRequestDetails.Fill(qParams);
                _logger.LogDebug(
                    "{Time}: [EmbeddedHTTP] Route: {Route} — Prepared call #{Index}: {Details}",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), route, index,
                    httpRequestDetails.Length > 500 ? httpRequestDetails[..500] + "..." : httpRequestDetails);
                return (Index: index, Match: matched, RequestDetails: httpRequestDetails);
            }).ToList();

            // Phase 2: Execute all HTTP calls in parallel (fan-out)
            // Normal calls share the request's cancellation token.
            // Fire-and-forget calls use the application stopping token so they survive after the response is sent.
            _logger.LogDebug(
                "{Time}: [EmbeddedHTTP] Route: {Route} — Starting {Count} HTTP call(s) in parallel...",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), route, preparedCalls.Count);
            var phase2Stopwatch = System.Diagnostics.Stopwatch.StartNew();

            // Separate normal calls from fire-and-forget calls
            var results = new string?[preparedCalls.Count];
            var awaitableTasks = new List<(int Index, Task<string?> Task)>();

            for (int i = 0; i < preparedCalls.Count; i++)
            {
                var call = preparedCalls[i];
                if (IsFireAndForget(call.RequestDetails))
                {
                    // Fire-and-forget: launch on a background thread with the app-lifetime token.
                    // We don't await, the result is null (parameterized as DbNull in SQL).
                    results[i] = null;
                    var requestDetails = call.RequestDetails;
                    var callIndex = i;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await ExecuteSingleEmbeddedHttpCallAsync(requestDetails, _appLifetime.ApplicationStopping);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex,
                                "{Time}: [EmbeddedHTTP] Route: {Route} — Fire-and-forget call #{Index} threw {ExType}. " +
                                "This error is non-fatal as the response was already sent.",
                                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), route, callIndex, ex.GetType().Name);
                        }
                    }, _appLifetime.ApplicationStopping);
                    _logger.LogDebug(
                        "{Time}: [EmbeddedHTTP] Route: {Route} — Call #{Index} launched as fire-and-forget.",
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), route, i);
                }
                else
                {
                    awaitableTasks.Add((i, ExecuteSingleEmbeddedHttpCallAsync(call.RequestDetails, this.HttpContext.RequestAborted)));
                }
            }

            // Await only the normal (non-fire-and-forget) calls
            if (awaitableTasks.Count > 0)
            {
                var awaitedResults = await Task.WhenAll(awaitableTasks.Select(t => t.Task));
                for (int j = 0; j < awaitableTasks.Count; j++)
                {
                    results[awaitableTasks[j].Index] = awaitedResults[j];
                }
            }

            phase2Stopwatch.Stop();

            _logger.LogDebug(
                "{Time}: [EmbeddedHTTP] Route: {Route} — All {Count} HTTP call(s) completed in {ElapsedMs}ms ({FireAndForgetCount} fire-and-forget). Results: [{ResultSummary}]",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), route, results.Length, phase2Stopwatch.ElapsedMilliseconds,
                preparedCalls.Count - awaitableTasks.Count,
                string.Join(", ", results.Select((res, i) => res == null
                    ? (IsFireAndForget(preparedCalls[i].RequestDetails) ? $"#{i}=FIRE_AND_FORGET" : $"#{i}=SKIPPED")
                    : $"#{i}={res.Length} chars")));

            // Phase 3: Apply results to query (sequential — no concurrency concerns)
            var dbQueryParams = new DbQueryParams()
            {
                DataModel = new Dictionary<string, string>(),
                QueryParamsRegex = internallyReplacedMarkerPattern
            };

            int count = 0;
            for (int i = 0; i < preparedCalls.Count; i++)
            {
                count++;
                var placeholderName = $"http_response_{count}";
                var structuredJson = results[i];

                if (structuredJson != null)
                {
                    // Normal response (success or failure) — add structured JSON to DataModel
                    (dbQueryParams.DataModel as Dictionary<string, string>)!.Add(
                        placeholderName,
                        structuredJson);
                }
                else
                {
                    // Skipped or fire-and-forget call — don't add to DataModel.
                    // Com.H.Data.Common will parameterize it as DbNull.Value.
                    _logger.LogDebug(
                        "{Time}: [EmbeddedHTTP] Route: {Route} — Call #{Index} result is NULL ({Reason}). Marker will be parameterized as NULL.",
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), route, i,
                        IsFireAndForget(preparedCalls[i].RequestDetails) ? "fire-and-forget" : "skipped");
                }

                // Replace the original marker with an internally-replaced placeholder.
                // This is critical because the original {http{...}http} marker may contain
                // inner variable references (e.g., {{param}}, {settings{...}}) that get
                // modified by subsequent pattern processing in Com.H.Data.Common.
                // If the original marker text is left in the query, the deferred null-param
                // replacement (String.Replace with the saved original text) will fail to match
                // the now-modified query text, leaving raw {http{ syntax in the SQL.
                query = query.Replace(preparedCalls[i].Match.Value, $"{{http_internally_replaced{{{placeholderName}}}}}");
            }

            qParams.Add(dbQueryParams);

            overallStopwatch.Stop();

            // Check if any unresolved {http{ markers remain (should be none)
            bool hasUnresolvedMarkers = query.Contains("{http{");
            int skippedCount = results.Count(r => r == null);
            _logger.LogDebug(
                "{Time}: [EmbeddedHTTP] Route: {Route} — Phase 3 complete. {TotalCount} call(s) processed ({ResolvedCount} resolved, {SkippedCount} skipped). " +
                "All markers replaced with placeholders. Unresolved {{http{{}} markers remaining: {HasUnresolved}. Total embedded HTTP processing time: {ElapsedMs}ms",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), route, preparedCalls.Count,
                preparedCalls.Count - skippedCount, skippedCount,
                hasUnresolvedMarkers, overallStopwatch.ElapsedMilliseconds);

            return query;
        }

        /// <summary>
        /// Executes a single embedded HTTP call and returns a structured JSON response
        /// containing status_code, headers, data, and error. Returns null if the call
        /// was skipped via the "skip" property.
        /// Designed to be called in parallel via Task.WhenAll — touches no shared state.
        /// </summary>
        /// <summary>
        /// Determines whether an embedded HTTP call should be skipped based on the "skip"
        /// property in its JSON configuration. Accepts bool, string ("true"/"1"/"yes"
        /// case-insensitive), or non-zero number as truthy values.
        /// Returns false if the property is absent, falsy, or the JSON is invalid.
        /// </summary>
        internal static bool ShouldSkipHttpCall(string httpRequestDetailsJson)
        {
            return CheckBooleanJsonProperty(httpRequestDetailsJson, "skip");
        }

        /// <summary>
        /// Determines whether an embedded HTTP call should be launched as fire-and-forget.
        /// When true, the call is started on a background thread and the SQL parameter
        /// receives NULL instead of waiting for the response.
        /// </summary>
        internal static bool IsFireAndForget(string httpRequestDetailsJson)
        {
            return CheckBooleanJsonProperty(httpRequestDetailsJson, "fire_and_forget");
        }

        /// <summary>
        /// Checks a boolean-like JSON property by name. Accepts bool, string ("true"/"1"/"yes"
        /// case-insensitive), or non-zero number as truthy values.
        /// Returns false if the property is absent, falsy, or the JSON is invalid.
        /// </summary>
        internal static bool CheckBooleanJsonProperty(string json, string propertyName)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty(propertyName, out var prop))
                    return false;

                return prop.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.True => true,
                    System.Text.Json.JsonValueKind.False => false,
                    System.Text.Json.JsonValueKind.String => prop.GetString() switch
                    {
                        var s when string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) => true,
                        var s when string.Equals(s, "1", StringComparison.Ordinal) => true,
                        var s when string.Equals(s, "yes", StringComparison.OrdinalIgnoreCase) => true,
                        _ => false
                    },
                    System.Text.Json.JsonValueKind.Number => prop.TryGetInt32(out var n) && n != 0,
                    _ => false
                };
            }
            catch
            {
                return false;
            }
        }

        private async Task<string?> ExecuteSingleEmbeddedHttpCallAsync(
            string httpRequestDetails,
            CancellationToken cancellationToken)
        {
            var callStopwatch = System.Diagnostics.Stopwatch.StartNew();
            // Extract URL for logging (best-effort)
            string urlForLog = "(unknown)";
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(httpRequestDetails);
                if (doc.RootElement.TryGetProperty("url", out var urlProp))
                    urlForLog = urlProp.GetString() ?? "(null)";
            }
            catch { /* ignore parse errors for logging */ }

            if (ShouldSkipHttpCall(httpRequestDetails))
            {
                callStopwatch.Stop();
                _logger.LogDebug(
                    "{Time}: [EmbeddedHTTP] Call to {Url} skipped (\"skip\" property is truthy). Marker will be parameterized as NULL.",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), urlForLog);
                return null;
            }

            _logger.LogDebug(
                "{Time}: [EmbeddedHTTP] Starting call to: {Url}",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), urlForLog);

            HttpExecutorResponse response;
            try
            {
                response = await this._httpRequestExecutor.ExecuteAsync(
                    httpRequestDetails,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                callStopwatch.Stop();
                _logger.LogWarning(
                    ex,
                    "{Time}: [EmbeddedHTTP] Call to {Url} threw {ExType} after {ElapsedMs}ms. Structured response will have status_code=0 and data=null.",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), urlForLog, ex.GetType().Name, callStopwatch.ElapsedMilliseconds);
                // Build a synthetic error response so we always return structured JSON
                response = HttpExecutorResponse.FromError(
                    ex.Message,
                    ex,
                    callStopwatch.Elapsed,
                    0);
            }

            callStopwatch.Stop();

            var structuredJson = response.ToStructuredJson();

            if (!response.IsSuccess)
            {
                _logger.LogWarning(
                    "{Time}: [EmbeddedHTTP] Call to {Url} returned status {StatusCode} (took {ElapsedMs}ms). " +
                    "Structured response delivered to SQL — query author can inspect $.status_code.",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), urlForLog, response.StatusCode,
                    callStopwatch.ElapsedMilliseconds);
            }
            else
            {
                _logger.LogDebug(
                    "{Time}: [EmbeddedHTTP] Call to {Url} succeeded with status {StatusCode} ({ContentLength} chars, took {ElapsedMs}ms)",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), urlForLog, response.StatusCode,
                    structuredJson.Length, callStopwatch.ElapsedMilliseconds);
            }

            return structuredJson;
        }


        /// <summary>
        /// Executes one or more database queries, passing results between them when chained.
        /// Each query can target a different database via its ConnectionStringName.
        /// The final (or only) query's result is returned to the client.
        /// </summary>
        /// <param name="serviceQuerySection">The configuration section for the specific service query.</param>
        /// <param name="queries">List of query definitions to execute in sequence.</param>
        /// <param name="qParams">Initial query parameters from the HTTP request.</param>
        /// <param name="disableDeferredExecution">If true, materializes final result for caching.</param>
        /// <returns>The result of the final query as an IActionResult.</returns>
        private async Task<IActionResult> GetResultFromDbMultipleQueriesAsync(
            IConfigurationSection serviceQuerySection,
            List<QueryDefinition> queries,
            List<DbQueryParams> qParams,
            bool disableDeferredExecution = false)
        {
            // Fallback timeouts from section and global config
            int? sectionTimeout = serviceQuerySection.GetValue<int?>("db_command_timeout");
            int? globalTimeout = _configuration.GetValue<int?>("db_command_timeout");

            // Ensure the chained query parameter regex pattern is registered in qParams.
            // Com.H.Data.Common requires at least one entry with a given regex pattern to recognize
            // and process variables matching that pattern. Without this, variables like {{column}} 
            // or {pq{column}} won't be replaced with DbNull when no matching parameter exists.
            // Adding a null DataModel entry is safe - it just registers the pattern for processing.
            // This also future-proofs the code if we consolidate to use this method for single queries.
            qParams.Add(new DbQueryParams
            {
                DataModel = null,
                QueryParamsRegex = DefaultRegex.DefaultPreviousQueryVariablesPattern
            });

            foreach (var query in queries)
            {
                // Resolve timeout: query → section → global
                int? commandTimeout = query.DbCommandTimeout ?? sectionTimeout ?? globalTimeout;

                // Create connection for this query
                var connection = _dbConnectionFactory.Create(query.ConnectionStringName);

                try
                {
                    if (query.IsLastInChain)
                    {
                        // Final query: register connection for disposal at request end (supports streaming)
                        HttpContext.Response.RegisterForDisposeAsync(connection);

                        // Delegate to existing method for response building
                        // We pass the accumulated qParams which now includes results from previous queries
                        return await GetResultFromDbFinalQueryAsync(
                            serviceQuerySection,
                            connection,
                            query.QueryText,
                            qParams,
                            commandTimeout,
                            disableDeferredExecution);
                    }
                    else
                    {
                        // Intermediate query: prepare embedded HTTP calls (if any), execute, materialize, and add result to qParams
                        var queryText = await PrepareEmbeddedHttpCallsParamsIfAny(query.QueryText, qParams, serviceQuerySection);

                        var result = await connection.ExecuteQueryAsync(
                            queryText,
                            qParams,
                            commandTimeout: commandTimeout,
                            cToken: HttpContext.RequestAborted);

                        HttpContext.RequestAborted.ThrowIfCancellationRequested();

                        // Detect single vs multiple rows using chambered enumerable
                        var chamberedResult = await result.ToChamberedEnumerableAsync(2, HttpContext.RequestAborted);

                        // Get the NEXT query's JsonVariableName for the dictionary key
                        var nextQuery = queries[query.Index + 1];

                        if (chamberedResult.WasExhausted(2))
                        {
                            // Single row (or zero rows): pass as dynamic object for {{column_name}} access
                            var singleRow = chamberedResult.AsEnumerable().FirstOrDefault();

                            qParams.Add(new DbQueryParams
                            {
                                DataModel = singleRow,
                                QueryParamsRegex = DefaultRegex.DefaultPreviousQueryVariablesPattern
                            });

                            // Also add as JSON array so {{json}} works consistently regardless of row count.
                            // This allows query authors to always use {{json}} without checking if result was single/multiple.
                            // The JSON entry is added AFTER the single row entry, so it takes precedence for {{json}}
                            // while individual columns remain accessible via {{column_name}}.
                            var jsonArray = singleRow != null
                                ? JsonSerializer.Serialize(new[] { singleRow })
                                : "[]";

                            qParams.Add(new DbQueryParams
                            {
                                DataModel = new Dictionary<string, object>
                                {
                                    [nextQuery.JsonVariableName] = jsonArray
                                },
                                QueryParamsRegex = DefaultRegex.DefaultPreviousQueryVariablesPattern
                            });
                        }
                        else
                        {
                            // Multiple rows: serialize to JSON and wrap in dictionary
                            var allRows = chamberedResult.AsEnumerable().ToList();
                            var jsonArray = JsonSerializer.Serialize(allRows);

                            qParams.Add(new DbQueryParams
                            {
                                DataModel = new Dictionary<string, object>
                                {
                                    [nextQuery.JsonVariableName] = jsonArray
                                },
                                QueryParamsRegex = DefaultRegex.DefaultPreviousQueryVariablesPattern
                            });
                        }

                        // Close the reader before moving to next query
                        await result.CloseReaderAsync();
                    }
                }
                catch (Exception ex)
                {
                    // Wrap exception with query index for debugging
                    throw new InvalidOperationException(
                        $"Query {query.Index + 1} of {queries.Count} failed: {ex.Message}", ex);
                }
                finally
                {
                    // Dispose intermediate connections immediately
                    if (!query.IsLastInChain)
                    {
                        await connection.DisposeAsync();
                    }
                }
            }

            // Should never reach here if queries list is non-empty
            return StatusCode(500, new
            {
                success = false,
                message = $"No queries to execute for route `{HttpContext.Items["route"]}` (Contact your service provider support and provide them with error code `{_errorCode}`)"
            });
        }

        /// <summary>
        /// Executes the final (or only) query and builds the HTTP response.
        /// Handles response_structure, count_query, root_node, embedded HTTP calls, and caching.
        /// </summary>
        private async Task<IActionResult> GetResultFromDbFinalQueryAsync(
            IConfigurationSection serviceQuerySection,
            DbConnection connection,
            string query,
            List<DbQueryParams> qParams,
            int? commandTimeout,
            bool disableDeferredExecution)
        {
            // Check if count query is defined
            var countQuery = serviceQuerySection.GetSection("count_query")?.Value;

            var customSuccessStatusCode = serviceQuerySection.GetValue<int?>("success_status_code") ??
                _configuration.GetValue<int?>("success_status_code") ?? 200;

            string? rootNodeName = serviceQuerySection.GetValue<string?>("root_node")
                ?? _configuration.GetValue<string?>("root_node") ?? null;

            if (string.IsNullOrWhiteSpace(countQuery))
            {
                var responseStructure = serviceQuerySection.GetValue<string>("response_structure")?.ToLower() ??
                    _configuration.GetValue<string>("response_structure")?.ToLower() ?? "auto";

                if (!_responseStructures.Contains(responseStructure, StringComparer.OrdinalIgnoreCase))
                {
                    return StatusCode(500, new
                    {
                        success = false,
                        message = $"Invalid response structure `{responseStructure}` defined for route "
                        + $"`{HttpContext.Items["route"]}` (Contact your service provider support and provide them with error code `{_errorCode}`)"
                    });
                }

                // Prepare embedded HTTP calls for main query (if any)
                query = await PrepareEmbeddedHttpCallsParamsIfAny(query, qParams, serviceQuerySection);

                // Diagnostic: check if unresolved {http{ markers remain before SQL execution
                if (query.Contains("{http{"))
                {
                    _logger.LogError(
                        "{Time}: [SQL-PRE-EXEC] Route: {Route} — WARNING: Query still contains unresolved {{http{{}} markers before SQL execution! " +
                        "This will cause SQL syntax errors. qParams count: {ParamCount}, qParams regexes: [{Regexes}]",
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                        HttpContext.Items.TryGetValue("route", out var preRoute) ? preRoute : "unknown",
                        qParams.Count,
                        string.Join("; ", qParams.Select((p, i) => $"#{i}: regex='{(p.QueryParamsRegex?.Length > 60 ? p.QueryParamsRegex[..60] + "..." : p.QueryParamsRegex)}' hasData={p.DataModel != null}")));
                }

                var resultWithNoCount = await connection.ExecuteQueryAsync(query, qParams, commandTimeout: commandTimeout, cToken: HttpContext.RequestAborted);
                if (resultWithNoCount != null)
                {
                    HttpContext.Response.RegisterForDisposeAsync(resultWithNoCount);
                }

                HttpContext.RequestAborted.ThrowIfCancellationRequested();

                if (responseStructure == "array")
                {
                    if (disableDeferredExecution)
                    {
                        if (!string.IsNullOrWhiteSpace(rootNodeName))
                        {
                            var wrappedResult = new ExpandoObject();
                            wrappedResult.TryAdd(rootNodeName, resultWithNoCount?.AsEnumerable().ToArray());
                            return StatusCode(customSuccessStatusCode, wrappedResult);
                        }
                        return StatusCode(customSuccessStatusCode, resultWithNoCount.AsEnumerable().ToArray());
                    }
                    if (!string.IsNullOrWhiteSpace(rootNodeName))
                    {
                        var wrappedResult = new ExpandoObject();
                        wrappedResult.TryAdd(rootNodeName, resultWithNoCount);
                        return StatusCode(customSuccessStatusCode, wrappedResult);
                    }
                    return StatusCode(customSuccessStatusCode, resultWithNoCount);
                }

                if (responseStructure == "single")
                {
                    var singleResult = resultWithNoCount.AsEnumerable().FirstOrDefault();
                    await resultWithNoCount.CloseReaderAsync();
                    if (!string.IsNullOrWhiteSpace(rootNodeName))
                    {
                        var wrappedResult = new ExpandoObject();
                        wrappedResult.TryAdd(rootNodeName, (object?)singleResult);
                        return StatusCode(customSuccessStatusCode, wrappedResult);
                    }
                    return StatusCode(customSuccessStatusCode, singleResult);
                }

                if (responseStructure == "file")
                {
                    return await ReturnFile(resultWithNoCount);
                }

                if (responseStructure == "auto")
                {
                    var chamberedResult = await resultWithNoCount.ToChamberedEnumerableAsync(2, HttpContext.RequestAborted);
                    HttpContext.RequestAborted.ThrowIfCancellationRequested();

                    if (chamberedResult.WasExhausted(2))
                    {
                        var singleResult = chamberedResult.AsEnumerable().FirstOrDefault();
                        await resultWithNoCount.CloseReaderAsync();
                        if (!string.IsNullOrWhiteSpace(rootNodeName))
                        {
                            var wrappedResult = new ExpandoObject();
                            wrappedResult.TryAdd(rootNodeName, (object?)singleResult);
                            return StatusCode(customSuccessStatusCode, wrappedResult);
                        }
                        return StatusCode(customSuccessStatusCode, singleResult);
                    }
                    else
                    {
                        if (!string.IsNullOrWhiteSpace(rootNodeName))
                        {
                            var wrappedResult = new ExpandoObject();
                            wrappedResult.TryAdd(rootNodeName,
                                disableDeferredExecution ?
                                chamberedResult.AsEnumerable().ToArray() :
                                chamberedResult);
                            return StatusCode(customSuccessStatusCode, wrappedResult);
                        }
                        return StatusCode(customSuccessStatusCode,
                            disableDeferredExecution ?
                            chamberedResult.AsEnumerable().ToArray() :
                            chamberedResult);
                    }
                }

                return StatusCode(500, new
                {
                    success = false,
                    message = $"Invalid response structure `{responseStructure}` defined for route `{HttpContext.Items["route"]}` (Contact your service provider support and provide them with error code `{_errorCode}`)"
                });
            }

            // With count query - prepare embedded HTTP calls (if any)
            countQuery = await PrepareEmbeddedHttpCallsParamsIfAny(countQuery, qParams, serviceQuerySection);

            var resultCount = await connection.ExecuteQueryAsync(countQuery, qParams, commandTimeout: commandTimeout, cToken: HttpContext.RequestAborted);
            if (resultCount != null)
            {
                HttpContext.Response.RegisterForDisposeAsync(resultCount);
            }

            var rowCount = resultCount.AsEnumerable().FirstOrDefault();
            HttpContext.RequestAborted.ThrowIfCancellationRequested();

            if (rowCount == null)
            {
                return StatusCode(500, new
                {
                    success = false,
                    message = $"Count query did not return any records for route `{HttpContext.Items["route"]}` (Contact your service provider support and provide them with error code `{_errorCode}`)"
                });
            }
            // Extract scalar value from single-column count result
            if (rowCount is IDictionary<string, object?> countDict && countDict.Count == 1)
            {
                rowCount = countDict.Values.First();
            }
            await resultCount.CloseReaderAsync();

            // Prepare embedded HTTP calls for main query (if any)
            query = await PrepareEmbeddedHttpCallsParamsIfAny(query, qParams, serviceQuerySection);

            var result = await connection.ExecuteQueryAsync(query, qParams, commandTimeout: commandTimeout, cToken: HttpContext.RequestAborted);
            if (result != null)
            {
                HttpContext.Response.RegisterForDisposeAsync(result);
            }

            HttpContext.RequestAborted.ThrowIfCancellationRequested();

            if (disableDeferredExecution)
            {
                if (!string.IsNullOrWhiteSpace(rootNodeName))
                {
                    var wrappedResult = new ExpandoObject();
                    wrappedResult.TryAdd(rootNodeName,
                        new { success = true, count = rowCount, data = result.AsEnumerable().ToArray() });
                    return StatusCode(customSuccessStatusCode, wrappedResult);
                }
                return StatusCode(customSuccessStatusCode,
                    new { success = true, count = rowCount, data = result.AsEnumerable().ToArray() });
            }

            if (!string.IsNullOrWhiteSpace(rootNodeName))
            {
                var wrappedResult = new ExpandoObject();
                wrappedResult.TryAdd(rootNodeName,
                    new { success = true, count = rowCount, data = await result.ToChamberedEnumerableAsync() });
                return StatusCode(customSuccessStatusCode, wrappedResult);
            }

            return StatusCode(customSuccessStatusCode,
                new { success = true, count = rowCount, data = await result.ToChamberedEnumerableAsync() });
        }

        private async Task<IActionResult> ReturnFile(
            DbAsyncQueryResult<dynamic>? resultWithNoCount)
        {
            #region getting the file details from the result set
            // if response structure is file, then return the first record and get the file details from it

            var singleResult = resultWithNoCount?.AsEnumerable().FirstOrDefault();

            // close the reader
            if (resultWithNoCount != null)
                await resultWithNoCount.CloseReaderAsync();

            if (singleResult == null)
            {
                // close the reader
                return NotFound(new
                {
                    success = false,
                    message = $"No file record found to download for route `{HttpContext.Items["route"]}` (Contact your service provider support and provide them with error code `{_errorCode}`)"
                });
            }

            var dictResult = singleResult as IDictionary<string, object?>;
            if (dictResult == null)
            {
                // return 404 not found error
                return NotFound(new
                {
                    success = false,
                    message = $"No file record found to download for route `{HttpContext.Items["route"]}` (Contact your service provider support and provide them with error code `{_errorCode}`)"
                });
            }
            #endregion


            // check if any of the following fields are present in the singleResult:
            /*
                file_name,
                base64_content,
                relative_path,
                content_type,
                http
                Note: if multiple fields are provided (e.g., both `base64_content`, `relative_path` and `http`),
                the app will prioritize the file content source in the following order:
                1- `base64_content`
                2- `relative_path`
                3- `http` (just proxy the file from the provided URL)
            */

            #region file name determination
            string fileName = "downloaded_file"; // default name if file_name or relative_path (from which we can infer the file name) was not provided

            if (dictResult.ContainsKey("file_name")
                && dictResult["file_name"] != null
                && !string.IsNullOrWhiteSpace(dictResult["file_name"]?.ToString()))
            {
                fileName = dictResult["file_name"]!.ToString()!;
            }
            else if (dictResult.ContainsKey("relative_path")
                && dictResult["relative_path"] != null
                && !string.IsNullOrWhiteSpace(dictResult["relative_path"]?.ToString()))
            {
                fileName = Path.GetFileName(dictResult["relative_path"]!.ToString()!);
            }

            if (string.IsNullOrWhiteSpace(fileName))
                fileName = "downloaded_file";

            #endregion

            #region determine content type
            // if not provided in the result set, try to determine the content type from file extension
            // and if that fails, use application/octet-stream as the default content type
            string mimeType = string.Empty;
            if (dictResult.ContainsKey("mime_type")
                && dictResult["mime_type"] != null
                && !string.IsNullOrWhiteSpace(dictResult["mime_type"]?.ToString()))
            {
                mimeType = dictResult["mime_type"]!.ToString()!;
            }
            if (string.IsNullOrWhiteSpace(mimeType))
            {
                var provider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
                if (!provider.TryGetContentType(fileName, out mimeType!))
                {
                    mimeType = "application/octet-stream";
                }
            }

            #endregion

            #region base64 content source handling


            if (dictResult.ContainsKey("base64_content")
                && dictResult.TryGetValue("base64_content", out object value) && value is string base64Content
                && !string.IsNullOrWhiteSpace(base64Content)
                )
            {
                // var base64Content = dictResult["base64_content"]?.ToString() ?? string.Empty;
                //if (string.IsNullOrWhiteSpace(base64Content))
                //{
                //    return NotFound(new
                //    {
                //        success = false,
                //        message = $"No file content found to download for route `{HttpContext.Items["route"]}` (Contact your service provider support and provide them with error code `{_errorCode}`)"
                //    });
                //}
                var fileBytes = Convert.FromBase64String(base64Content);
                return File(fileBytes, mimeType, fileName);
            }
            #endregion
            else if (dictResult.ContainsKey("relative_path"))
            {
                var relativePath = dictResult["relative_path"] as string;
                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    relativePath = fileName;
                }
                // get IConfigurationSection for local_file_store from context items
                if (HttpContext.Items.ContainsKey("local_file_store_section"))
                {
                    var localStoreSection = HttpContext.Items["local_file_store_section"] as IConfigurationSection;
                    if (localStoreSection != null
                         && localStoreSection.Exists())
                    {
                        var basePath = localStoreSection.GetValue<string>("base_path") ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(basePath)) basePath = AppContext.BaseDirectory;
                        if (!string.IsNullOrWhiteSpace(basePath))
                        {
                            relativePath = Path.Combine(basePath, relativePath).UnifyPathSeperator();
                        }
                        if (!System.IO.File.Exists(relativePath))
                        {
                            return NotFound(new
                            {
                                success = false,
                                message = $"File not found at relative path `{relativePath}` for route `{HttpContext.Items["route"]}` (Contact your service provider support and provide them with error code `{_errorCode}`)"
                            });
                        }

                        // Use async file stream with proper buffering
                        var fileStream = new FileStream(relativePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
                        return File(fileStream, mimeType, fileName);
                    }
                }
                if (HttpContext.Items.ContainsKey("sftp_file_store_section"))
                {
                    var sftpStoreSection = HttpContext.Items["sftp_file_store_section"] as IConfigurationSection;
                    if (sftpStoreSection != null
                         && sftpStoreSection.Exists())
                    {
                        var basePath = HttpContext.Items["base_path"] as string;
                        if (string.IsNullOrWhiteSpace(basePath)) basePath = "";
                        else
                        {
                            relativePath = Path.Combine(basePath, relativePath)
                                .UnifyPathSeperator()
                                .Replace("\\", "/");
                        }
                        string host = sftpStoreSection.GetValue<string>("host") ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(host))
                        {
                            return NotFound(new
                            {
                                success = false,
                                message = $"SFTP host not defined for route `{HttpContext.Items["route"]}` (Contact your service provider support and provide them with error code `{_errorCode}`)"
                            });
                        }

                        int port = sftpStoreSection.GetValue<int?>("port") ?? 22;
                        string username = sftpStoreSection.GetValue<string>("username") ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(username))
                        {
                            return NotFound(new
                            {
                                success = false,
                                message = $"SFTP username not defined for route `{HttpContext.Items["route"]}` (Contact your service provider support and provide them with error code `{_errorCode}`)"
                            });
                        }

                        string password = sftpStoreSection.GetValue<string>("password") ?? string.Empty;

                        var sftpClient = new Com.H.Net.Ssh.SFtpClient(
                            host,
                            port,
                            username,
                            password
                            );
                        // register sftpClient for disposal
                        HttpContext.Response.RegisterForDisposeAsync(sftpClient);


                        var stream = await sftpClient.DownloadAsStreamAsync(
                            relativePath,
                            HttpContext.RequestAborted
                            );


                        if (stream == null)
                        {
                            return NotFound(new
                            {
                                success = false,
                                message = $"File not found at relative path `{relativePath}` for route `{HttpContext.Items["route"]}` (Contact your service provider support and provide them with error code `{_errorCode}`)"
                            });
                        }
                        return File(stream, mimeType, fileName);


                    }
                }



            }
            else if (dictResult.ContainsKey("http"))
            {
                var httpUrl = dictResult["http"]?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(httpUrl)
                    || !Uri.IsWellFormedUriString(httpUrl, UriKind.Absolute))
                {
                    return NotFound(new
                    {
                        success = false,
                        message = $"Invalid HTTP URL `{httpUrl}` for route `{HttpContext.Items["route"]}` (Contact your service provider support and provide them with error code `{_errorCode}`)"

                    });
                }

                using (HttpClient httpClient = new HttpClient())
                {
                    var response = await httpClient.GetAsync(httpUrl, HttpCompletionOption.ResponseHeadersRead, HttpContext.RequestAborted);
                    if (!response.IsSuccessStatusCode)
                    {
                        return StatusCode((int)response.StatusCode, new
                        {
                            success = false,
                            message = $"Failed to download file from `{httpUrl}` for route `{HttpContext.Items["route"]}` (Contact your service provider support and provide them with error code `{_errorCode}`)"
                        });
                    }

                    // Stream the content instead of loading into memory
                    var stream = await response.Content.ReadAsStreamAsync(HttpContext.RequestAborted);
                    mimeType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
                    return File(stream, mimeType, fileName);
                }
            }
            return NotFound(new
            {
                success = false,
                message = $"No valid file content source found to download for route `{HttpContext.Items["route"]}` (Contact your service provider support and provide them with error code `{_errorCode}`)"
            });
        }
    }

}

