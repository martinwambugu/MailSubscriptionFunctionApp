using MailSubscriptionFunctionApp.Interfaces;
using MailSubscriptionFunctionApp.Models;
using Dapper;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace MailSubscriptionFunctionApp.Services
{
    /// <summary>  
    /// Provides persistence operations for <see cref="MailSubscription"/> entities using Dapper.  
    /// </summary>  
    /// <remarks>  
    /// <para>  
    /// This service handles database interactions for mail subscription records.  
    /// It uses parameterized queries to prevent SQL injection and includes comprehensive error handling.  
    /// </para>  
    /// <para>  
    /// <b>Database Schema:</b> Targets the <c>mail_subscriptions</c> table in PostgreSQL.  
    /// </para>  
    /// </remarks>  
    public class MailSubscriptionService : IMailSubscriptionRepository
    {
        private readonly IDbConnectionFactory _dbFactory;
        private readonly ILogger<MailSubscriptionService> _logger;
        private readonly ICustomTelemetry _telemetry;

        /// <summary>  
        /// Initializes a new instance of the <see cref="MailSubscriptionService"/> class.  
        /// </summary>  
        public MailSubscriptionService(
            IDbConnectionFactory dbFactory,
            ILogger<MailSubscriptionService> logger,
            ICustomTelemetry telemetry)
        {
            _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        }

        /// <inheritdoc/>  
        public async Task SaveSubscriptionAsync(
            MailSubscription subscription,
            CancellationToken cancellationToken = default)
        {
            // ✅ Input validation  
            ArgumentNullException.ThrowIfNull(subscription);

            if (string.IsNullOrWhiteSpace(subscription.SubscriptionId))
                throw new ArgumentException("SubscriptionId cannot be empty.", nameof(subscription));

            if (string.IsNullOrWhiteSpace(subscription.UserId))
                throw new ArgumentException("UserId cannot be empty.", nameof(subscription));

            if (subscription.SubscriptionExpirationTime <= DateTime.UtcNow)
                throw new ArgumentException("Expiration time must be in the future.", nameof(subscription));

            // ✅ Use lowercase snake_case column names (PostgreSQL convention)  
            const string sql = @"  
                INSERT INTO mail_subscriptions (  
                    subscription_id,  
                    user_id,  
                    subscription_start_time,  
                    subscription_expiration_time,  
                    notification_url,  
                    client_state,  
                    created_date_time,  
                    last_renewed_date_time,  
                    subscription_change_type,  
                    application_id  
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
                )  
                ON CONFLICT (subscription_id)   
                DO UPDATE SET  
                    last_renewed_date_time = EXCLUDED.last_renewed_date_time,  
                    subscription_expiration_time = EXCLUDED.subscription_expiration_time;";

            try
            {
                // ✅ Use async connection creation  
                using var conn = await _dbFactory.CreateConnectionAsync(cancellationToken);

                // ✅ Use CommandDefinition for proper cancellation support  
                var command = new CommandDefinition(
                    sql,
                    subscription,
                    cancellationToken: cancellationToken,
                    commandTimeout: 30 // 30 seconds timeout  
                );

                var rowsAffected = await conn.ExecuteAsync(command);

                if (rowsAffected == 0)
                {
                    _logger.LogWarning(
                        "⚠️ No rows affected for SubscriptionId: {SubscriptionId}. This may indicate a database issue.",
                        subscription.SubscriptionId);
                }
                else
                {
                    _logger.LogInformation(
                        "✅ Subscription persisted successfully. UserId: {UserId}, SubscriptionId: {SubscriptionId}, RowsAffected: {RowsAffected}",
                        subscription.UserId, subscription.SubscriptionId, rowsAffected);
                }

                _telemetry.TrackEvent("MailSubscription_Saved", new Dictionary<string, string>
                {
                    { "UserId", subscription.UserId },
                    { "SubscriptionId", subscription.SubscriptionId },
                    { "RowsAffected", rowsAffected.ToString() }
                });
            }
            catch (PostgresException pgEx) when (pgEx.SqlState == "23505") // Unique constraint violation  
            {
                _logger.LogWarning(
                    pgEx,
                    "⚠️ Duplicate subscription detected for SubscriptionId: {SubscriptionId}",
                    subscription.SubscriptionId);

                _telemetry.TrackException(pgEx, new Dictionary<string, string>
                {
                    { "Operation", "SaveSubscriptionAsync" },
                    { "ErrorType", "DuplicateKey" },
                    { "SubscriptionId", subscription.SubscriptionId }
                });

                throw new InvalidOperationException(
                    $"Subscription with ID '{subscription.SubscriptionId}' already exists in the database.",
                    pgEx);
            }
            catch (PostgresException pgEx) when (pgEx.SqlState == "23503") // Foreign key violation  
            {
                _logger.LogError(
                    pgEx,
                    "❌ Foreign key constraint violation for UserId: {UserId}",
                    subscription.UserId);

                _telemetry.TrackException(pgEx, new Dictionary<string, string>
                {
                    { "Operation", "SaveSubscriptionAsync" },
                    { "ErrorType", "ForeignKeyViolation" },
                    { "UserId", subscription.UserId }
                });

                throw new InvalidOperationException(
                    $"User '{subscription.UserId}' does not exist in the database.",
                    pgEx);
            }
            catch (PostgresException pgEx)
            {
                _logger.LogError(
                    pgEx,
                    "❌ PostgreSQL error while saving subscription. SQL State: {SqlState}, Error Code: {ErrorCode}",
                    pgEx.SqlState, pgEx.ErrorCode);

                _telemetry.TrackException(pgEx, new Dictionary<string, string>
                {
                    { "Operation", "SaveSubscriptionAsync" },
                    { "SqlState", pgEx.SqlState ?? "Unknown" },
                    { "UserId", subscription.UserId }
                });

                throw;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("⚠️ Save operation was cancelled for SubscriptionId: {SubscriptionId}",
                    subscription.SubscriptionId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Unexpected error while saving subscription for UserId: {UserId}",
                    subscription.UserId);

                _telemetry.TrackException(ex, new Dictionary<string, string>
                {
                    { "Operation", "SaveSubscriptionAsync" },
                    { "UserId", subscription.UserId }
                });

                throw;
            }
        }
    }
}