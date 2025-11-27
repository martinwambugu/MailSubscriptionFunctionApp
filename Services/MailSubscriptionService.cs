using MailSubscriptionFunctionApp.Interfaces;
using MailSubscriptionFunctionApp.Models;
using Dapper;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MailSubscriptionFunctionApp.Services
{
    /// <summary>  
    /// Provides persistence operations for <see cref="MailSubscription"/> using Dapper.  
    /// </summary>  
    public class MailSubscriptionService : IMailSubscriptionRepository
    {
        private readonly IDbConnectionFactory _dbFactory;
        private readonly ILogger<MailSubscriptionService> _logger;
        private readonly ICustomTelemetry _telemetry;

        public MailSubscriptionService(IDbConnectionFactory dbFactory, ILogger<MailSubscriptionService> logger, ICustomTelemetry telemetry)
        {
            _dbFactory = dbFactory;
            _logger = logger;
            _telemetry = telemetry;
        }

        /// <inheritdoc/>  
        public async Task SaveSubscriptionAsync(MailSubscription subscription)
        {
            const string sql = @"  
                INSERT INTO ""MailSubscriptions"" (  
                    ""SubscriptionId"",  
                    ""UserId"",  
                    ""SubscriptionStartTime"",  
                    ""SubscriptionExpirationTime"",  
                    ""NotificationUrl"",  
                    ""ClientState"",  
                    ""CreatedDateTime"",  
                    ""LastRenewedDateTime"",  
                    ""SubscriptionChangeType"",  
                    ""ApplicationId""  
                )  
                VALUES (  
                    @SubscriptionId,  
                    @UserId,  
                    @SubscriptionStartTime,  
                    @SubscriptionExpirationTime,  
                    @NotificationUrl,  
                    @ClientState,  
                    @CreatedDateTime,  
                    @LastRenewedDateTime,  
                    @SubscriptionChangeType,  
                    @ApplicationId  
                );";

            try
            {
                using var conn = _dbFactory.CreateConnection();
                await ((dynamic)conn).OpenAsync();
                await conn.ExecuteAsync(sql, subscription);

                _logger.LogInformation("Subscription persisted for UserId: {UserId}, SubscriptionId: {SubscriptionId}",
                    subscription.UserId, subscription.SubscriptionId);

                _telemetry.TrackEvent("MailSubscription_Saved", new Dictionary<string, string>
                {
                    { "UserId", subscription.UserId },
                    { "SubscriptionId", subscription.SubscriptionId }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save subscription for {UserId}", subscription.UserId);
                _telemetry.TrackException(ex, new Dictionary<string, string> { { "Operation", "SaveSubscriptionAsync" }, { "UserId", subscription.UserId } });
                throw;
            }
        }
    }
}