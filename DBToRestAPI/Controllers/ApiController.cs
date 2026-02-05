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
        ILogger<ApiController> logger
            ) : ControllerBase
    {
        private readonly IEncryptedConfiguration _configuration = configuration;
        private readonly DbConnectionFactory _dbConnectionFactory = dbConnectionFactory;

        private readonly IQueryConfigurationParser _queryConfigurationParser = queryConfigurationParser;
        private readonly IHttpRequestExecutor _httpRequestExecutor = httpRequestExecutor;

        private readonly ILogger<ApiController> _logger = logger;


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
                IActionResult response;

                if (queries.Count == 1)
                {
                    // Single query: use existing GetResultFromDbAsync (backward compatible)
                    var query = queries[0];
                    DbConnection connection = string.IsNullOrWhiteSpace(query.ConnectionStringName) ?
                        _dbConnectionFactory.Create() :
                        _dbConnectionFactory.Create(query.ConnectionStringName);

                    // Register connection for disposal when response completes
                    // This ensures the connection is returned to the pool even if streaming fails
                    HttpContext.Response.RegisterForDisposeAsync(connection);

                    response = await _settings.CacheService
                        .GetQueryResultAsync<IActionResult>(
                        section,
                        qParams,
                        disableDiffered => GetResultFromDbAsync(section, connection, query.QueryText, qParams, disableDiffered),
                        HttpContext.RequestAborted
                        );
                }
                else
                {
                    // Multiple queries: use chained query execution
                    response = await _settings.CacheService
                        .GetQueryResultAsync<IActionResult>(
                        section,
                        qParams,
                        disableDiffered => GetResultFromDbMultipleQueriesAsync(section, queries, qParams, disableDiffered),
                        HttpContext.RequestAborted
                        );
                }

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

                    var errorMsg = $"====== exception ======{Environment.NewLine}"
                        + $"{ex.Message}{Environment.NewLine}{Environment.NewLine}"
                        + $"====== stack trace ====={Environment.NewLine}"
                        + $"{ex.StackTrace}{Environment.NewLine}{Environment.NewLine}";

                    this.Response.ContentType = "query/plain";
                    this.Response.StatusCode = 500;
                    await this.Response.WriteAsync(errorMsg);
                    await this.Response.CompleteAsync();
                    // IMPORTANT: Return immediately after writing to response to avoid
                    // the BadRequest below trying to set StatusCode on an already-started response
                    return new EmptyResult();
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
            // Check if there is an embedded http call within the query to resolve it before executing the main query.
            // Look for http regex in `http_variable_pattern` within the section to use for identifying http call variables.
            // If not found, try to get it from the global configuration, and if not found there, use the default pattern.
            var httpVariablePattern = section.GetValue<string?>("http_variable_pattern")
                ?? this._configuration.GetValue<string?>("http_variable_pattern")
                ?? Settings.DefaultRegex.DefaultHttpVariablesPattern;

            // Use Singleline mode to allow JSON content to span multiple lines.
            // Use Distinct to avoid executing the same HTTP request multiple times when the same
            // marker appears multiple times in the query. String.Replace will naturally replace
            // all occurrences of the same marker with the result.
            var matches = System.Text.RegularExpressions.Regex.Matches(
                query,
                httpVariablePattern,
                System.Text.RegularExpressions.RegexOptions.Singleline);

            // Get distinct matches by their full value (includes markers + param)
            // This ensures identical HTTP call definitions are only executed once
            // DistinctBy is more efficient than GroupBy().Select(g => g.First()) as it uses
            // an internal HashSet without allocating intermediate IGrouping objects
            var distinctMatches = matches
                .Cast<System.Text.RegularExpressions.Match>()
                .Where(m => m.Groups["param"] != null && !string.IsNullOrWhiteSpace(m.Groups["param"].Value))
                .DistinctBy(m => m.Value)
                .ToList();

            if (distinctMatches.Count < 1)
            {
                return query;
            }

            var dbQueryParams = new DbQueryParams()
            {
                DataModel = new Dictionary<string, string>(),
                QueryParamsRegex = @"(?<open_marker>\{http_internally_replaced\{)(?<param>.*?)?(?<close_marker>\}\})"
            };

            int count = 0;
            foreach (var matched in distinctMatches)
            {
                var httpRequestDetails = matched.Groups["param"].Value;

                // Fill any variables within the httpRequestDetails using the existing parameters
                httpRequestDetails = httpRequestDetails.Fill(qParams);

                // Execute the HTTP call
                HttpExecutorResponse response = await this._httpRequestExecutor.ExecuteAsync(
                    httpRequestDetails, 
                    this.HttpContext.RequestAborted);

                // Check for HTTP errors and log them
                if (!response.IsSuccess)
                {
                    _logger.LogWarning(
                        "Embedded HTTP call failed with status {StatusCode}: {ErrorMessage}. " +
                        "The marker will be replaced with NULL in the SQL query.",
                        response.StatusCode,
                        response.ErrorMessage);
                    // Leave the original marker - it will be replaced with DbNull by Com.H.Data.Common
                    continue;
                }

                var responseContent = response.ContentAsString;
                if (string.IsNullOrWhiteSpace(responseContent))
                {
                    _logger.LogDebug("Embedded HTTP call returned empty content. Marker will be replaced with NULL.");
                    continue;
                }

                count++;
                var placeholderName = $"http_response_{count}";

                // Add variable to the existing qParams so it can be used in the main query
                (dbQueryParams.DataModel as Dictionary<string, string>)!.Add(
                    placeholderName, 
                    responseContent);

                // Replace ALL occurrences of this marker in the query
                // Since we're iterating over distinct matches, duplicates are handled automatically
                query = query.Replace(matched.Value, $"{{http_internally_replaced{{{placeholderName}}}}}");
            }

            if (count > 0)
            {
                qParams.Add(dbQueryParams);
            }
            else
            {
                // Add an empty variable with marker regex to avoid issues in subsequent processing steps
                // where the Com.H.Data.Common library won't be able to fill markers that don't 
                // have values with DbNull since it doesn't know how the markers look like
                qParams.Add(new DbQueryParams
                {
                    DataModel = null,
                    QueryParamsRegex = httpVariablePattern
                });
            }

            return query;
        }


        /// <summary>
        /// Executes a database query and returns the result as an <see cref="ObjectResult"/>.
        /// </summary>
        /// <param name="serviceQuerySection">The configuration section for the specific service query.</param>
        /// <param name="query">The SQL query to be executed.</param>
        /// <param name="qParams">A list of query parameters to be used in the query.</param>
        /// <param name="disableDifferredExecution">For caching purposes, retrieves all records in memory if enabled so they could be placed in a cache mechanism</param>
        /// <returns>An <see cref="ObjectResult"/> containing the result of the query execution.</returns>
        public async Task<IActionResult> GetResultFromDbAsync(
            IConfigurationSection serviceQuerySection,
            DbConnection connection,
            string query,
            List<DbQueryParams> qParams,
            bool disableDifferredExecution = false
            )
        {
            int? dbCommandTimeout =
                serviceQuerySection.GetValue<int?>("db_command_timeout") ??
                this._configuration.GetValue<int?>("db_command_timeout");

            // check if count query is defined
            var countQuery = serviceQuerySection.GetSection("count_query")?.Value;

            var customSuccessStatusCode = serviceQuerySection.GetValue<int?>("success_status_code") ??
                this._configuration.GetValue<int?>("success_status_code") ?? 200;

            // root node name for wrapping the result (if configured - helps with legacy APIs that wraps results within an object)
            // this is experimential and may be removed in future releases in favor of 
            // giving users more control over response structure by defining custom json templates
            // and identifying where the results should be placed within the template
            // for now we just support a single root node name for wrapping the result set
            // This feature will be left undocumented in readme.md for now until the template feature is implemented
            // to be used right now for only specific legacy use cases
            string? rootNodeName = serviceQuerySection.GetValue<string?>("root_node")
                ?? this._configuration.GetValue<string?>("root_node") ?? null;


            if (string.IsNullOrWhiteSpace(countQuery))
            {
                var responseStructure = serviceQuerySection.GetValue<string>("response_structure")?.ToLower() ??
                    this._configuration.GetValue<string>("response_structure")?.ToLower() ?? "auto";

                // check if `response_structure` is valid (valid values are `array`, `single`, `auto`, `file`)
                if (!_responseStructures.Contains(responseStructure, StringComparer.OrdinalIgnoreCase))
                {
                    return StatusCode(500, new
                    {
                        success = false,
                        message = $"Invalid response structure `{responseStructure}` defined for route "
                        + $"`{HttpContext.Items["route"]}` (Contact your service provider support and provide them with error code `{_errorCode}`)"
                    });
                }

                query = await PrepareEmbeddedHttpCallsParamsIfAny(query, qParams, serviceQuerySection);

                var resultWithNoCount = await connection.ExecuteQueryAsync(query, qParams, commandTimeout: dbCommandTimeout, cToken: HttpContext.RequestAborted);
                // perhaps here is the right place to register for disposal
                if (resultWithNoCount != null)
                {
                    HttpContext.Response.RegisterForDisposeAsync(resultWithNoCount);
                }

                HttpContext.RequestAborted.ThrowIfCancellationRequested();


                if (responseStructure == "array")
                {

                    if (disableDifferredExecution)
                    {
                        if (!string.IsNullOrWhiteSpace(rootNodeName))
                        {
                            // wrap the result in an object with the root node name
                            var wrappedResult = new ExpandoObject();
                            wrappedResult.TryAdd(rootNodeName, resultWithNoCount?.AsEnumerable().ToArray());
                            return StatusCode(customSuccessStatusCode, wrappedResult);
                        }

                        return StatusCode(customSuccessStatusCode,
                            resultWithNoCount.AsEnumerable().ToArray());
                    }
                    if (!string.IsNullOrWhiteSpace(rootNodeName))
                    {
                        // wrap the result in an object with the root node name
                        var wrappedResult = new ExpandoObject();
                        wrappedResult.TryAdd(rootNodeName, resultWithNoCount);
                        return StatusCode(customSuccessStatusCode, wrappedResult);
                    }
                    return StatusCode(customSuccessStatusCode, resultWithNoCount);

                }
                if (responseStructure == "single")
                {
                    // if response structure is single, then return the first record


                    var singleResult = resultWithNoCount.AsEnumerable().FirstOrDefault();
                    // close the reader
                    await resultWithNoCount.CloseReaderAsync();
                    if (!string.IsNullOrWhiteSpace(rootNodeName))
                    {
                        // wrap the result in an object with the root node name
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
                    // if response structure is auto, then return an array if there are more than one record
                    // and a single record if there is only one record
                    // ToChamberedAsyncEnumerable is a custom extension method that returns an enumerable that have 
                    // some of its items already read into memory. In the case below, the extension method is
                    // instructed to read 2 items into memory and keep the remaining (if any) in the enumerable.
                    // The returned enumerable from the extension method should have a `ChamberedCount` property
                    // matching that of the items count its instructed to read into memory.
                    // If the `ChamberedCount` is less than 2, then this indicates that there is only one (or zero) record
                    // available in the enumerable (i.e., the enumerable is exhausted, in other words ran out of items to iterate through
                    // before it got to our `ChamberedCount` limit).
                    // In this case, we return the first record if it exists, or an empty resultWithNoCount.

                    var chamberedResult = await resultWithNoCount.ToChamberedEnumerableAsync(2, HttpContext.RequestAborted);

                    HttpContext.RequestAborted.ThrowIfCancellationRequested();

                    if (chamberedResult.WasExhausted(2))
                    {
                        var singleResult = chamberedResult.AsEnumerable().FirstOrDefault();
                        // close the reader
                        await resultWithNoCount.CloseReaderAsync();
                        if (!string.IsNullOrWhiteSpace(rootNodeName))
                        {
                            // wrap the result in an object with the root node name
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
                            // wrap the result in an object with the root node name
                            var wrappedResult = new ExpandoObject();
                            wrappedResult.TryAdd(rootNodeName,
                                disableDifferredExecution ?
                                chamberedResult.AsEnumerable().ToArray()
                                :
                                chamberedResult);
                            return StatusCode(customSuccessStatusCode, wrappedResult);
                        }

                        return StatusCode(customSuccessStatusCode,
                            disableDifferredExecution ?
                            chamberedResult.AsEnumerable().ToArray()
                            :
                            chamberedResult);
                    }
                }
                return StatusCode(500, new
                {
                    success = false,
                    message = $"Invalid response structure `{responseStructure}` defined for route `{HttpContext.Items["route"]}` (Contact your service provider support and provide them with error code `{_errorCode}`)"
                });
            }

            var resultCount = await connection.ExecuteQueryAsync(countQuery, qParams, commandTimeout: dbCommandTimeout, cToken: HttpContext.RequestAborted);
            // Register for disposal to ensure DbDataReader is properly cleaned up
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
                    message = $"Count query `{countQuery}` did not return any records for route `{HttpContext.Items["route"]}` (Contact your service provider support and provide them with error code `{_errorCode}`)"
                });
            }
            // close the reader for the count query
            await resultCount.CloseReaderAsync();


            var result = await connection.ExecuteQueryAsync(query, qParams, commandTimeout: dbCommandTimeout, cToken: HttpContext.RequestAborted);
            // Register for disposal to ensure DbDataReader is properly cleaned up
            if (result != null)
            {
                HttpContext.Response.RegisterForDisposeAsync(result);
            }

            HttpContext.RequestAborted.ThrowIfCancellationRequested();

            if (disableDifferredExecution)
            {

                if (!string.IsNullOrWhiteSpace(rootNodeName))
                {
                    // wrap the result in an object with the root node name
                    var wrappedResult = new ExpandoObject();
                    wrappedResult.TryAdd(rootNodeName,
                        new
                        {
                            success = true,
                            count = rowCount,
                            data = result.AsEnumerable().ToArray()
                        }
                        );
                    return StatusCode(customSuccessStatusCode, wrappedResult);
                }

                // if disableDifferredExecution is true, then we want to read all records into memory
                // so that we can cache them
                return StatusCode(customSuccessStatusCode,
                    new
                    {
                        success = true,
                        count = rowCount,
                        data = result.AsEnumerable().ToArray()
                    });
            }

            if (!string.IsNullOrWhiteSpace(rootNodeName))
            {
                // wrap the result in an object with the root node name
                var wrappedResult = new ExpandoObject();
                wrappedResult.TryAdd(rootNodeName,
                    new
                    {
                        success = true,
                        count = rowCount,
                        data = await result.ToChamberedEnumerableAsync()
                    }
                    );
                return StatusCode(customSuccessStatusCode, wrappedResult);
            }


            return StatusCode(customSuccessStatusCode,
                new
                {
                    success = true,
                    count = rowCount,
                    data = await result.ToChamberedEnumerableAsync()
                });
        }

        /// <summary>
        /// Executes a chain of database queries, passing results between them.
        /// Each query can target a different database via its ConnectionStringName.
        /// Only the final query's result is returned to the client.
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
                        // Intermediate query: execute, materialize, and add result to qParams
                        var result = await connection.ExecuteQueryAsync(
                            query.QueryText,
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
        /// Executes the final query in a chain and builds the HTTP response.
        /// This is a helper method that contains the response-building logic extracted from GetResultFromDbAsync.
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

            // With count query
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
            await resultCount.CloseReaderAsync();

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

