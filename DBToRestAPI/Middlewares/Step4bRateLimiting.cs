using DBToRestAPI.Services;
using DBToRestAPI.Settings.Extensinos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.RateLimiting;

namespace DBToRestAPI.Middlewares
{
    /// <summary>
    /// Per-endpoint inbound rate limiting.
    ///
    /// Reads the endpoint's <c>&lt;rate_limit&gt;</c> block, falling back tag by tag to the global
    /// <c>&lt;rate_limit&gt;</c> under <c>&lt;settings&gt;</c> (see <see cref="RateLimitPolicyResolver"/>),
    /// and rejects with 429 once a caller has used its allowance for the window. No block anywhere
    /// means no limit.
    ///
    /// Runs after <see cref="Step4JwtAuthorization"/> so the allowance can belong to the authenticated
    /// user, and before <see cref="Step5APIGatewayProcess"/> because Step5 never calls the next
    /// middleware for API-gateway routes, so anything later would not cover them. OPTIONS never
    /// reaches this step (Step2 answers it) and neither does a request Step3 or Step4 rejected.
    ///
    /// Required context.Items from previous middlewares:
    /// - `section`: IConfigurationSection for the route's configuration
    /// Optional:
    /// - `user_claims` (Step4) and `api_key_hash` (Step3) to identify the caller
    ///
    /// Responses:
    /// - 429 Too Many Requests, with `Retry-After` and a `{ success, message, retry_after_seconds }` body
    /// - 500 Internal Server Error: required context items missing from previous middlewares
    /// - Passes to next middleware: no effective limit, allowance available, or the limiter itself failed
    ///   (a limiter fault must never take the API down, so it fails open and logs)
    ///
    /// Log lines name the configuration node (`queries:create_user`), never the request URL, and
    /// the caller only by kind at Information level; the rejection path is attacker-paced, so
    /// nothing request-shaped may reach a log or grow a dictionary.
    /// </summary>
    public class Step4bRateLimiting(
        RequestDelegate next,
        ILogger<Step4bRateLimiting> logger,
        IEncryptedConfiguration settingsEncryptionService)
    {
        private readonly RequestDelegate _next = next;
        private readonly ILogger<Step4bRateLimiting> _logger = logger;
        private readonly RateLimitPolicyResolver _resolver = new(settingsEncryptionService);
        private static readonly string _errorCode = "Step 4b - Rate Limiting Error";

        /// <summary>
        /// One limiter for the whole engine, partitioned on (endpoint, caller, limits). The runtime
        /// caches one sliding-window limiter per key and evicts it once it has been fully replenished
        /// and idle for 10 s. Because the limits are part of the key, a hot-reloaded change simply
        /// starts fresh partitions; nothing is rebuilt or disposed on reload.
        /// </summary>
        private readonly PartitionedRateLimiter<RateLimitPartitionKey> _limiter =
            PartitionedRateLimiter.Create<RateLimitPartitionKey, RateLimitPartitionKey>(key =>
                RateLimitPartition.GetSlidingWindowLimiter(key, k => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = k.MaxRequests,
                    Window = k.Window,
                    SegmentsPerWindow = k.SegmentsPerWindow,
                    QueueLimit = 0,             // reject immediately, never hold a request
                    // Must be false: GetSlidingWindowLimiter discards a `true` by copying the options
                    // (one extra allocation per partition). The partitioned limiter's own 100 ms
                    // heartbeat calls TryReplenish on every partition instead.
                    AutoReplenishment = false,
                }));

        /// <summary>Configuration problems are logged once per distinct message, not once per request. Keys are config-derived, never request-derived.</summary>
        private readonly ConcurrentDictionary<string, byte> _warned = new();

        /// <summary>Rejections are summarised per endpoint at most once a minute; see <see cref="NoteRejection"/>.</summary>
        private readonly ConcurrentDictionary<string, RejectionCounter> _rejections = new();

        private static readonly TimeSpan SummaryInterval = TimeSpan.FromMinutes(1);

        public async Task InvokeAsync(HttpContext context)
        {
            #region log the time and the middleware name
            this._logger.LogDebug("{time}: in Step4bRateLimiting middleware",
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

            var endpointPath = section.Path;

            // Decide first, act after: an exception anywhere in the decision fails OPEN (the request
            // goes through) and is logged once per endpoint and exception type. Exceptions thrown by
            // the rest of the pipeline are deliberately outside this try so they keep their normal handling.
            RateLimitPolicy? policy = null;
            string caller = string.Empty;
            bool rejected = false;

            try
            {
                var warnings = new List<string>();
                policy = _resolver.Resolve(section, warnings);

                if (policy is not null)
                {
                    caller = ResolveCaller(context, section, policy, warnings);
                    var key = new RateLimitPartitionKey(endpointPath, caller, policy.MaxRequests, policy.WindowSeconds);

                    using var lease = _limiter.AttemptAcquire(key);
                    rejected = !lease.IsAcquired;
                }

                foreach (var warning in warnings)
                    WarnOnce(warning);
            }
            catch (Exception ex)
            {
                // Bounded key: endpoint × exception type. The exception itself goes on the log
                // line, never into the key (its message can carry runtime data).
                if (_warned.TryAdd($"failopen|{endpointPath}|{ex.GetType().FullName}", 0))
                    _logger.LogError(ex, "rate limiting failed open on `{Endpoint}`: {ExceptionType}", endpointPath, ex.GetType().Name);
                rejected = false;
            }

            if (!rejected)
            {
                await _next(context);
                return;
            }

            #region reject with 429
            var retryAfterSeconds = policy!.WindowSeconds;
            var message = BuildMessage(policy.Message, retryAfterSeconds);

            NoteRejection(endpointPath, caller);

            context.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            // Retry-After is not a CORS-safelisted response header; without this a browser client cannot read it.
            context.Response.Headers.AccessControlExposeHeaders = "Retry-After";

            await context.Response.DeferredWriteAsJsonAsync(
                new ObjectResult(
                    new
                    {
                        success = false,
                        message,
                        retry_after_seconds = retryAfterSeconds
                    }
                )
                {
                    StatusCode = StatusCodes.Status429TooManyRequests
                }
            );
            #endregion
        }

        private string ResolveCaller(HttpContext context, IConfigurationSection section, RateLimitPolicy policy, List<string> warnings)
        {
            var clientIpSettings = _resolver.ResolveClientIpSettings(warnings);
            if (clientIpSettings.ClientIpHeader is not null)
                WarnOnce($"rate limiting trusts header `{clientIpSettings.ClientIpHeader}` (client_ip_header_trusted_hops={clientIpSettings.TrustedHops}) for the client address; make sure only the proxy that sets it can reach this engine");

            string? degradation;
            string caller;

            switch (policy.Per)
            {
                case RateLimitPer.Endpoint:
                    return "*";

                case RateLimitPer.Ip:
                    caller = "ip:" + RateLimitCallerIdentity.ClientIp(context, clientIpSettings, out degradation);
                    break;

                default:
                    // Same rule as Step3: a value with no collection names after splitting means no key is required.
                    var requiresApiKey = (section.GetValue<string>("api_keys_collections")?
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Length ?? 0) > 0;
                    caller = RateLimitCallerIdentity.Resolve(context, requiresApiKey, clientIpSettings, out degradation);
                    break;
            }

            if (degradation is not null)
                WarnOnce($"`{section.Path}`: {degradation}", LogLevel.Error);

            return caller;
        }

        private static string BuildMessage(string? custom, int retryAfterSeconds)
        {
            var seconds = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);

            if (string.IsNullOrWhiteSpace(custom))
                return $"Too many requests. Please wait {seconds} seconds and try again.";

            return custom.Replace("{{retry_after_seconds}}", seconds, StringComparison.Ordinal);
        }

        private void WarnOnce(string message, LogLevel level = LogLevel.Warning)
        {
            if (_warned.TryAdd(message, 0))
                _logger.Log(level, "{Message}", message);
        }

        /// <summary>
        /// A rejection path is attacker-paced, so it must not write a log line per request (under
        /// IIS stdout logging that becomes a blocking file write). Each rejection is a Debug line;
        /// per endpoint, one Information summary with the count since the last summary goes out at
        /// most once a minute. The count is read and reset atomically, so every rejection lands in
        /// exactly one summary. The tail of a burst is reported with the first rejection after the
        /// interval, whenever that is.
        /// </summary>
        private void NoteRejection(string endpointPath, string caller)
        {
            _logger.LogDebug("rate limit rejected `{Endpoint}` for {Caller}", endpointPath, caller);

            var counter = _rejections.GetOrAdd(endpointPath, _ => new RejectionCounter());
            Interlocked.Increment(ref counter.Count);

            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref counter.LastSummaryTicks);

            if (now - last < SummaryInterval.TotalMilliseconds)
                return;

            if (Interlocked.CompareExchange(ref counter.LastSummaryTicks, now, last) != last)
                return; // another thread is writing the summary

            var count = Interlocked.Exchange(ref counter.Count, 0);
            _logger.LogInformation("rate limit: {Count} request(s) rejected on `{Endpoint}` since the last summary; latest caller kind {CallerKind}",
                count, endpointPath, CallerKind(caller));
        }

        /// <summary>`user`, `key`, `ip` or `*` — enough for an operator, nothing personal.</summary>
        private static string CallerKind(string caller)
        {
            var colon = caller.IndexOf(':');
            return colon < 0 ? caller : caller[..colon];
        }

        private sealed class RejectionCounter
        {
            public int Count;
            public long LastSummaryTicks;
        }
    }
}
