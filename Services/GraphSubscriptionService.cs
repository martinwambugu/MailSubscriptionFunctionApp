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
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace MailSubscriptionFunctionApp.Services
{
    /// <summary>  
    /// Handles Microsoft Graph API interactions for creating mail subscriptions.  
    /// Implements token caching and secure client state generation.  
    /// </summary>  
    /// <remarks>  
    /// <para>  
    /// This service creates Microsoft Graph subscriptions for monitoring user mailbox changes.  
    /// It caches the <see cref="GraphServiceClient"/> instance for 1 hour to avoid recreating  
    /// authentication tokens on every request.  
    /// </para>  
    /// <para>  
    /// <b>Security:</b> Client state values are generated using cryptographically secure random bytes.  
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

            _logger.LogInformation("Creating Microsoft Graph subscription for UserId: {UserId}", userId);
            _telemetry.TrackEvent("GraphSubscription_Create_Start", new Dictionary<string, string>
            {
                { "UserId", userId }
            });

            // ✅ Get or create cached GraphServiceClient  
            var graphClient = await GetOrCreateGraphClientAsync(cancellationToken);

            var notificationUrl = _config["Graph:NotificationUrl"]
                ?? throw new InvalidOperationException(
                    "Graph:NotificationUrl is not configured. Please set it in configuration.");

            var subscription = new Subscription
            {
                ChangeType = "created",
                Resource = $"/users/{userId}/messages",
                NotificationUrl = notificationUrl,
                ExpirationDateTime = DateTime.UtcNow.AddHours(48),
                ClientState = GenerateSecureClientState()
            };

            try
            {
                // ✅ Create subscription via Microsoft Graph API  
                var created = await graphClient.Subscriptions
                    .PostAsync(subscription, cancellationToken: cancellationToken);

                if (created == null)
                {
                    throw new InvalidOperationException("Graph API returned null subscription.");
                }

                _logger.LogInformation(
                    "✅ Graph subscription created successfully. SubscriptionId: {SubscriptionId}",
                    created.Id);

                _telemetry.TrackEvent("GraphSubscription_Create_Success", new Dictionary<string, string>
                {
                    { "UserId", userId },
                    { "SubscriptionId", created.Id ?? "null" }
                });

                return MapToMailSubscription(created, userId);
            }
            catch (ODataError odataEx) // ✅ Correct exception type for Microsoft Graph SDK v5+  
            {
                var errorCode = odataEx.Error?.Code ?? "Unknown";
                var errorMessage = odataEx.Error?.Message ?? odataEx.Message;

                _logger.LogError(
                    odataEx,
                    "❌ Graph API error for UserId: {UserId}. Error Code: {ErrorCode}, Message: {ErrorMessage}",
                    userId, errorCode, errorMessage);

                _telemetry.TrackException(odataEx, new Dictionary<string, string>
                {
                    { "Operation", "CreateMailSubscriptionAsync" },
                    { "UserId", userId },
                    { "ErrorCode", errorCode }
                });

                throw new InvalidOperationException(
                    $"Failed to create Graph subscription for user {userId}. Error: {errorMessage}",
                    odataEx);
            }
            catch (ServiceException svcEx) // ✅ Fallback for older SDK versions  
            {
                _logger.LogError(
                    svcEx,
                    "❌ Graph API service exception for UserId: {UserId}. Status Code: {StatusCode}",
                    userId, svcEx.ResponseStatusCode);

                _telemetry.TrackException(svcEx, new Dictionary<string, string>
                {
                    { "Operation", "CreateMailSubscriptionAsync" },
                    { "UserId", userId },
                    { "StatusCode", svcEx.ResponseStatusCode.ToString() }
                });

                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Unexpected error creating Graph subscription for UserId: {UserId}", userId);
                _telemetry.TrackException(ex, new Dictionary<string, string>
                {
                    { "Operation", "CreateMailSubscriptionAsync" },
                    { "UserId", userId }
                });

                throw;
            }
        }

        /// <summary>  
        /// Gets or creates a cached <see cref="GraphServiceClient"/> instance.  
        /// </summary>  
        /// <remarks>  
        /// The client is cached for 1 hour to avoid recreating authentication tokens on every request.  
        /// </remarks>  
        private async Task<GraphServiceClient> GetOrCreateGraphClientAsync(
            CancellationToken cancellationToken)
        {
            const string cacheKey = "GraphServiceClient";

            // ✅ Check cache first  
            if (_tokenCache.TryGetValue(cacheKey, out GraphServiceClient? cachedClient) && cachedClient != null)
            {
                _logger.LogDebug("Using cached GraphServiceClient instance.");
                return cachedClient;
            }

            _logger.LogDebug("Creating new GraphServiceClient instance...");

            var clientId = _config["AzureAd:ClientId"]
                ?? throw new InvalidOperationException("AzureAd:ClientId is not configured.");
            var tenantId = _config["AzureAd:TenantId"]
                ?? throw new InvalidOperationException("AzureAd:TenantId is not configured.");
            var clientSecret = _config["AzureAd:ClientSecret"]
                ?? throw new InvalidOperationException(
                    "AzureAd:ClientSecret is not configured. Store this securely in Azure Key Vault.");

            // ✅ Create credential with client secret  
            var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);

            // ✅ Create Graph client with default scopes  
            var graphClient = new GraphServiceClient(credential, new[] { "https://graph.microsoft.com/.default" });

            // ✅ Cache for 1 hour  
            var cacheOptions = new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(TimeSpan.FromHours(1))
                .SetPriority(CacheItemPriority.High);

            _tokenCache.Set(cacheKey, graphClient, cacheOptions);

            _logger.LogInformation("✅ New GraphServiceClient created and cached for 1 hour.");

            return graphClient;
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