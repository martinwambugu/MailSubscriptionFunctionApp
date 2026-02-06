using Azure.Identity;
using MailSubscriptionFunctionApp.Interfaces;
using MailSubscriptionFunctionApp.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace MailSubscriptionFunctionApp.Services
{
    /// <summary>  
    /// Handles Microsoft Graph API interactions for managing mail subscriptions.  
    /// Implements secure authentication, caching, and comprehensive error handling.  
    /// </summary>  
    /// <remarks>  
    /// <para>  
    /// <b>Features:</b>  
    /// - Creates and renews Microsoft Graph subscriptions for monitoring user mailboxes  
    /// - Caches GraphServiceClient for 50 minutes to optimize token usage  
    /// - Enforces TLS 1.2/1.3 for all communications  
    /// - Generates cryptographically secure client state values  
    /// - Includes comprehensive telemetry and structured logging  
    /// </para>  
    /// <para>  
    /// <b>Security:</b>  
    /// - Uses Azure AD client credentials with automatic token refresh  
    /// - Client state values generated using <see cref="RandomNumberGenerator"/>  
    /// - All sensitive configuration retrieved from IConfiguration (supports Azure Key Vault)  
    /// </para>  
    /// <para>  
    /// <b>Performance:</b>  
    /// - HTTP/2 enabled with HTTP/1.1 fallback  
    /// - Connection pooling optimized for Microsoft Graph API  
    /// - GraphServiceClient cached to reduce token acquisition overhead  
    /// </para>  
    /// </remarks>  
    public class GraphSubscriptionService : IGraphSubscriptionClient
    {
        private readonly IConfiguration _config;
        private readonly ILogger<GraphSubscriptionService> _logger;
        private readonly ICustomTelemetry _telemetry;
        private readonly IMemoryCache _tokenCache;

        /// <summary>  
        /// Initializes a new instance of the <see cref="GraphSubscriptionService"/> class.  
        /// </summary>  
        /// <param name="config">Application configuration containing Azure AD and Graph settings.</param>  
        /// <param name="logger">Logger for structured logging.</param>  
        /// <param name="telemetry">Custom telemetry service for tracking metrics.</param>  
        /// <param name="tokenCache">Memory cache for GraphServiceClient instances.</param>  
        /// <exception cref="ArgumentNullException">Thrown when any parameter is null.</exception>  
        public GraphSubscriptionService(
            IConfiguration config,
            ILogger<GraphSubscriptionService> logger,
            ICustomTelemetry telemetry,
            IMemoryCache tokenCache)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
            _tokenCache = tokenCache ?? throw new ArgumentNullException(nameof(tokenCache));
        }

        /// <inheritdoc/>  
        public async Task<MailSubscription> CreateMailSubscriptionAsync(
            string userId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(userId, nameof(userId));

            _logger.LogInformation(
                "Creating Microsoft Graph subscription for UserId: {UserId}",
                userId);

            _telemetry.TrackEvent("GraphSubscription_Create_Start", new Dictionary<string, string>
            {
                { "UserId", userId }
            });

            var graphClient = await GetOrCreateGraphClientAsync(cancellationToken);

            var notificationUrl = ValidateAndGetNotificationUrl();
            var expirationHours = _config.GetValue<int>("Graph:SubscriptionExpirationHours", 48);

            var subscription = new Subscription
            {
                ChangeType = "created",
                Resource = $"/users/{userId}/messages",
                NotificationUrl = notificationUrl,
                ExpirationDateTime = DateTime.UtcNow.AddHours(expirationHours),
                ClientState = GenerateSecureClientState()
            };

            try
            {
                var created = await graphClient.Subscriptions
                    .PostAsync(subscription, cancellationToken: cancellationToken);

                if (created?.Id == null)
                {
                    throw new InvalidOperationException(
                        "Graph API returned null or incomplete subscription response.");
                }

                _logger.LogInformation(
                    "✅ Graph subscription created successfully. " +
                    "SubscriptionId: {SubscriptionId}, Resource: {Resource}, ExpiresAt: {ExpiresAt:O}",
                    created.Id,
                    subscription.Resource,
                    created.ExpirationDateTime);

                _telemetry.TrackEvent("GraphSubscription_Create_Success", new Dictionary<string, string>
                {
                    { "UserId", userId },
                    { "SubscriptionId", created.Id },
                    { "Resource", subscription.Resource ?? "Unknown" },
                    { "ExpiresAt", created.ExpirationDateTime?.ToString("O") ?? "Unknown" }
                });

                return MapToMailSubscription(created, userId);
            }
            catch (ODataError odataEx)
            {
                HandleODataError(odataEx, userId, "CreateMailSubscriptionAsync");
                throw; // Never reached due to throw in HandleODataError  
            }
            catch (HttpRequestException httpEx)
            {
                HandleHttpRequestException(httpEx, userId, "CreateMailSubscriptionAsync");
                throw;
            }
            catch (ServiceException svcEx)
            {
                HandleServiceException(svcEx, userId, "CreateMailSubscriptionAsync");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "❌ Unexpected error creating Graph subscription for UserId: {UserId}",
                    userId);

                _telemetry.TrackException(ex, new Dictionary<string, string>
                {
                    { "Operation", "CreateMailSubscriptionAsync" },
                    { "UserId", userId },
                    { "ExceptionType", ex.GetType().Name }
                });

                throw;
            }
        }

        /// <summary>
        /// Gets or creates a cached <see cref="GraphServiceClient"/> instance with optimized settings.
        /// </summary>
        /// <remarks>
        /// The GraphServiceClient is cached for 50 minutes (typical token lifetime minus buffer).
        /// Uses Azure AD client credentials with automatic token refresh.
        /// </remarks>
        private Task<GraphServiceClient> GetOrCreateGraphClientAsync(
            CancellationToken cancellationToken)
        {
            const string cacheKey = "GraphServiceClient";

            if (_tokenCache.TryGetValue(cacheKey, out GraphServiceClient? cachedClient) && cachedClient != null)
            {
                _logger.LogDebug("✅ Using cached GraphServiceClient instance.");
                return Task.FromResult(cachedClient);
            }

            _logger.LogInformation("🔧 Creating new GraphServiceClient instance...");

            var clientId = _config["AzureAd:ClientId"]
                ?? throw new InvalidOperationException("AzureAd:ClientId is not configured.");
            var tenantId = _config["AzureAd:TenantId"]
                ?? throw new InvalidOperationException("AzureAd:TenantId is not configured.");
            var clientSecret = _config["AzureAd:ClientSecret"]
                ?? throw new InvalidOperationException("AzureAd:ClientSecret is not configured.");

            var credential = new ClientSecretCredential(
                tenantId,
                clientId,
                clientSecret,
                new ClientSecretCredentialOptions
                {
                    AuthorityHost = AzureAuthorityHosts.AzurePublicCloud
                });

            var httpClientHandler = new SocketsHttpHandler
            {
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 |
                                         System.Security.Authentication.SslProtocols.Tls13
                },
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                MaxConnectionsPerServer = 20,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseCookies = false,
                AllowAutoRedirect = true,
                ConnectTimeout = TimeSpan.FromSeconds(15)
            };

            var httpClient = new HttpClient(httpClientHandler)
            {
                Timeout = TimeSpan.FromSeconds(100),
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
            };

            httpClient.DefaultRequestHeaders.Add("User-Agent", "MailSubscriptionFunctionApp/1.0");
            httpClient.DefaultRequestHeaders.Add("Accept", "application/json");

            var graphClient = new GraphServiceClient(
                httpClient,
                credential,
                new[] { "https://graph.microsoft.com/.default" });

            var cacheOptions = new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(TimeSpan.FromMinutes(50))
                .SetPriority(CacheItemPriority.High)
                .RegisterPostEvictionCallback((key, value, reason, state) =>
                {
                    _logger.LogInformation(
                        "🗑️ GraphServiceClient evicted from cache. Reason: {Reason}",
                        reason);

                    if (value is IDisposable disposable)
                    {
                        try
                        {
                            disposable.Dispose();
                            _logger.LogDebug("✅ GraphServiceClient disposed successfully.");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(
                                ex,
                                "⚠️ Error disposing GraphServiceClient: {Message}",
                                ex.Message);
                        }
                    }
                });

            _tokenCache.Set(cacheKey, graphClient, cacheOptions);

            _logger.LogInformation(
                "✅ New GraphServiceClient created and cached for 50 minutes. " +
                "TLS 1.2+/1.3 enforced, HTTP/2 enabled with HTTP/1.1 fallback.");

            return Task.FromResult(graphClient);
        }

        /// <summary>  
        /// Validates and retrieves the notification URL from configuration.  
        /// </summary>  
        private string ValidateAndGetNotificationUrl()
        {
            var notificationUrl = _config["Graph:NotificationUrl"]
                ?? throw new InvalidOperationException(
                    "Graph:NotificationUrl is not configured. Please set it in configuration.");

            if (!Uri.TryCreate(notificationUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidOperationException(
                    $"Graph:NotificationUrl must be a valid HTTPS URL. Current value: {notificationUrl}");
            }

            return notificationUrl;
        }

        /// <summary>  
        /// Handles ODataError exceptions from Microsoft Graph API.  
        /// </summary>  
        private void HandleODataError(ODataError odataEx, string contextId, string operation)
        {
            var errorCode = odataEx.Error?.Code ?? "Unknown";
            var errorMessage = odataEx.Error?.Message ?? odataEx.Message;
            var errorTarget = odataEx.Error?.Target;
            var errorDetails = odataEx.Error?.InnerError?.AdditionalData;

            _logger.LogError(
                odataEx,
                "❌ Graph API OData error in {Operation} for {ContextId}. " +
                "Code: {ErrorCode}, Message: {ErrorMessage}, Target: {Target}, Details: {Details}",
                operation,
                contextId,
                errorCode,
                errorMessage,
                errorTarget ?? "None",
                errorDetails != null
                    ? string.Join(", ", errorDetails.Select(kvp => $"{kvp.Key}={kvp.Value}"))
                    : "None");

            _telemetry.TrackException(odataEx, new Dictionary<string, string>
            {
                { "Operation", operation },
                { "ContextId", contextId },
                { "ErrorCode", errorCode },
                { "ErrorMessage", errorMessage },
                { "ErrorTarget", errorTarget ?? "Unknown" }
            });

            throw new InvalidOperationException(
                $"Graph API error in {operation}. " +
                $"Error Code: {errorCode}, Message: {errorMessage}" +
                (errorTarget != null ? $", Target: {errorTarget}" : string.Empty),
                odataEx);
        }

        /// <summary>  
        /// Handles HttpRequestException from network layer.  
        /// </summary>  
        private void HandleHttpRequestException(HttpRequestException httpEx, string contextId, string operation)
        {
            _logger.LogError(
                httpEx,
                "❌ HTTP request failed in {Operation} for {ContextId}. " +
                "StatusCode: {StatusCode}, Message: {Message}",
                operation,
                contextId,
                httpEx.StatusCode,
                httpEx.Message);

            _telemetry.TrackException(httpEx, new Dictionary<string, string>
            {
                { "Operation", operation },
                { "ContextId", contextId },
                { "ErrorType", "HttpRequestException" },
                { "StatusCode", httpEx.StatusCode?.ToString() ?? "Unknown" }
            });

            throw new InvalidOperationException(
                $"Network error in {operation}. " +
                $"Ensure outbound HTTPS connectivity to graph.microsoft.com is available. " +
                $"Status: {httpEx.StatusCode}, Error: {httpEx.Message}",
                httpEx);
        }

        /// <summary>  
        /// Handles ServiceException from Microsoft Graph SDK.  
        /// </summary>  
        private void HandleServiceException(ServiceException svcEx, string contextId, string operation)
        {
            _logger.LogError(
                svcEx,
                "❌ Graph API service exception in {Operation} for {ContextId}. " +
                "StatusCode: {StatusCode}, Message: {Message}",
                operation,
                contextId,
                svcEx.ResponseStatusCode,
                svcEx.Message);

            _telemetry.TrackException(svcEx, new Dictionary<string, string>
            {
                { "Operation", operation },
                { "ContextId", contextId },
                { "StatusCode", svcEx.ResponseStatusCode.ToString() }
            });

            throw new InvalidOperationException(
                $"Microsoft Graph service error in {operation}. " +
                $"Status: {svcEx.ResponseStatusCode}, Message: {svcEx.Message}",
                svcEx);
        }

        /// <summary>  
        /// Generates a cryptographically secure client state value for webhook validation.  
        /// </summary>  
        /// <returns>A Base64-encoded 32-byte random string.</returns>  
        private static string GenerateSecureClientState()
        {
            return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        }

        /// <summary>  
        /// Maps a Microsoft Graph <see cref="Subscription"/> to a <see cref="MailSubscription"/> entity.  
        /// </summary>  
        private MailSubscription MapToMailSubscription(Subscription created, string userId)
        {
            return new MailSubscription
            {
                SubscriptionId = created.Id ?? Guid.NewGuid().ToString(),
                UserId = userId,
                SubscriptionStartTime = DateTime.UtcNow,
                SubscriptionExpirationTime = created.ExpirationDateTime?.DateTime ?? DateTime.UtcNow.AddHours(48),
                NotificationUrl = _config["Graph:NotificationUrl"]!,
                ClientState = created.ClientState ?? GenerateSecureClientState(),
                CreatedDateTime = DateTime.UtcNow,
                LastRenewedDateTime = DateTime.UtcNow,
                SubscriptionChangeType = "created",
                ApplicationId = _config["AzureAd:ClientId"]
            };
        }
    }
}