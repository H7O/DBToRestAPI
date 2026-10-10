using DBToRestAPI.Cache;
using DBToRestAPI.Services;
using DBToRestAPI.Settings;
using DBToRestAPI.Settings.Extensinos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DBToRestAPI.Middlewares
{
    public class Step4JwtAuthorization(
                RequestDelegate next,
        ILogger<Step4JwtAuthorization> logger,
        CacheService cacheService,
        IEncryptedConfiguration settingsEncryptionService,
        IHttpClientFactory httpClientFactory,
        OidcProviderIndex providerIndex)
    {
        private readonly RequestDelegate _next = next;
        // private readonly IConfiguration _configuration = configuration;
        private readonly IEncryptedConfiguration _configuration = settingsEncryptionService;
        private readonly ILogger<Step4JwtAuthorization> _logger = logger;
        private readonly CacheService _cacheService = cacheService;
        private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
        private readonly OidcProviderIndex _providerIndex = providerIndex;
        private static readonly string _errorCode = "Step 5 - JWT Authorization";

        // Default HTTP header an app sends to hint which configured provider issued the token,
        // consulted only for multi-provider endpoints. Overridable per-endpoint via
        // <authorize><provider_hint_header> or globally via authorize:provider_hint_header.
        private const string DefaultProviderHintHeader = "X-Auth-Provider";

        // The named HttpClient that fetches each provider's discovery document and signing keys.
        internal const string OidcMetadataClient = "oidcMetadata";

        public async Task InvokeAsync(HttpContext context)
        {

            #region log the time and the middleware name
            this._logger.LogDebug("{time}: in Step5JwtAuthorization middleware",
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

            var route = context.Items.ContainsKey("route")
                ? context.Items["route"] as string
                : null;

            #region Check if authorization is required
            var routeAuthorizeSection = section.GetSection("authorize");

            // If no authorization configured, pass through
            if (!routeAuthorizeSection.Exists())
            {
                await _next(context);
                return;
            }

            // Check if authorization is explicitly disabled
            var enabled = routeAuthorizeSection.GetValue<bool?>("enabled") ?? true;
            if (!enabled)
            {
                _logger.LogDebug($"Authorization explicitly disabled for route `{route}`");
                await _next(context);
                return;
            }
            #endregion


            #region Extract Bearer token
            if (!context.Request.Headers.TryGetValue("Authorization", out var authHeader)
                || string.IsNullOrWhiteSpace(authHeader.ToString()))
            {
                _logger.LogDebug("Missing Authorization header for route `{route}`", route);
                await context.Response.DeferredWriteAsJsonAsync(
                    new ObjectResult(
                        new
                        {
                            success = false,
                            message = "Authorization header is required"
                        }
                    )
                    {
                        StatusCode = 401
                    }
                );
                return;
            }

            var authHeaderValue = authHeader.ToString();
            if (!authHeaderValue.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Authorization header must use Bearer scheme");
                await context.Response.DeferredWriteAsJsonAsync(
                    new ObjectResult(
                        new
                        {
                            success = false,
                            message = "Invalid authorization header format. Use: Bearer <token>"
                        }
                    )
                    {
                        StatusCode = 401
                    }
                );
                return;
            }

            var accessToken = authHeaderValue.Substring("Bearer ".Length).Trim();
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                _logger.LogDebug("Empty Bearer token in route `{route}`", route);
                await context.Response.DeferredWriteAsJsonAsync(
                    new ObjectResult(
                        new
                        {
                            success = false,
                            message = "Bearer token is required"
                        }
                    )
                    {
                        StatusCode = 401
                    }
                );
                return;
            }
            #endregion


            #region Resolve OIDC provider (single, or multi via hint -> issuer)
            // <provider> may be a single name (today's behavior), a comma-separated allow-list,
            // or "*" meaning any provider configured in auth_providers.xml. For multi-provider
            // endpoints we must select exactly one provider to validate against — see
            // MULTI_PROVIDER_OIDC.md for the design and security rationale.
            var allowedProviders = _providerIndex.ExpandAllowedProviders(
                routeAuthorizeSection.GetValue<string>("provider"));

            // With more than one allowed provider, route-level overrides of authority/audience/
            // issuer/etc. are ambiguous and intentionally ignored; each provider uses its own block.
            var isMultiProvider = allowedProviders.Count > 1;

            string? providerName;
            if (!isMultiProvider)
            {
                // 0 allowed -> inline/no-provider mode (providerName stays null, route-level config used)
                // 1 allowed -> today's behavior, unchanged
                providerName = allowedProviders.Count == 1 ? allowedProviders[0] : null;
            }
            else
            {
                providerName = await ResolveProviderForTokenAsync(
                    context, routeAuthorizeSection, allowedProviders, accessToken, route);

                if (providerName == null)
                {
                    // No allowed provider could be matched to this token (no/unaccepted hint and the
                    // token issuer matched none of the allowed providers). Treat as an invalid token.
                    await context.Response.DeferredWriteAsJsonAsync(
                        new ObjectResult(
                            new
                            {
                                success = false,
                                message = "Invalid token"
                            }
                        )
                        {
                            StatusCode = 401
                        }
                    );
                    return;
                }
            }

            IConfigurationSection? providerSection = null;
            if (!string.IsNullOrWhiteSpace(providerName))
            {
                // Look for provider in oidc_providers configuration
                providerSection = _configuration.GetSection($"authorize:providers:{providerName}");

                if (!providerSection.Exists())
                {
                    _logger.LogError("Provider '{providerName}' not found in configuration for route `{route}`", providerName, route);
                    await context.Response.DeferredWriteAsJsonAsync(
                        new ObjectResult(
                            new
                            {
                                success = false,
                                message = $"Authorization provider configuration error. (Contact your service provider support and provide them with error code `{_errorCode}`)"
                            }
                        )
                        {
                            StatusCode = 500
                        }
                    );
                    return;
                }
            }

            // Route-level overrides apply only in single-provider mode (see comment above).
            IConfigurationSection? routeOverrides = isMultiProvider ? null : routeAuthorizeSection;
            #endregion


            #region Get JWT configuration (route > provider > global)
            // NOTE: `routeOverrides` is the route-level <authorize> section in single-provider mode,
            // and null in multi-provider mode (so each selected provider uses its own config block).
            var authority = routeOverrides?.GetValue<string>("authority")
                            ?? providerSection?.GetValue<string>("authority");
            // global failover removed as it might be confusing for users to understand how to set it
            // ?? _configuration.GetValue<string>("authorize:authority");

            var audience = routeOverrides?.GetValue<string>("audience")
                           ?? providerSection?.GetValue<string>("audience");
            // global failover removed as it might be confusing for users to understand how to set it
            //?? _configuration.GetValue<string>("authorize:audience");

            var issuer = routeOverrides?.GetValue<string>("issuer")
                         ?? providerSection?.GetValue<string>("issuer")
                         // global failover removed as it might be confusing for users to understand how to set it
                         // ?? _configuration.GetValue<string>("authorize:issuer")
                         ?? authority;

            var validateIssuer = routeOverrides?.GetValue<bool?>("validate_issuer")
                                 ?? providerSection?.GetValue<bool?>("validate_issuer")
                                 // global failover removed as it might be confusing for users to understand how to set it
                                 // ?? _configuration.GetValue<bool?>("authorize:validate_issuer")
                                 ?? true;

            var validateAudience = routeOverrides?.GetValue<bool?>("validate_audience")
                                   ?? providerSection?.GetValue<bool?>("validate_audience")
                                   // global failover removed as it might be confusing for users to understand how to set it
                                   // ?? _configuration.GetValue<bool?>("authorize:validate_audience")
                                   ?? true;

            var validateLifetime = routeOverrides?.GetValue<bool?>("validate_lifetime")
                                   ?? providerSection?.GetValue<bool?>("validate_lifetime")
                                   // global failover removed as it might be confusing for users to understand how to set it
                                   // ?? _configuration.GetValue<bool?>("authorize:validate_lifetime")
                                   ?? true;

            var clockSkewSeconds = routeOverrides?.GetValue<int?>("clock_skew_seconds")
                                   ?? providerSection?.GetValue<int?>("clock_skew_seconds")
                                   // global failover removed as it might be confusing for users to understand how to set it
                                   // ?? _configuration.GetValue<int?>("authorize:clock_skew_seconds")
                                   ?? 300;

            // Get UserInfo fallback configuration
            var userInfoFallbackClaims = routeOverrides?.GetValue<string>("userinfo_fallback_claims")
                                         ?? providerSection?.GetValue<string>("userinfo_fallback_claims")
                                         // global failover removed as it might be confusing for users to understand how to set it
                                         // ?? _configuration.GetValue<string>("authorize:userinfo_fallback_claims")
                                         ?? "email,name,given_name,family_name";

            var userInfoCacheDuration = routeOverrides?.GetValue<int?>("userinfo_cache_duration_seconds")
                                        ?? providerSection?.GetValue<int?>("userinfo_cache_duration_seconds");
            // global failover removed as it might be confusing for users to understand how to set it
            // ?? _configuration.GetValue<int?>("authorize:userinfo_cache_duration_seconds");
            // Note: If null, cache will default to token expiration time

            var userInfoTimeoutSeconds = routeOverrides?.GetValue<int?>("userinfo_timeout_seconds")
                                         ?? providerSection?.GetValue<int?>("userinfo_timeout_seconds")
                                         ?? _configuration.GetValue<int?>("authorize:userinfo_timeout_seconds")
                                         ?? _configuration.GetValue<int?>("userinfo_timeout_seconds")
                                         ?? 30;
            if (userInfoTimeoutSeconds < 1)
                userInfoTimeoutSeconds = 30;

            if (string.IsNullOrWhiteSpace(authority))
            {
                _logger.LogError("JWT authority not configured for route `{route}", route);
                await context.Response.DeferredWriteAsJsonAsync(
                    new ObjectResult(
                        new
                        {
                            success = false,
                            message = $"Authorization configuration error. (Contact your service provider support and provide them with error code `{_errorCode}`)"
                        }
                    )
                    {
                        StatusCode = 500
                    }
                );
                return;
            }
            #endregion


            #region Validate JWT access token
            ClaimsPrincipal principal;
            SecurityToken validatedToken;
            OpenIdConnectConfiguration? discoveryDocument;
            try
            {
                var tokenHandler = new JwtSecurityTokenHandler();

                // A bearer that isn't a readable JWT is a client error, not a server error — and we can
                // tell without contacting the provider. Reject it as 401 up front so the provider-hint
                // path matches the issuer-fallback path (which screens unreadable tokens the same way)
                // and the documented contract, and so we skip the discovery fetch for junk input.
                // Otherwise ValidateToken throws ArgumentException (IDX12741) — not a
                // SecurityTokenException — which would fall through to the generic 500.
                //
                // TryReadJwt, not CanReadToken alone: CanReadToken only checks the SHAPE (three
                // base64url segments). "abc.def.ghi" has that shape, so it passed this guard and then
                // blew up inside ValidateToken with a 500 — the very bug this guard exists to stop.
                // Tokens with 1, 2 or 5 segments were rejected correctly; only correctly-shaped
                // garbage slipped through. TryReadJwt actually parses the header and payload, so
                // content that merely looks like a JWT is caught here too.
                if (TryReadJwt(accessToken) == null)
                {
                    _logger.LogDebug("Bearer is not a readable JWT; rejecting as invalid token");
                    await context.Response.DeferredWriteAsJsonAsync(
                        new ObjectResult(
                            new
                            {
                                success = false,
                                message = "Invalid token"
                            }
                        )
                        {
                            StatusCode = 401
                        }
                    );
                    return;
                }

                // Get or fetch discovery document (with caching)
                discoveryDocument = await GetDiscoveryDocumentAsync(authority, context.RequestAborted);

                // DEBUG: Log discovery document details
                _logger.LogDebug("Discovery document loaded from: {authority}", LogText.Url(authority));
                _logger.LogDebug("Issuer from discovery: {issuer}", discoveryDocument.Issuer);
                _logger.LogDebug("JWKS URI: {jwksUri}", LogText.Url(discoveryDocument.JwksUri));
                _logger.LogDebug("Number of signing keys: {count}", discoveryDocument.SigningKeys?.Count ?? 0);

                if (discoveryDocument.SigningKeys == null || !discoveryDocument.SigningKeys.Any())
                {
                    _logger.LogError("No signing keys found in discovery document. JWKS URI: {jwksUri}", LogText.Url(discoveryDocument.JwksUri));
                    throw new InvalidOperationException("No signing keys available from OIDC provider");
                }

                var validationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKeys = discoveryDocument.SigningKeys,
                    ValidateIssuer = validateIssuer,
                    ValidIssuer = issuer,
                    ValidateAudience = validateAudience,
                    ValidAudience = audience,
                    ValidateLifetime = validateLifetime,
                    ClockSkew = TimeSpan.FromSeconds(clockSkewSeconds)
                };

                try
                {
                    principal = tokenHandler.ValidateToken(accessToken, validationParameters, out validatedToken);
                }
                catch (ArgumentException ex)
                {
                    // Belt and braces behind the TryReadJwt guard above. ValidateToken reports some
                    // malformed-token conditions as ArgumentException rather than a
                    // SecurityTokenException, and ArgumentException is not caught below, so it would
                    // reach the generic handler and be reported as a 500 — telling the caller the
                    // server broke when in fact they sent a bad token.
                    //
                    // Scoped to this one call on purpose: an ArgumentException from anywhere else in
                    // the enclosing try (a malformed authority URL in OUR config, say) really is a
                    // server error and must keep returning 500.
                    _logger.LogDebug(ex, "Access token rejected as malformed by the validator");
                    throw new SecurityTokenMalformedException("The token is malformed.", ex);
                }

                _logger.LogDebug("Access token validation successful");
            }
            catch (SecurityTokenExpiredException)
            {
                _logger.LogDebug("Access token expired");
                await context.Response.DeferredWriteAsJsonAsync(
                    new ObjectResult(
                        new
                        {
                            success = false,
                            message = "Token has expired"
                        }
                    )
                    {
                        StatusCode = 401
                    }
                );
                return;
            }
            catch (SecurityTokenInvalidSignatureException)
            {
                _logger.LogWarning("Access token has invalid signature");
                await context.Response.DeferredWriteAsJsonAsync(
                    new ObjectResult(
                        new
                        {
                            success = false,
                            message = "Invalid token signature"
                        }
                    )
                    {
                        StatusCode = 401
                    }
                );
                return;
            }
            catch (SecurityTokenException ex)
            {
                _logger.LogWarning(ex, "Access token validation failed");
                await context.Response.DeferredWriteAsJsonAsync(
                    new ObjectResult(
                        new
                        {
                            success = false,
                            message = "Invalid token"
                        }
                    )
                    {
                        StatusCode = 401
                    }
                );
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during token validation");
                await context.Response.DeferredWriteAsJsonAsync(
                    new ObjectResult(
                        new
                        {
                            success = false,
                            message = $"Authorization error. (Contact your service provider support and provide them with error code `{_errorCode}`)"
                        }
                    )
                    {
                        StatusCode = 500
                    }
                );
                return;
            }
            #endregion


            #region Claims: read the token, fill gaps from UserInfo, build {auth{...}}
            var (claimsDict, token) = await ResolveClaimsAsync(
                principal,
                userInfoFallbackClaims,
                providerName,
                async missingClaims =>
                {
                    _logger.LogDebug("Missing claims in access token: {claims}. Calling UserInfo endpoint...",
                        string.Join(", ", missingClaims));

                    var userInfoClaims = await GetUserInfoAsync(
                        accessToken,
                        discoveryDocument,
                        userInfoCacheDuration,
                        userInfoTimeoutSeconds,
                        validatedToken.ValidTo,  // Pass token expiration
                        context.RequestAborted);

                    if (userInfoClaims != null && userInfoClaims.Any())
                        _logger.LogDebug("UserInfo claims added successfully");
                    else
                        _logger.LogWarning("Failed to retrieve UserInfo claims");

                    return userInfoClaims;
                });

            context.User = principal;
            context.Items["user_claims"] = claimsDict;

            _logger.LogDebug("User context set successfully. UserId: {userId}, Email: {email}",
                claimsDict.TryGetValue("user_id", out var loggedUserId) ? loggedUserId : "unknown",
                claimsDict.TryGetValue("email", out var loggedEmail) ? loggedEmail : "unknown");
            #endregion


            #region Check required scopes
            var requiredScopes = routeAuthorizeSection.GetValue<string>("required_scopes")
                                 ?? providerSection?.GetValue<string>("required_scopes");

            if (!string.IsNullOrWhiteSpace(requiredScopes))
            {
                var scopes = token.Scopes;

                var required = requiredScopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                if (!required.All(r => scopes.Contains(r)))
                {
                    _logger.LogWarning("Missing required scopes. Required: {required}, Found: {scopes}",
                        string.Join(", ", required), string.Join(", ", scopes));

                    await context.Response.DeferredWriteAsJsonAsync(
                        new ObjectResult(
                            new
                            {
                                success = false,
                                message = "Insufficient permissions"
                            }
                        )
                        {
                            StatusCode = 403
                        }
                    );
                    return;
                }
            }
            #endregion

            #region Check required roles
            var requiredRoles = routeAuthorizeSection.GetValue<string>("required_roles")
                                ?? providerSection?.GetValue<string>("required_roles");

            if (!string.IsNullOrWhiteSpace(requiredRoles))
            {
                var required = requiredRoles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                if (!required.All(r => token.Roles.Contains(r, StringComparer.OrdinalIgnoreCase)))
                {
                    _logger.LogWarning("Missing required roles. Required: {required}, Found: {roles}",
                        string.Join(", ", required), string.Join(", ", token.Roles));

                    await context.Response.DeferredWriteAsJsonAsync(
                        new ObjectResult(
                            new
                            {
                                success = false,
                                message = "Insufficient permissions"
                            }
                        )
                        {
                            StatusCode = 403
                        }
                    );
                    return;
                }
            }
            #endregion

            // Validation successful, proceed to next middleware
            await _next(context);


        }

        #region Claim names

        // JwtSecurityTokenHandler renames well-known claims on the way in: sub becomes
        // ClaimTypes.NameIdentifier, scp becomes http://schemas.microsoft.com/identity/claims/scope,
        // oid becomes .../identity/claims/objectidentifier, given_name becomes ClaimTypes.GivenName,
        // and so on. It records the name the token used in the claim's ShortClaimTypeProperty.
        // Code that looks a claim up by the token's name alone never finds a renamed one, which
        // is how {auth{sub}} was usually missing and why required_scopes rejected every Entra
        // and Okta token (their scopes arrive in scp). These helpers match either name.

        /// <summary>
        /// The name the token itself used for this claim, before .NET renamed it.
        /// </summary>
        internal static string TokenClaimName(Claim claim)
            => claim.Properties.TryGetValue(JwtSecurityTokenHandler.ShortClaimTypeProperty, out var shortName)
               && !string.IsNullOrEmpty(shortName)
                ? shortName
                : claim.Type;

        /// <summary>
        /// The claims whose .NET type or original token name is <paramref name="name"/>.
        /// </summary>
        internal static IEnumerable<Claim> FindClaims(ClaimsPrincipal principal, string name)
            => principal.Claims.Where(c => c.Type == name || TokenClaimName(c) == name);

        /// <summary>
        /// The first non-empty value among the claims named in <paramref name="names"/>, tried in order.
        /// </summary>
        internal static string? FirstClaimValue(ClaimsPrincipal principal, params string[] names)
        {
            foreach (var name in names)
            {
                var value = FindClaims(principal, name)
                    .Select(c => c.Value)
                    .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
                if (value != null)
                    return value;
            }
            return null;
        }

        /// <summary>
        /// The token's roles, from "roles" and "role" (both renamed to ClaimTypes.Role).
        /// </summary>
        internal static List<string> GetRoles(ClaimsPrincipal principal)
            => principal.FindAll(ClaimTypes.Role)
                .Concat(FindClaims(principal, "roles"))
                .Select(c => c.Value)
                .Distinct()
                .ToList();

        /// <summary>
        /// The token's scopes, from "scp" and "scope". Each claim may hold several, space-separated.
        /// </summary>
        internal static HashSet<string> GetScopes(ClaimsPrincipal principal)
            => FindClaims(principal, "scp")
                .Concat(FindClaims(principal, "scope"))
                .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .ToHashSet();

        /// <summary>
        /// Text for a claim value from the UserInfo response. That response is deserialised into
        /// <c>Dictionary&lt;string, object&gt;</c>, so every value is a JsonElement, which no SQL
        /// driver can bind: a query using such a claim failed with the generic 400.
        /// </summary>
        internal static string? ClaimValueToString(object? value) => value switch
        {
            null => null,
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
            JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
            JsonElement e => e.GetRawText(),
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
        };

        /// <summary>
        /// Adds the UserInfo response's claims that the token doesn't already carry, under either
        /// name. The token's own value always wins: it is signed, and for some providers (Entra
        /// access tokens, for one) its sub differs from the UserInfo sub.
        /// </summary>
        internal static void AddUserInfoClaims(ClaimsPrincipal principal, IDictionary<string, object> userInfoClaims)
        {
            if (principal.Identity is not ClaimsIdentity identity)
                return;

            foreach (var (name, rawValue) in userInfoClaims)
            {
                var value = ClaimValueToString(rawValue);
                if (string.IsNullOrWhiteSpace(value) || FindClaims(principal, name).Any())
                    continue;
                identity.AddClaim(new Claim(name, value));
            }
        }

        /// <summary>
        /// What the signed token itself says, read before any UserInfo claim is added.
        /// </summary>
        /// <remarks>
        /// required_scopes and required_roles are checked against these, so a UserInfo response
        /// can never grant them, and user_id, email and name prefer the token's value. The old
        /// code checked scopes after the merge, so a UserInfo `scope` counted.
        /// </remarks>
        internal sealed record TokenIdentity(
            string? UserId,
            string? Email,
            string? Name,
            IReadOnlyList<string> Roles,
            IReadOnlySet<string> Scopes);

        internal static TokenIdentity ReadTokenIdentity(ClaimsPrincipal principal) => new(
            // NameIdentifier is where .NET puts sub. oid (Entra's object id) is the fallback for
            // a token without a subject; .NET renames it too, so it is matched by either name.
            UserId: FirstClaimValue(principal, ClaimTypes.NameIdentifier, "sub", "oid"),
            // "emails" is Azure AD B2C's claim, a list; the first value is used.
            Email: FirstClaimValue(principal, ClaimTypes.Email, "email", "emails"),
            Name: FirstClaimValue(principal, ClaimTypes.Name, "name"),
            Roles: GetRoles(principal),
            Scopes: GetScopes(principal));

        /// <summary>
        /// The claims listed in userinfo_fallback_claims that the token doesn't carry. UserInfo is
        /// called only when this isn't empty.
        /// </summary>
        /// <remarks>
        /// given_name and family_name used to be looked up only by the token's names. .NET renames
        /// both, so they always looked missing and UserInfo was called for every new token.
        /// </remarks>
        internal static List<string> MissingFallbackClaims(ClaimsPrincipal principal, string? fallbackClaimsCsv)
        {
            var listed = (fallbackClaimsCsv ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var missing = new List<string>();
            foreach (var name in listed)
            {
                string? value = name switch
                {
                    "email" => FirstClaimValue(principal, ClaimTypes.Email, "email", "emails"),
                    "name" => FirstClaimValue(principal, ClaimTypes.Name, "name"),
                    "given_name" => FirstClaimValue(principal, ClaimTypes.GivenName, "given_name"),
                    "family_name" => FirstClaimValue(principal, ClaimTypes.Surname, "family_name"),
                    // Other names (picture, first_name...) never triggered the call, and still don't.
                    _ => "not checked",
                };
                if (string.IsNullOrWhiteSpace(value))
                    missing.Add(name);
            }
            return missing;
        }

        /// <summary>
        /// Reads the token, calls UserInfo when a listed claim is missing, adds what UserInfo
        /// knows that the token doesn't, and builds the claims a query reads through
        /// <c>{auth{...}}</c>. Returns those claims and the token's own identity, which the role
        /// and scope checks use.
        /// </summary>
        /// <param name="fetchUserInfo">Called with the missing claim names, at most once.</param>
        internal static async Task<(Dictionary<string, object> Claims, TokenIdentity Token)> ResolveClaimsAsync(
            ClaimsPrincipal principal,
            string? userInfoFallbackClaims,
            string? providerName,
            Func<IReadOnlyList<string>, Task<Dictionary<string, object>?>> fetchUserInfo)
        {
            var token = ReadTokenIdentity(principal);

            var missing = MissingFallbackClaims(principal, userInfoFallbackClaims);
            if (missing.Count > 0)
            {
                var userInfoClaims = await fetchUserInfo(missing);
                if (userInfoClaims is { Count: > 0 })
                    AddUserInfoClaims(principal, userInfoClaims);
            }

            return (BuildClaimsDictionary(principal, token, providerName), token);
        }

        /// <summary>
        /// The claims a query reads through <c>{auth{...}}</c>.
        /// </summary>
        /// <remarks>
        /// Each claim is exposed under its .NET type and under the name the token used, so
        /// <c>{auth{sub}}</c>, <c>{auth{given_name}}</c>, <c>{auth{family_name}}</c>, <c>{auth{oid}}</c>,
        /// <c>{auth{tid}}</c> and <c>{auth{scp}}</c> all work. Where several claims share a name,
        /// the first one's value is used, except scp and scope, which hold every scope,
        /// space-separated. On top of those, the engine adds unified names that work across
        /// providers: user_id, email, name, roles (joined with |), auth_time and auth_provider.
        /// </remarks>
        internal static Dictionary<string, object> BuildClaimsDictionary(
            ClaimsPrincipal principal,
            TokenIdentity token,
            string? providerName)
        {
            var claimsDict = new Dictionary<string, object>();

            if (!string.IsNullOrWhiteSpace(token.UserId))
                claimsDict["user_id"] = token.UserId;

            // The token's value first; a UserInfo value only when the token has none. Looked up
            // after the merge alone, a UserInfo "email" would beat a token's "emails".
            var userEmail = token.Email ?? FirstClaimValue(principal, ClaimTypes.Email, "email", "emails");
            if (!string.IsNullOrWhiteSpace(userEmail))
                claimsDict["email"] = userEmail;

            var userName = token.Name ?? FirstClaimValue(principal, ClaimTypes.Name, "name");
            if (!string.IsNullOrWhiteSpace(userName))
                claimsDict["name"] = userName;

            // .NET rewrites inbound "roles" and "role" to
            // http://schemas.microsoft.com/ws/2008/06/identity/claims/role, so this unified,
            // |-joined value is what gives SQL authors every role through {auth{roles}}.
            if (token.Roles.Count > 0)
                claimsDict["roles"] = string.Join("|", token.Roles);

            // Unified login instant, same intent as "email" and "roles" above: one name a SQL
            // author can rely on across providers. Both source claims keep their own names
            // (neither is rewritten), but their AVAILABILITY differs - iat is required in an
            // OIDC ID token, auth_time only when max_age was requested. Prefer auth_time: it
            // marks when the human authenticated and survives a silent token refresh, whereas
            // iat changes on every refresh. Lets a SQL author compare the login instant against
            // a server-side "sessions invalidated at" timestamp, so a token issued before a
            // logout is rejected even though it has not yet expired.
            var authInstant = principal.FindFirst("auth_time")?.Value
                              ?? principal.FindFirst("iat")?.Value;
            if (!string.IsNullOrWhiteSpace(authInstant))
                claimsDict["auth_time"] = authInstant;

            // Scopes as one space-separated list, the form Entra ID sends in a single claim. Okta
            // sends scp as a JSON array, which becomes one claim per scope, so the first-value
            // rule below would keep only the first scope.
            foreach (var scopeName in new[] { "scp", "scope" })
            {
                var scopeClaims = FindClaims(principal, scopeName).ToList();
                if (scopeClaims.Count == 0)
                    continue;
                var joined = string.Join(" ", scopeClaims.Select(c => c.Value));
                claimsDict[scopeName] = joined;
                foreach (var type in scopeClaims.Select(c => c.Type).Distinct())
                    claimsDict[type] = joined;
            }

            // Store all OIDC claims for SQL access, under both names. The token's claims come
            // before any added from UserInfo, so the signed value wins a shared name.
            foreach (var claim in principal.Claims)
            {
                if (!claimsDict.ContainsKey(claim.Type))
                    claimsDict[claim.Type] = claim.Value;

                var tokenName = TokenClaimName(claim);
                if (!claimsDict.ContainsKey(tokenName))
                    claimsDict[tokenName] = claim.Value;
            }

            // Expose the resolved provider (the auth_providers.xml key, e.g. "google", "azure_b2c")
            // to SQL via {auth{auth_provider}}. Set AFTER copying token claims so the engine-resolved
            // value is authoritative even if a token happens to carry an "auth_provider" claim.
            if (!string.IsNullOrWhiteSpace(providerName))
                claimsDict["auth_provider"] = providerName;

            return claimsDict;
        }

        #endregion

        /// <summary>
        /// Gets the OIDC discovery document with caching.
        /// Uses CachedOpenIdConnectConfiguration to properly serialize/deserialize signing keys through HybridCache.
        /// </summary>
        private async Task<OpenIdConnectConfiguration> GetDiscoveryDocumentAsync(
            string authority,
            CancellationToken cancellationToken)
        {
            var normalizedAuthority = authority.TrimEnd('/');
            var cacheKey = $"oidc_discovery:{normalizedAuthority}";

            // Cache discovery documents for 24 hours (common practice for OIDC metadata)
            var cacheDuration = TimeSpan.FromHours(24);

            var cachedConfig = await _cacheService.GetAsync(
                cacheKey,
                cacheDuration,
                async (ct) =>
                {
                    // A pooled client from the factory, which also lets tests answer for the provider
                    // in process. The retriever refuses any address that isn't https.
                    var retriever = new HttpDocumentRetriever(_httpClientFactory.CreateClient(OidcMetadataClient));
                    async Task<string> FetchAsync(string address)
                    {
                        try
                        {
                            return await retriever.GetDocumentAsync(address, ct);
                        }
                        catch (Exception ex) when (ex is IOException or ArgumentException)
                        {
                            // The retriever's message holds the whole address, query string included, and
                            // this exception is logged. So it is replaced by one that says why without it.
                            // The inner exception, kept, names the host at most.
                            var cause = ex switch
                            {
                                ArgumentNullException => "no address",
                                ArgumentException when !address.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                                    => "the address must be https",
                                _ when ex.Data["status_code"] is System.Net.HttpStatusCode status
                                    => $"the provider answered {(int)status} {status}",
                                _ => ex.GetType().Name,
                            };
                            throw new IOException($"Unable to retrieve {LogText.Url(address)}: {cause}", ex.InnerException);
                        }
                    }

                    var config = OpenIdConnectConfiguration.Create(await FetchAsync(
                        $"{normalizedAuthority}/.well-known/openid-configuration"));

                    // The keys are cached as their JSON, because OpenIdConnectConfiguration.SigningKeys
                    // doesn't survive HybridCache's serialization. Fetched once: a ConfigurationManager
                    // fetched them too, only for its copy to be dropped.
                    var jwksJson = await FetchAsync(config.JwksUri!);
                    _logger.LogDebug("Fetched JWKS from {uri}", LogText.Url(config.JwksUri));

                    // Parsed here, so that a document that isn't a key set (a maintenance page sent with a
                    // 200), or one without a signing key, throws and is never cached: the next request
                    // fetches again instead of failing for the cache's 24 hours.
                    if (new JsonWebKeySet(jwksJson).GetSigningKeys().Count == 0)
                        throw new InvalidOperationException($"No signing keys in the JWKS at {LogText.Url(config.JwksUri)}");

                    // Create cacheable wrapper that stores JWKS as JSON string
                    return CachedOpenIdConnectConfiguration.FromDiscoveryDocument(config, jwksJson);
                },
                cancellationToken);

            // Convert cached wrapper back to OpenIdConnectConfiguration with signing keys properly populated
            return cachedConfig.ToDiscoveryDocument();
        }

        /// <summary>
        /// Calls the UserInfo endpoint with the access token to retrieve additional user claims.
        /// Results are cached using SHA-256 hash of the access token.
        /// 
        /// Smart Caching Strategy:
        /// - If userInfoCacheDuration is configured, it acts as the MAXIMUM cache duration
        /// - Cache NEVER outlives the access token's expiration
        /// - If userInfoCacheDuration is null/0, defaults to token's expiration time
        /// - Example: Token expires in 3600s, max cache is 300s → cache for 300s
        /// - Example: Token expires in 120s, max cache is 300s → cache for 120s (token expiry wins)
        /// - Example: Token expires in 3600s, no max configured → cache for 3600s
        /// </summary>
        private async Task<Dictionary<string, object>?> GetUserInfoAsync(
            string accessToken,
            Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration discoveryDocument,
            int? userInfoCacheDuration,
            int userInfoTimeoutSeconds,
            DateTime tokenExpiration,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(discoveryDocument.UserInfoEndpoint))
            {
                _logger.LogWarning("UserInfo endpoint not defined in discovery document");
                return null;
            }
            // Compute SHA-256 hash of the access token for cache key
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var tokenHashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(accessToken));
            var tokenHashString = Convert.ToBase64String(tokenHashBytes);
            var cacheKey = $"userinfo_claims:{tokenHashString}";
            // Determine cache duration
            TimeSpan effectiveCacheDuration;
            if (userInfoCacheDuration.HasValue && userInfoCacheDuration.Value > 0)
            {
                var maxCache = TimeSpan.FromSeconds(userInfoCacheDuration.Value);
                var timeToTokenExpiry = tokenExpiration - DateTime.UtcNow;
                effectiveCacheDuration = timeToTokenExpiry < maxCache ? timeToTokenExpiry : maxCache;
            }
            else
            {
                effectiveCacheDuration = tokenExpiration - DateTime.UtcNow;
            }
            if (effectiveCacheDuration <= TimeSpan.Zero)
            {
                _logger.LogDebug("Token already expired, skipping UserInfo call");
                return null;
            }
            return await _cacheService.GetAsync(
                cacheKey,
                effectiveCacheDuration,
                async (ct) =>
                {
                    var httpClient = _httpClientFactory.CreateClient("checkCertificateErrors");
                    var request = new HttpRequestMessage(HttpMethod.Get, discoveryDocument.UserInfoEndpoint);
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, cancellationToken);
                    linkedCts.CancelAfter(TimeSpan.FromSeconds(userInfoTimeoutSeconds));
                    var response = await httpClient.SendAsync(request, linkedCts.Token);
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("UserInfo endpoint returned non-success status: {status}", response.StatusCode);
                        return null;
                    }
                    var content = await response.Content.ReadAsStringAsync(linkedCts.Token);
                    var claims = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(content);
                    return claims;
                },
                cancellationToken);
        }


        /// <summary>
        /// Resolves a token to exactly one of the endpoint's allowed providers, for endpoints that
        /// permit MORE THAN ONE provider. Selection order (see MULTI_PROVIDER_OIDC.md):
        ///   1. The provider-hint header (default "X-Auth-Provider", overridable) when present and it
        ///      names an allowed provider. A present-but-not-allowed hint fails fast (returns null).
        ///   2. Otherwise the token's UNVALIDATED issuer (disambiguated by audience when several
        ///      allowed providers share an issuer).
        /// Returns null when no allowed provider can be determined (the caller then responds 401).
        ///
        /// The hint/issuer only SELECT a provider; trust still comes entirely from the subsequent
        /// cryptographic validation, so a wrong hint or a forged issuer can only cause a rejection.
        /// </summary>
        private Task<string?> ResolveProviderForTokenAsync(
            HttpContext context,
            IConfigurationSection routeAuthorizeSection,
            IReadOnlyList<string> allowedProviders,
            string accessToken,
            string? route)
        {
            // 1) Client hint header (endpoint override > global setting > built-in default)
            var hintHeaderName = routeAuthorizeSection.GetValue<string>("provider_hint_header")
                                 ?? _configuration.GetValue<string>("authorize:provider_hint_header")
                                 ?? DefaultProviderHintHeader;

            if (context.Request.Headers.TryGetValue(hintHeaderName, out var hintValues))
            {
                var hint = hintValues.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(hint))
                {
                    var hinted = allowedProviders.FirstOrDefault(
                        p => string.Equals(p, hint, StringComparison.OrdinalIgnoreCase));
                    if (hinted != null)
                        return Task.FromResult<string?>(hinted);

                    // Hint present but not allowed on this endpoint -> fail fast (clear client/config error).
                    // To prefer leniency, remove this block and let resolution fall through to the issuer.
                    _logger.LogWarning(
                        "Provider hint '{hint}' (header '{header}') is not allowed for route `{route}`",
                        LogText.Escape(hint), hintHeaderName, route);
                    return Task.FromResult<string?>(null);
                }
            }

            // 2) Issuer (iss) fallback — read UNVALIDATED, used only to select a provider.
            var jwt = TryReadJwt(accessToken);
            if (jwt == null || string.IsNullOrWhiteSpace(jwt.Issuer))
            {
                _logger.LogDebug(
                    "No provider hint and token issuer is unreadable for route `{route}`; cannot select a provider", route);
                return Task.FromResult<string?>(null);
            }

            var resolved = _providerIndex.ResolveProviderByIssuer(jwt.Issuer, jwt.Audiences, allowedProviders);
            if (resolved == null)
                _logger.LogDebug(
                    "Token issuer '{iss}' did not resolve to a single allowed provider for route `{route}` " +
                    "(send the '{header}' hint header to disambiguate)", LogText.Escape(jwt.Issuer), route, hintHeaderName);

            return Task.FromResult<string?>(resolved);
        }

        /// <summary>
        /// Reads a JWT WITHOUT validating it, to extract routing hints (issuer/audience) before the
        /// full validation runs. Returns null for non-JWT (e.g. opaque) tokens.
        /// </summary>
        private static JwtSecurityToken? TryReadJwt(string accessToken)
        {
            try
            {
                var handler = new JwtSecurityTokenHandler();
                return handler.CanReadToken(accessToken) ? handler.ReadJwtToken(accessToken) : null;
            }
            catch
            {
                return null;
            }
        }



    }
}