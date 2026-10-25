using Azure;
using Azure.Core;
using Com.H.Data;
using DBToRestAPI.Cache;
using DBToRestAPI.Services;
using DBToRestAPI.Settings;
using DBToRestAPI.Settings.Extensinos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DBToRestAPI.Middlewares
{
    /// <summary>
    /// Processes API gateway routing requests.
    /// 
    /// This middleware acts as a reverse proxy, forwarding requests to external APIs when the
    /// service type is 'api_gateway'. For other service types (e.g., 'db_query'), it passes 
    /// the request to the next middleware without modification.
    /// 
    /// Required context.Items from previous middlewares:
    /// - `route`: String representing the matched route path
    /// - `section`: IConfigurationSection for the route's configuration
    /// - `service_type`: String indicating service type (must be `api_gateway` for processing)
    /// - `remaining_path`: Additional path segments to append to target URL (for wildcard routes)
    /// 
    /// API Gateway functionality:
    /// - Constructs target URL from route configuration
    /// - Forwards HTTP method, headers, query strings, and request body
    /// - Supports header exclusion and override (per-route or default)
    /// - Handles SSL certificate validation settings
    /// - Streams response back to the caller with appropriate headers
    /// 
    /// Configuration options per route:
    /// - `url`: Target API endpoint (required)
    /// - `excluded_headers`: Headers to exclude when forwarding
    /// - `applied_headers`: Headers to add/override in forwarded request
    /// - `ignore_target_route_certificate_errors`: Allow self-signed/invalid certificates
    /// 
    /// Responses:
    /// - Proxied response: Returns the exact response from the target API
    /// - 500 Internal Server Error: Missing configuration or improper setup
    /// - Passes to next middleware: Service type is not 'api_gateway'
    /// </summary>

    public class Step5APIGatewayProcess(
        RequestDelegate next,
        IEncryptedConfiguration settingsEncryptionService,
        SettingsService settings,
        IHttpClientFactory httpClientFactory,
        ILogger<Step5APIGatewayProcess> logger,
        ParametersBuilder paramsBuilder
            )
    {
        private readonly RequestDelegate _next = next;
        private readonly SettingsService _settings = settings;
        private readonly IEncryptedConfiguration _configuration = settingsEncryptionService;
        private readonly IHttpClientFactory httpClientFactory = httpClientFactory;
        private readonly ILogger<Step5APIGatewayProcess> _logger = logger;
        private readonly ParametersBuilder _paramsBuilder = paramsBuilder;
        /// <summary>
        /// Headers that should not be copied from the target response to the client response.
        /// These headers are managed by ASP.NET Core and manually setting them could cause issues.
        /// </summary>
        private static readonly string[] _excludedResponseHeaders = new string[] { "Transfer-Encoding", "Content-Length" };
        private static readonly string _errorCode = "Step 5 - Gateway Process Error";

        // List of headers that belong to Content, not to the request itself
        private static readonly HashSet<string> _contentHeaderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Content-Type", "Content-Length", "Content-Encoding", "Content-Language",
            "Content-Location", "Content-MD5", "Content-Range", "Content-Disposition"
        };



        public async Task InvokeAsync(HttpContext context)
        {

            #region log the time and the middleware name
            if (this._logger.IsEnabled(LogLevel.Debug))
                this._logger.LogDebug("{time}: in Step4APIGatewayProcess middleware",
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
                            // SMWSE6: standard middleware section error
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

            #region if no service type passed from the previous middlewares, return 500


            //var containsServiceType = context.Items.ContainsKey("service_type")
            //    && context.Items["serivce_type"] is string serviceType
            //    && !string.IsNullOrWhiteSpace(serviceType);

            //this._logger.LogDebug("Contains service type: {containsServiceType}",
            //    containsServiceType);

            if (!context.Items.ContainsKey("service_type"))
            {
                await context.Response.DeferredWriteAsJsonAsync(
                    new ObjectResult(
                        new
                        {
                            success = false,
                            // SMWSTE6: standard middleware service type error 6
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

            #region if service type is not `api_gateway`, call the next middleware
            if (context.Items["service_type"] as string != "api_gateway")
            {
                await _next(context);
                return;
            }

            #endregion

            #region url check
            var url = section.GetValue<string>("url");

            if (string.IsNullOrWhiteSpace(url))
            {

                await context.Response.DeferredWriteAsJsonAsync(
                    new ObjectResult(
                        new
                        {
                            success = false,
                            message = $"Improper route settings (missing `url`). (Contact your service provider support and provide them with error code `{_errorCode}`)"
                        }
                    )
                    {
                        StatusCode = 500
                    }
                );
                return;
            }

            #endregion

            #region get parameters
            // retrieve the parameters (which consists of query string parameters and headers)
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
                            message = $"Improper parameters setup. (Contact your service provider support and provide them with error code `{_errorCode}`)"
                        }
                    )
                    {
                        StatusCode = 500
                    }
                );
                return;
            }


            #endregion

            #region check if there are any mandatory parameters missing
            var failedMandatoryCheckResponse = this._settings
                .GetFailedMandatoryParamsCheckIfAny(section, qParams);
            if (failedMandatoryCheckResponse != null)
            {
                await context.Response.DeferredWriteAsJsonAsync(failedMandatoryCheckResponse);
                return;
            }
            #endregion


            #region get remaining path and apply it to the url
            var remainingPath = context.Items["remaining_path"] as string;
            if (!string.IsNullOrWhiteSpace(remainingPath))
            {
                // url might have a query string, 
                // so we need to insert the remaining path
                // before the query string or append it to the url
                // if there is no query string.
                if (!url.Contains('?'))
                    url += remainingPath;
                else
                    // insert the remaining path before the query string `?`
                    url = url.Insert(url.IndexOf('?'), remainingPath);
            }
            #endregion

            #region get caller's query string

            // check if `this.Request` has query string
            // if queryString has values, append it to the url, and if the url already has a query string, append it with `&`
            if (!string.IsNullOrWhiteSpace(context.Request.QueryString.Value))
            {
                url += string.Concat(url.Contains('?') ? "&" : "?", context.Request.QueryString.Value.AsSpan(1));
                // the above is equivalent to:
                // url += (url.Contains("?") ? "&" : "?") + context.Request.QueryString.Value.Substring(1);
            }

            #endregion

            #region get resolved route for cache key
            var resolvedRoute = context.Items["route"] as string ?? string.Empty;
            #endregion

            #region cache implementation
            try
            {
                // Built from this request now: a cache fill forwards it later, possibly after this
                // request has ended.
                var targetRequestMsg = await BuildTargetRequestAsync(section, context, url,
                    copyBody: _settings.CacheService.CachesGatewayRequest(section, context, resolvedRoute));
                CachableHttpResponseContainer? notStored = null;
                var cachedResponse = await _settings.CacheService.GetForGateway<CachableHttpResponseContainer>(
                    section,
                    context,
                    resolvedRoute,
                    (disableStreaming, cancel) => ProcessApiGatewayRequestAsync(
                        section, context, targetRequestMsg, disableStreaming, cancel, response => notStored = response),
                    context.RequestAborted
                );

                // If response is null, it was streamed and already written, or this request's own cache
                // fill got a response that must not be stored, and handed it back here.
                if (cachedResponse == null)
                {
                    if (notStored != null)
                        await WriteHttpResponseFromCache(context, notStored);
                    return;
                }

                // Response came from cache or was buffered - write it to the client
                await WriteHttpResponseFromCache(context, cachedResponse);
            }
            // Step1ServiceTypeChecks answers an invalid setting.
            catch (Exception ex) when (ex is not InvalidSettingException)
            {
                // A caller who left gets nothing more; the connection is already gone.
                if (Controllers.ApiController.CallerLeft(_logger, context, resolvedRoute, ex))
                    return;
                await context.Response.DeferredWriteAsJsonAsync(FailedForwardResponse(context, ex, resolvedRoute, url));
            }
            #endregion

        }

        /// <summary>
        /// The answer to a forward that failed, which is logged. A target that didn't answer within
        /// `target_route_timeout_seconds` is a 504, and one that couldn't be reached or whose answer
        /// couldn't be read (no such host, a refused or dropped connection, a TLS failure) is a 502, each
        /// with the generic message. A caller's body the server refused to read keeps its status (413 for
        /// one that is too large): the client wraps that refusal in its own exception while it sends the
        /// body. Anything else is the generic 400, and in debug mode every failure gets its details.
        /// </summary>
        private ObjectResult FailedForwardResponse(HttpContext context, Exception ex, string route, string url)
        {
            var refused = ex;
            while (refused is not null and not BadHttpRequestException)
                refused = refused.InnerException;
            if (refused is BadHttpRequestException badRequest)
            {
                _logger.LogDebug("{Time}: Gateway route {Route} answered {Status}: the request body could not be read",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), route, badRequest.StatusCode);
                return new ObjectResult(new { success = false, message = "The request body could not be read." })
                {
                    StatusCode = badRequest.StatusCode
                };
            }

            // The caller is still there (CallerLeft), so a cancelled forward ran out of time. An answer
            // that ends early while it is read is an IOException of the client's.
            var status = ex switch
            {
                OperationCanceledException => StatusCodes.Status504GatewayTimeout,
                HttpRequestException or IOException => StatusCodes.Status502BadGateway,
                _ => StatusCodes.Status400BadRequest
            };
            var failure = status switch
            {
                StatusCodes.Status504GatewayTimeout => "its target didn't answer within target_route_timeout_seconds",
                StatusCodes.Status502BadGateway => "its target couldn't be reached or its answer couldn't be read",
                _ => "the forward failed"
            };
            _logger.LogError(LogText.Loggable(ex),
                "{Time}: Gateway route {Route} answered {Status}: {Failure}. Target: {Target}{EscapedException}",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), route, status, failure, LogText.Url(url),
                LogText.ExceptionText(ex));

            if (status != StatusCodes.Status400BadRequest && !_settings.IsDebugMode(context.Request))
                return new ObjectResult(new { success = false, message = _settings.GetDefaultGenericErrorMessage() })
                {
                    StatusCode = status
                };
            return _settings.GetExceptionResponse(context.Request, ex);
        }

        /// <summary>
        /// Builds the request to forward to the target URL from the caller's request.
        /// </summary>
        /// <remarks>
        /// A request the cache handles may be forwarded by a cache fill, which can outlive the
        /// caller's request, so its body is copied now (<paramref name="copyBody"/>). Any other
        /// request is forwarded within its own request, and streams its body.
        /// </remarks>
        private async Task<HttpRequestMessage> BuildTargetRequestAsync(
            IConfigurationSection section,
            HttpContext context,
            string url,
            bool copyBody)
        {
            #region prepare target request msg
            var targetRequestMsg = new HttpRequestMessage(new HttpMethod(context.Request.Method), url);

            // see if the request has a body
            if (context.Request.Body?.CanRead == true)
            {
                if (copyBody)
                {
                    // Grows with the bytes received, never with the length the caller claims.
                    var body = new MemoryStream();
                    await context.Request.Body.CopyToAsync(body, context.RequestAborted);
                    targetRequestMsg.Content = new ByteArrayContent(body.GetBuffer(), 0, (int)body.Length);
                }
                else
                {
                    targetRequestMsg.Content = new StreamContent(context.Request.Body);
                }
            }

            #endregion

            #region see if there are headers that should not be passed to the server for this particular route
            var excludeHeaders = section.GetValue<string>("excluded_headers")?
                .Split(new char[] { ',', ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (excludeHeaders == null || excludeHeaders.Length < 1)
                // if no headers to exclude for this route, check if there are default headers to exclude for all routes
                excludeHeaders = _configuration.GetValue<string>("excluded_headers")?
                    .Split(new char[] { ',', ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            #endregion

            #region see if there are headers that should be overridden for this particular route

            // Each child of <applied_headers> is one header with a <name> and a <value>, whatever the child
            // is called. Repeated siblings with one element name (<header> twice) are numbered by the
            // configuration reader, so the numbered children of a child without a <name> are headers too.
            // A name given twice keeps its last value.
            var appliedHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in section.GetSection("applied_headers").GetChildren()
                         .SelectMany(x => string.IsNullOrWhiteSpace(x.GetValue<string>("name")) ? x.GetChildren() : [x]))
            {
                var name = child.GetValue<string>("name");
                if (!string.IsNullOrWhiteSpace(name))
                    appliedHeaders[name] = child.GetValue<string>("value") ?? string.Empty;
            }
            // adding the override headers to the target request
            if (appliedHeaders?.Count > 0 == true)
            {
                foreach (var header in appliedHeaders)
                {
                    bool success;
                    // Content headers must be added to Content.Headers, not to request Headers
                    if (_contentHeaderNames.Contains(header.Key))
                    {
                        // Only add to Content.Headers if we have content
                        if (targetRequestMsg.Content != null)
                        {
                            success = targetRequestMsg.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                            if (!success)
                                this._logger.LogWarning("Failed to add `applied/overridden` content header `{headerKey}` to target request",
                                    header.Key);
                        }
                    }
                    else
                    {
                        success = targetRequestMsg.Headers.TryAddWithoutValidation(header.Key, header.Value);
                        if (!success)
                            this._logger.LogWarning("Failed to add `applied/overridden` header `{headerKey}` to target request",
                                header.Key);
                    }
                }
            }

            #endregion

            #region get headers from the caller request and add them to the target request
            foreach (var header in context.Request.Headers)
            {

                if (
                    // exclude headers that should not be passed to the server (make sure to accomodate for case sensitivity)
                    excludeHeaders?.Contains(header.Key, StringComparer.OrdinalIgnoreCase) == true
                    || appliedHeaders?.Select(x => x.Key).Contains(header.Key, StringComparer.OrdinalIgnoreCase) == true
                    )
                    continue;

                bool success;
                // Content headers must be added to Content.Headers, not to request Headers
                if (_contentHeaderNames.Contains(header.Key))
                {
                    // Only add to Content.Headers if we have content
                    if (targetRequestMsg.Content != null)
                    {
                        success = targetRequestMsg.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                        if (!success)
                            this._logger.LogWarning("Failed to add content header `{headerKey}` to target request",
                                header.Key);
                    }
                }
                else
                {
                    success = targetRequestMsg.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                    // The caller chose this name, and the server accepts control characters in one.
                    if (!success)
                        this._logger.LogWarning("Failed to add header `{headerKey}` to target request",
                            LogText.Escape(header.Key));
                }
            }
            #endregion

            return targetRequestMsg;
        }

        /// <summary>
        /// Processes the API gateway request by forwarding it to the target URL.
        /// </summary>
        /// <param name="section">Configuration section for the route.</param>
        /// <param name="context">The HTTP context, written to only when streaming.</param>
        /// <param name="targetRequestMsg">The request to forward, from <see cref="BuildTargetRequestAsync"/>.</param>
        /// <param name="disableStreaming">If true, buffers the entire response for caching. If false, streams directly to client.</param>
        /// <param name="cancel">The token to stop on: the cache's own in a cache fill, the request's otherwise.</param>
        /// <param name="handBackNotStored">Receives a buffered response that must not be stored.</param>
        /// <returns>CachableHttpResponseContainer if buffered and storable, null otherwise.</returns>
        /// <remarks>
        /// When buffering, this is a cache fill, which every caller with the same key waits on and
        /// which can outlive the request that started it. So it never touches that request: a
        /// response that must not be stored is handed back, and that request's caller gets it if it
        /// is still there.
        /// </remarks>
        private async Task<CachableHttpResponseContainer?> ProcessApiGatewayRequestAsync(
            IConfigurationSection section,
            HttpContext context,
            HttpRequestMessage targetRequestMsg,
            bool disableStreaming,
            CancellationToken cancel,
            Action<CachableHttpResponseContainer> handBackNotStored)
        {
            #region check if the target route certificate errors should be ignored
            var ignoreCertificateErrors = section.GetSetting<bool>("ignore_target_route_certificate_errors");
            // if no ignore certificate errors for this route, check if there are default ignore certificate errors for all routes
            ignoreCertificateErrors ??= _configuration.GetSetting<bool>("ignore_target_route_certificate_errors");

            var targetRouteTimeoutSeconds = section.GetSetting<int>("target_route_timeout_seconds")
                ?? _configuration.GetSetting<int>("target_route_timeout_seconds")
                ?? 30;
            if (targetRouteTimeoutSeconds < 1)
                targetRouteTimeoutSeconds = 30;
            #endregion

            using (var client = ignoreCertificateErrors == true
                ? httpClientFactory.CreateClient("ignoreCertificateErrors")
                : httpClientFactory.CreateClient("checkCertificateErrors")
                )
            {
                var timeout = TimeSpan.FromSeconds(targetRouteTimeoutSeconds);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                linkedCts.CancelAfter(timeout);
                // Only the headers: a streamed answer is copied as it arrives, never held whole.
                using var targetRouteResponse = await client.SendAsync(
                    targetRequestMsg, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);

                #region check if we should cache this status code
                var storable = true;
                var memorySection = section.GetSection("cache:memory");
                if (memorySection.Exists())
                {
                    var excludeStatusCodesCsv = memorySection.GetValue<string?>("exclude_status_codes_from_cache") ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(excludeStatusCodesCsv))
                    {
                        var excludedStatusCodes = excludeStatusCodesCsv
                            .Split(new char[] { ',', ' ', '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(x => int.TryParse(x, out var code) ? code : -1)
                            .Where(x => x >= 0)
                            .ToHashSet();

                        if (excludedStatusCodes.Contains((int)targetRouteResponse.StatusCode))
                        {
                            // This status code should not be cached
                            storable = false;
                        }
                    }
                }
                #endregion

                // A Set-Cookie belongs to the caller it was sent for, so a response that carries one
                // goes to that caller and is never stored for the next.
                if (targetRouteResponse.Headers.Contains("Set-Cookie"))
                    storable = false;

                if (disableStreaming)
                {
                    // Read whole, to be stored: all of it must arrive within the timeout.
                    var buffered = await CachableHttpResponseContainer.Parse(targetRouteResponse, linkedCts.Token);
                    if (storable)
                        return buffered;
                    handBackNotStored(buffered);
                    return null;
                }
                else
                {
                    // Stream directly to client (a route without a cache, or a caller forwarding on its own).
                    // The target's status and headers are set once the first block of its answer has
                    // arrived, so a target that fails before then gets its caller the failure's status.
                    var started = false;
                    void SetStatusAndHeaders()
                    {
                        started = true;
                        context.Response.StatusCode = (int)targetRouteResponse.StatusCode;

                        #region setup the response headers back to the caller
                        // Copy response headers (general headers)
                        foreach (var header in targetRouteResponse.Headers
                            .Where(x => !_excludedResponseHeaders.Contains(x.Key))
                            )
                        {
                            context.Response.Headers[header.Key] = header.Value.ToArray();
                        }

                        // Copy content headers (Content-Type, Content-Encoding, etc.)
                        foreach (var header in targetRouteResponse.Content.Headers
                            .Where(x => !_excludedResponseHeaders.Contains(x.Key))
                            )
                        {
                            context.Response.Headers[header.Key] = header.Value.ToArray();
                        }
                        #endregion
                    }

                    try
                    {
                        await CopyAnswerAsync(context, targetRouteResponse, linkedCts, timeout, cancel, SetStatusAndHeaders);
                    }
                    catch (Exception ex) when (started && !cancel.IsCancellationRequested)
                    {
                        // Part of the answer has been sent, so its status can't change any more. Cut the
                        // connection: ending the answer normally would hand the caller a cut-off body that
                        // reads like a whole one.
                        _logger.LogError(LogText.Loggable(ex),
                            "{Time}: Gateway route {Route} cut its caller's connection: {Failure} after part of it was sent. Target: {Target}{EscapedException}",
                            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), context.Items["route"],
                            ex is OperationCanceledException
                                ? "its target didn't send the rest of its answer within target_route_timeout_seconds"
                                : "its target's answer broke off",
                            LogText.Url(targetRequestMsg.RequestUri?.ToString()), LogText.ExceptionText(ex));
                        context.Abort();
                        return null;
                    }

                    context.Response.BodyWriter.Complete();
                    return null; // Indicates response already written
                }
            }
        }

        /// <summary>
        /// Copies the target's answer to the caller as it arrives, each block sent on as soon as it is
        /// read, after <paramref name="start"/> has set the status and headers: once the first block
        /// is in, or the answer turned out to have none. Each read must complete within the route's
        /// timeout, so a long download goes on while data keeps coming, and a target that stops
        /// sending is cut off. The timer stops while a block is written, so a slow caller never counts
        /// against the target.
        /// </summary>
        private static async Task CopyAnswerAsync(HttpContext context, HttpResponseMessage answer,
            CancellationTokenSource timer, TimeSpan timeout, CancellationToken cancel, Action start)
        {
            await using var source = await answer.Content.ReadAsStreamAsync(timer.Token);
            var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                var first = true;
                while (true)
                {
                    timer.CancelAfter(timeout);
                    var read = await source.ReadAsync(buffer, timer.Token);
                    timer.CancelAfter(Timeout.InfiniteTimeSpan);
                    if (first)
                    {
                        first = false;
                        start();
                    }
                    if (read == 0)
                        return;
                    // Written and flushed: the caller gets the block now.
                    await context.Response.BodyWriter.WriteAsync(buffer.AsMemory(0, read), cancel);
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>
        /// Writes a cached HTTP response to the client.
        /// </summary>
        /// <param name="context">The HTTP context.</param>
        /// <param name="cachedResponse">The cached response container.</param>
        private async Task WriteHttpResponseFromCache(HttpContext context, CachableHttpResponseContainer cachedResponse)
        {
            context.Response.StatusCode = cachedResponse.StatusCode;

            // Copy headers from cache
            foreach (var header in cachedResponse.Headers)
            {
                context.Response.Headers[header.Key] = header.Value;
            }

            // Copy content headers from cache
            foreach (var header in cachedResponse.ContentHeaders)
            {
                context.Response.Headers[header.Key] = header.Value;
            }

            // Write the cached content
            await context.Response.BodyWriter.WriteAsync(cachedResponse.Content, context.RequestAborted);
            context.Response.BodyWriter.Complete();
        }
    }
}
