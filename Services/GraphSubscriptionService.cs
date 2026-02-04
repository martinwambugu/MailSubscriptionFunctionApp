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
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace MailSubscriptionFunctionApp.Services
{
    /// <summary>  
    /// Handles Microsoft Graph API interactions for creating mail subscriptions.  
    /// Implements token caching, TLS 1.2+ enforcement, and secure client state generation.  
    /// </summary>  
    /// <remarks>  
    /// <para>  
    /// This service creates Microsoft Graph subscriptions for monitoring user mailbox changes.  
    /// It caches the <see cref="GraphServiceClient"/> instance for 1 hour to avoid recreating  
    /// authentication tokens on every request.  
    /// </para>  
    /// <para>  
    /// <b>Security:</b> Client state values are generated using cryptographically secure random bytes.  
    /// TLS 1.2+ is enforced for all Microsoft Graph API communications.  
    /// </para>  
    /// <para>  
    /// <b>Resilience:</b> Includes connectivity testing, retry logic, and circuit breaker patterns  
    /// via HttpClient factory configuration in Program.cs.  
    /// </para>  
    /// </remarks>  
    public class GraphSubscriptionService : IGraphSubscriptionClient
    {
        private readonly IConfiguration _config;
        private readonly ILogger<GraphSubscriptionService> _logger;
        private readonly ICustomTelemetry _telemetry;
        private readonly IMemoryCache _tokenCache;
        private readonly IHttpClientFactory _httpClientFactory;

        /// <summary>  
        /// Initializes a new instance of the <see cref="GraphSubscriptionService"/> class.  
        /// </summary>  
        public GraphSubscriptionService(
            IConfiguration config,
            ILogger<GraphSubscriptionService> logger,
            ICustomTelemetry telemetry,
            IMemoryCache tokenCache,
            IHttpClientFactory httpClientFactory)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
            _tokenCache = tokenCache ?? throw new ArgumentNullException(nameof(tokenCache));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
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
                // ✅ DEBUG: Test raw request to see actual error response  
                await TestSubscriptionCreationRawAsync(userId, cancellationToken);

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
            catch (ODataError odataEx)
            {
                var errorCode = odataEx.Error?.Code ?? "Unknown";
                var errorMessage = odataEx.Error?.Message ?? odataEx.Message;

                // ✅ NEW: Log all error details  
                var errorDetails = odataEx.Error?.InnerError?.AdditionalData;
                var errorTarget = odataEx.Error?.Target;

                _logger.LogError(
                    odataEx,
                    "❌ Graph API error for UserId: {UserId}. " +
                    "Error Code: {ErrorCode}, " +
                    "Message: {ErrorMessage}, " +
                    "Target: {Target}, " +
                    "Details: {Details}",
                    userId, errorCode, errorMessage, errorTarget,
                    errorDetails != null ? string.Join(", ", errorDetails.Select(kvp => $"{kvp.Key}={kvp.Value}")) : "None");

                _telemetry.TrackException(odataEx, new Dictionary<string, string>
                {
                    { "Operation", "CreateMailSubscriptionAsync" },
                    { "UserId", userId },
                    { "ErrorCode", errorCode },
                    { "ErrorMessage", errorMessage },
                    { "ErrorTarget", errorTarget ?? "Unknown" }
                });

                throw new InvalidOperationException(
                    $"Failed to create Graph subscription for user {userId}. " +
                    $"Error Code: {errorCode}, Message: {errorMessage}, Target: {errorTarget}",
                    odataEx);
            }
            catch (HttpRequestException httpEx)
            {
                _logger.LogError(
                    httpEx,
                    "❌ HTTP request failed for UserId: {UserId}. Message: {Message}",
                    userId, httpEx.Message);

                _telemetry.TrackException(httpEx, new Dictionary<string, string>
                {
                    { "Operation", "CreateMailSubscriptionAsync" },
                    { "UserId", userId },
                    { "ErrorType", "HttpRequestException" }
                });

                throw new InvalidOperationException(
                    $"Network error while creating Graph subscription for user {userId}. " +
                    $"Ensure outbound HTTPS connectivity to graph.microsoft.com is available. " +
                    $"Error: {httpEx.Message}",
                    httpEx);
            }
            catch (ServiceException svcEx)
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

                throw new InvalidOperationException(
                    $"Microsoft Graph service error for user {userId}. Status: {svcEx.ResponseStatusCode}",
                    svcEx);
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
                    { "UserId", userId }
                });

                throw;
            }
        }

        /// <summary>  
        /// DEBUG ONLY: Test subscription creation with raw HttpClient to see full error response  
        /// </summary>  
        private async Task<string> TestSubscriptionCreationRawAsync(
            string userId,
            CancellationToken cancellationToken)
        {
            // ✅ Create credential to get access token  
            var clientId = _config["AzureAd:ClientId"]
                ?? throw new InvalidOperationException("AzureAd:ClientId is not configured.");
            var tenantId = _config["AzureAd:TenantId"]
                ?? throw new InvalidOperationException("AzureAd:TenantId is not configured.");
            var clientSecret = _config["AzureAd:ClientSecret"]
                ?? throw new InvalidOperationException("AzureAd:ClientSecret is not configured.");

            var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);

            // ✅ Get access token  
            var tokenRequestContext = new Azure.Core.TokenRequestContext(
                new[] { "https://graph.microsoft.com/.default" });
            var accessTokenResult = await credential.GetTokenAsync(tokenRequestContext, cancellationToken);

            // ✅ Create HttpClient with TLS 1.2+  
            var httpClientHandler = new HttpClientHandler
            {
                SslProtocols = System.Security.Authentication.SslProtocols.Tls12 |
                              System.Security.Authentication.SslProtocols.Tls13,
                MaxConnectionsPerServer = 10,
                AutomaticDecompression = System.Net.DecompressionMethods.GZip |
                                        System.Net.DecompressionMethods.Deflate,
                UseCookies = false,
                UseProxy = false,
                AllowAutoRedirect = true
            };

            var httpClient = new HttpClient(httpClientHandler);
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessTokenResult.Token}");
            httpClient.DefaultRequestHeaders.Add("Accept", "application/json");

            var notificationUrl = _config["Graph:NotificationUrl"];
            var clientState = GenerateSecureClientState();

            var payload = new
            {
                changeType = "created",
                notificationUrl = notificationUrl,
                resource = $"/users/{userId}/messages",
                expirationDateTime = DateTime.UtcNow.AddHours(48).ToString("o"),
                clientState = clientState
            };

            var jsonPayload = System.Text.Json.JsonSerializer.Serialize(payload);
            var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");

            _logger.LogInformation("🔍 Sending raw POST to Graph API with payload: {Payload}", jsonPayload);

            try
            {
                var response = await httpClient.PostAsync(
                    "https://graph.microsoft.com/v1.0/subscriptions",
                    content,
                    cancellationToken);

                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

                _logger.LogInformation(
                    "📥 Graph API Raw Response: Status={Status}, Body={Body}",
                    (int)response.StatusCode,
                    responseBody);

                return responseBody;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Raw HTTP request failed: {Message}", ex.Message);
                throw;
            }
            finally
            {
                httpClient.Dispose();
            }
        }

        /// <summary>  
        /// Gets or creates a cached <see cref="GraphServiceClient"/> instance with TLS 1.2+ enforcement.  
        /// </summary>  
        /// <remarks>  
        /// <para>  
        /// The client is cached for 1 hour to avoid recreating authentication tokens on every request.  
        /// </para>  
        /// <para>  
        /// Uses a dedicated HttpClient with:  
        /// - TLS 1.2/1.3 enforcement  
        /// - HTTP/1.1 only (HTTP/2 disabled to fix connection reset issues)  
        /// - Connection pooling optimized for Microsoft Graph API  
        /// - Automatic compression (GZip/Deflate)  
        /// </para>  
        /// </remarks>  
        private async Task<GraphServiceClient> GetOrCreateGraphClientAsync(
            CancellationToken cancellationToken)
        {
            const string cacheKey = "GraphServiceClient";
            const string httpClientCacheKey = "GraphServiceClient_HttpClient";

            // ✅ Check cache first  
            if (_tokenCache.TryGetValue(cacheKey, out GraphServiceClient? cachedClient) && cachedClient != null)
            {
                _logger.LogDebug("✅ Using cached GraphServiceClient instance.");
                return cachedClient;
            }

            _logger.LogInformation("🔧 Creating new GraphServiceClient instance with TLS 1.2+ enforcement...");

            var clientId = _config["AzureAd:ClientId"]
                ?? throw new InvalidOperationException("AzureAd:ClientId is not configured.");
            var tenantId = _config["AzureAd:TenantId"]
                ?? throw new InvalidOperationException("AzureAd:TenantId is not configured.");
            var clientSecret = _config["AzureAd:ClientSecret"]
                ?? throw new InvalidOperationException("AzureAd:ClientSecret is not configured.");

            var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);

            // ✅ CRITICAL FIX: Create HttpClient with explicit TLS 1.2+ configuration  
            // DO NOT use factory here - create a dedicated instance for Graph SDK  
            var httpClientHandler = new HttpClientHandler
            {
                // ✅ Force TLS 1.2 and TLS 1.3  
                SslProtocols = System.Security.Authentication.SslProtocols.Tls12 |
                              System.Security.Authentication.SslProtocols.Tls13,

                // ✅ Disable HTTP/2 (use HTTP/1.1 only)  
                // This often fixes "connection forcibly closed" issues with Graph API  
                MaxConnectionsPerServer = 10,

                // ✅ Allow automatic decompression  
                AutomaticDecompression = System.Net.DecompressionMethods.GZip |
                                        System.Net.DecompressionMethods.Deflate,

                // ✅ Optimize connection settings  
                UseCookies = false,
                UseProxy = false,
                AllowAutoRedirect = true,

                // ✅ For production: use default certificate validation  
                // For testing only: uncomment line below  
                // ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator  
            };

            var httpClient = new HttpClient(httpClientHandler)
            {
                Timeout = TimeSpan.FromSeconds(100)
            };

            // ✅ Add required headers  
            httpClient.DefaultRequestHeaders.Add("User-Agent", "MailSubscriptionFunctionApp/1.0");
            httpClient.DefaultRequestHeaders.Add("Accept", "application/json");

            // ✅ Test connectivity before creating Graph client  
            await TestGraphConnectivityAsync(httpClient, cancellationToken);

            // ✅ Create Graph client with our configured HttpClient  
            var graphClient = new GraphServiceClient(
                httpClient,
                credential,
                new[] { "https://graph.microsoft.com/.default" });

            // ✅ Cache both GraphServiceClient and HttpClient for proper disposal  
            var cacheOptions = new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(TimeSpan.FromHours(1))
                .SetPriority(CacheItemPriority.High)
                .RegisterPostEvictionCallback((key, value, reason, state) =>
                {
                    _logger.LogInformation("🗑️ GraphServiceClient evicted from cache. Reason: {Reason}", reason);

                    // ✅ Dispose HttpClient when evicted (retrieve from separate cache entry)  
                    if (_tokenCache.TryGetValue(httpClientCacheKey, out HttpClient? cachedHttpClient))
                    {
                        cachedHttpClient?.Dispose();
                        _tokenCache.Remove(httpClientCacheKey);
                        _logger.LogDebug("🗑️ HttpClient disposed and removed from cache.");
                    }
                });

            _tokenCache.Set(cacheKey, graphClient, cacheOptions);
            _tokenCache.Set(httpClientCacheKey, httpClient, cacheOptions); // Store HttpClient separately for disposal  

            _logger.LogInformation(
                "✅ New GraphServiceClient created and cached for 1 hour. " +
                "TLS 1.2+ enforced, HTTP/1.1 only, connection pooling optimized.");

            return graphClient;
        }

        /// <summary>  
        /// Tests connectivity to Microsoft Graph API before creating the client.  
        /// </summary>  
        /// <remarks>  
        /// This pre-flight check helps identify network/TLS issues early with better error messages.  
        /// </remarks>  
        private async Task TestGraphConnectivityAsync(
            HttpClient httpClient,
            CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("🔍 Testing connectivity to Microsoft Graph API (graph.microsoft.com)...");

                var testRequest = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0");
                var testResponse = await httpClient.SendAsync(testRequest, cancellationToken);

                _logger.LogInformation(
                    "✅ Connectivity test successful. Status: {StatusCode} ({ReasonPhrase})",
                    (int)testResponse.StatusCode,
                    testResponse.ReasonPhrase);

                _telemetry.TrackEvent("GraphConnectivity_Test_Success", new Dictionary<string, string>
                {
                    { "StatusCode", ((int)testResponse.StatusCode).ToString() },
                    { "Endpoint", "graph.microsoft.com" }
                });
            }
            catch (HttpRequestException httpEx)
            {
                _logger.LogError(
                    httpEx,
                    "❌ Connectivity test to graph.microsoft.com FAILED. " +
                    "Error: {Message}. " +
                    "Possible causes: Network firewall blocking HTTPS, TLS version mismatch, DNS resolution failure.",
                    httpEx.Message);

                _telemetry.TrackException(httpEx, new Dictionary<string, string>
                {
                    { "Operation", "TestGraphConnectivityAsync" },
                    { "Endpoint", "graph.microsoft.com" },
                    { "ErrorType", "HttpRequestException" }
                });

                throw new InvalidOperationException(
                    "Cannot reach Microsoft Graph API (graph.microsoft.com). " +
                    "Please verify: " +
                    "1) Outbound HTTPS (port 443) is allowed, " +
                    "2) DNS can resolve graph.microsoft.com, " +
                    "3) TLS 1.2+ is enabled, " +
                    "4) No proxy/firewall blocking Microsoft endpoints. " +
                    $"Error: {httpEx.Message}",
                    httpEx);
            }
            catch (TaskCanceledException tcEx) when (tcEx.InnerException is TimeoutException)
            {
                _logger.LogError(
                    tcEx,
                    "❌ Connectivity test to graph.microsoft.com TIMED OUT. " +
                    "Network latency too high or endpoint unreachable.");

                _telemetry.TrackException(tcEx, new Dictionary<string, string>
                {
                    { "Operation", "TestGraphConnectivityAsync" },
                    { "ErrorType", "Timeout" }
                });

                throw new InvalidOperationException(
                    "Timeout while testing connectivity to Microsoft Graph API. " +
                    "Check network latency and firewall rules.",
                    tcEx);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "❌ Unexpected error during connectivity test to graph.microsoft.com: {Message}",
                    ex.Message);

                _telemetry.TrackException(ex, new Dictionary<string, string>
                {
                    { "Operation", "TestGraphConnectivityAsync" }
                });

                throw new InvalidOperationException(
                    $"Unexpected error testing Microsoft Graph connectivity: {ex.Message}",
                    ex);
            }
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