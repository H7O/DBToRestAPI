using DBToRestAPI.Services;
using DBToRestAPI.Settings;
using DBToRestAPI.Settings.Extensinos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace DBToRestAPI.Middlewares
{
    /// <summary>
    /// Validates mandatory request parameters and prepares data for processing.
    /// 
    /// This middleware parses the request body, validates that all mandatory fields are present,
    /// and prepares the parameters for subsequent middleware (typically database query execution).
    /// 
    /// Required context.Items from previous middlewares:
    /// - `route`: String representing the matched route path
    /// - `section`: IConfigurationSection for the route's configuration
    /// - `service_type`: String indicating service type (`api_gateway` or `db_query`)
    /// 
    /// The middleware:
    /// - Enables request body buffering for multiple reads
    /// - Parses and validates JSON payload format
    /// - Extracts parameters from query string, body, and headers
    /// - Validates mandatory parameters as defined in route configuration
    /// - Prepares parameter collection for database queries or further processing
    /// 
    /// Sets in context.Items for next middleware:
    /// - `parameters`: Complete collection of parameters from all sources (query string, body, headers)
    /// - `payload`: Parsed JSON payload string (if body contains valid JSON)
    /// 
    /// Responses:
    /// - 400 Bad Request: Invalid JSON format, missing mandatory fields, or request cancelled
    /// - 500 Internal Server Error: Required context items missing from previous middlewares
    /// - Passes to next middleware: All validations successful, parameters prepared
    /// </summary>

    public class Step6MandatoryFieldsCheck(
        RequestDelegate next,
        SettingsService settings,
        ILogger<Step6MandatoryFieldsCheck> logger,
        ParametersBuilder paramsBuilder)
    {
        private readonly RequestDelegate _next = next;
        private readonly SettingsService _settings = settings;
        private readonly ILogger<Step6MandatoryFieldsCheck> _logger = logger;
        private readonly ParametersBuilder _paramsBuilder = paramsBuilder;
        private static readonly string _errorCode = "Step 6 - Mandatory Fields Check Error";
        public async Task InvokeAsync(HttpContext context)
        {

            #region log the time and the middleware name

            this._logger.LogDebug("{time}: in Step5MandatoryFieldsCheck middleware",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffff"));

            #endregion

            #region if no section passed from the previous middlewares, return 500
            IConfigurationSection? section = context.Items.ContainsKey("section")
                ? context.Items["section"] as IConfigurationSection
                : null;


            if (section == null)
            {
                await context.Response.DeferredWriteAsJsonAsync(
                    new ObjectResult(
                        new
                        {
                            success = false,
                            message = $"Improper service setup. (Contact your service provider support and provide them with error code `{_errorCode}`)"
                        }
                    )
                    {
                        StatusCode = 500
                    }
                );

                return;
            }

            #endregion

            #region Get content type from context
            // Content type was validated and stored in context.Items by Step2ServiceTypeChecks
            var contentType = context.Items.ContainsKey("content_type")
                ? context.Items["content_type"] as string
                : null;

            if (string.IsNullOrWhiteSpace(contentType))
            {
                await context.Response.DeferredWriteAsJsonAsync(
                    new ObjectResult(
                        new
                        {
                            success = false,
                            message = $"Content type not found in context. (Contact your service provider support and provide them with error code `{_errorCode}`)"
                        }
                    )
                    {
                        StatusCode = 500
                    }
                );

                return;
            }
            #endregion


            #region check if the request was cancelled
            if (context.RequestAborted.IsCancellationRequested)
            {
                await context.Response.DeferredWriteAsJsonAsync(
                    new ObjectResult(
                        new
                        {
                            success = false,
                            message = "Request was cancelled"
                        }
                    )
                    {
                        StatusCode = 400
                    }
                );

                return;
            }
            #endregion
            // retrieve the parameters (which consists of route, query string, form data, json body, and headers parameters)
            var (qParams, paramsError) = await this._paramsBuilder.GetParamsOrErrorAsync(_errorCode);
            if (paramsError != null)
            {
                await context.Response.DeferredWriteAsJsonAsync(paramsError);
                return;
            }

            if (qParams == null)
            {
                await context.Response.DeferredWriteAsJsonAsync(
                    new ObjectResult(
                        new
                        {
                            success = false,
                            message = "Failed to extract parameters from the request"
                        }
                    )
                    {
                        StatusCode = 400
                    }
                );
                return;
            }

            #region check if there are any mandatory parameters missing
            var failedMandatoryCheckResponse = this._settings
                .GetFailedMandatoryParamsCheckIfAny(section, qParams);
            if (failedMandatoryCheckResponse != null)
            {
                // Check if response has already started before writing error response
                if (context.Response.HasStarted)
                {
                    _logger.LogWarning(
                        "{Time}: Cannot write mandatory fields error response - response has already started. " +
                        "Route: {Route}, StatusCode attempted: {StatusCode}",
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                        context.Items.TryGetValue("route", out var route) ? route : "unknown",
                        failedMandatoryCheckResponse.StatusCode);
                    return;
                }
                await context.Response.DeferredWriteAsJsonAsync(failedMandatoryCheckResponse);
                return;
            }
            #endregion

            try
            {
                await _next(context);
            }
            catch (Exception ex)
            {
                // Log detailed error information to help diagnose issues
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                var route = context.Items.TryGetValue("route", out var r) ? r?.ToString() : "unknown";
                var serviceType = context.Items.TryGetValue("service_type", out var st) ? st?.ToString() : "unknown";
                var hasResponseStarted = context.Response.HasStarted;
                var currentStatusCode = context.Response.StatusCode;

                _logger.LogError(ex,
                    "{Time}: Exception in Step6MandatoryFieldsCheck pipeline. " +
                    "Route: {Route}, ServiceType: {ServiceType}, " +
                    "ResponseHasStarted: {HasStarted}, CurrentStatusCode: {StatusCode}, " +
                    "ExceptionType: {ExceptionType}, Message: {Message}",
                    timestamp, route, serviceType, hasResponseStarted, currentStatusCode,
                    ex.GetType().Name, ex.Message);

                // Only attempt to write error response if nothing of the response exists yet. Not
                // started isn't enough: MVC's JSON writer hands its buffer to the server before the
                // first flush, so HasStarted can still be false with part of a streamed result in
                // the response pipe, and an error body written now would follow those rows: the
                // client would get the error status with invalid JSON. Treat that like a started
                // response.
                var bodyWriter = context.Response.BodyWriter;
                var hasBufferedBody = bodyWriter.CanGetUnflushedBytes && bodyWriter.UnflushedBytes > 0;
                if (!context.Response.HasStarted && !hasBufferedBody)
                {
                    try
                    {
                        await context.Response.DeferredWriteAsJsonAsync(
                            LateErrorResponse(
                                ex,
                                _settings.IsDebugMode(context.Request),
                                _settings.GetDefaultGenericErrorMessage()));
                    }
                    catch (Exception writeEx)
                    {
                        _logger.LogError(writeEx,
                            "{Time}: Failed to write error response in Step6MandatoryFieldsCheck. " +
                            "Route: {Route}, Original exception: {OriginalMessage}",
                            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), route, ex.Message);
                    }
                }
                else
                {
                    // Part of a success response has been sent or buffered, so the status can't
                    // change any more. Abort the connection: ending the response normally would hand
                    // the client a 200 whose body is cut off, which reads like a success.
                    _logger.LogWarning(
                        "{Time}: Cannot write error response in Step6MandatoryFieldsCheck - response already started or buffered; "
                        + "aborting the connection. Route: {Route}, OriginalException: {ExceptionType}",
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), route, ex.GetType().Name);
                    context.Abort();
                }
            }

        }

        /// <summary>
        /// The response for an exception that escaped the controller before anything was sent.
        /// </summary>
        /// <remarks>
        /// A streamed result (`array` or `auto` with two or more rows, a count query's data) is read
        /// while MVC writes the response, after the controller has returned. A database error raised
        /// after the rows (SQL Server's THROW after a SELECT, or a later statement that fails) then
        /// surfaces here, outside the controller's catch, and used to become a 500 "unexpected
        /// error". It now gets the controller's response: the status the SQL asked for (50000 + n),
        /// the generic 400 for any other database error, or the details in debug mode. Anything
        /// that isn't a database error is still the 500.
        /// </remarks>
        internal static ObjectResult LateErrorResponse(Exception ex, bool debugMode, string genericErrorMessage)
        {
            if (Controllers.ApiController.TryGetCustomDbError(ex, out var errorNumber, out var errorMessage))
            {
                var status = errorNumber - 50000;
                return new ObjectResult(new { success = false, message = errorMessage, error_number = status })
                {
                    StatusCode = status
                };
            }

            if (ex is System.Data.Common.DbException || ex.InnerException is System.Data.Common.DbException)
            {
                if (debugMode)
                {
                    return new ObjectResult(new
                    {
                        success = false,
                        message = ex.Message,
                        stack_trace = ex.StackTrace,
                        inner_exception = ex.InnerException?.Message
                    })
                    {
                        StatusCode = 500
                    };
                }
                return new ObjectResult(new { success = false, message = genericErrorMessage })
                {
                    StatusCode = 400
                };
            }

            return new ObjectResult(new
            {
                success = false,
                message = "An unexpected error occurred processing your request"
            })
            {
                StatusCode = 500
            };
        }
    }
}
