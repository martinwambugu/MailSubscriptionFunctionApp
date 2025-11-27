using Azure.Identity;
using MailSubscriptionFunctionApp.Interfaces;
using MailSubscriptionFunctionApp.Models;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MailSubscriptionFunctionApp.Services
{
    /// <summary>  
    /// Handles Microsoft Graph API interactions for creating subscriptions.  
    /// </summary>  
    public class GraphSubscriptionService : IGraphSubscriptionClient
    {
        private readonly IConfiguration _config;
        private readonly ILogger<GraphSubscriptionService> _logger;
        private readonly ICustomTelemetry _telemetry;

        public GraphSubscriptionService(IConfiguration config, ILogger<GraphSubscriptionService> logger, ICustomTelemetry telemetry)
        {
            _config = config;
            _logger = logger;
            _telemetry = telemetry;
        }

        /// <inheritdoc/>  
        public async Task<MailSubscription> CreateMailSubscriptionAsync(string userId)
        {
            _logger.LogInformation("Creating Microsoft Graph subscription for UserId: {UserId}", userId);
            _telemetry.TrackEvent("GraphSubscription_Create_Start", new Dictionary<string, string> { { "UserId", userId } });

            var clientId = _config["AzureAd:ClientId"] ?? throw new InvalidOperationException("Missing AzureAd:ClientId");
            var tenantId = _config["AzureAd:TenantId"] ?? throw new InvalidOperationException("Missing AzureAd:TenantId");
            var clientSecret = _config["AzureAd:ClientSecret"] ?? throw new InvalidOperationException("Missing AzureAd:ClientSecret");
            var notificationUrl = _config["Graph:NotificationUrl"] ?? throw new InvalidOperationException("Missing Graph:NotificationUrl");

            var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
            var graphClient = new GraphServiceClient(credential, new[] { "https://graph.microsoft.com/.default" });

            var subscription = new Subscription
            {
                ChangeType = "created",
                Resource = $"/users/{userId}/messages",
                NotificationUrl = notificationUrl,
                ExpirationDateTime = DateTime.UtcNow.AddHours(48),
                ClientState = Guid.NewGuid().ToString()
            };

            try
            {
                var created = await graphClient.Subscriptions.PostAsync(subscription);
                _logger.LogInformation("Graph subscription created successfully. SubscriptionId: {Id}", created.Id);

                _telemetry.TrackEvent("GraphSubscription_Create_Success", new Dictionary<string, string>
                {
                    { "UserId", userId }, { "SubscriptionId", created.Id ?? "null" }
                });

                return new MailSubscription
                {
                    SubscriptionId = created.Id ?? Guid.NewGuid().ToString(),
                    UserId = userId,
                    SubscriptionStartTime = DateTime.UtcNow,
                    SubscriptionExpirationTime = created.ExpirationDateTime?.DateTime ?? DateTime.UtcNow.AddHours(48),
                    NotificationUrl = notificationUrl,
                    ClientState = created.ClientState ?? subscription.ClientState!,
                    CreatedDateTime = DateTime.UtcNow,
                    LastRenewedDateTime = DateTime.UtcNow,
                    SubscriptionChangeType = "created",
                    ApplicationId = clientId
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating Graph subscription for {UserId}", userId);
                _telemetry.TrackException(ex, new Dictionary<string, string> { { "Operation", "CreateMailSubscriptionAsync" }, { "UserId", userId } });
                throw;
            }
        }
    }
}