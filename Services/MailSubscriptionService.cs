using MailSubscriptionFunctionApp.Interfaces;
using MailSubscriptionFunctionApp.Models;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace MailSubscriptionFunctionApp.Services
{
    /// <summary>  
    /// Provides persistence operations for <see cref="MailSubscription"/> entities using Dapper.  
    /// Aligns with existing PostgreSQL table <c>mailsubscriptions</c> (lowercase, no underscores).  
    /// </summary>  
    /// <remarks>  
    /// <para>  
    /// This service handles database interactions for mail subscription records.  
    /// It uses parameterized queries to prevent SQL injection and includes comprehensive error handling.  
    /// </para>  
    /// <para>  
    /// <b>Database Schema:</b> Targets the <c>mailsubscriptions</c> table in PostgreSQL.  
    /// Column names follow the existing convention: lowercase without underscores (e.g., <c>subscriptionid</c>).  
    /// </para>  
    /// <para>  
    /// <b>Key Features:</b>  
    /// <list type="bullet">  
    /// <item>Idempotent upsert operations (ON CONFLICT handling)</item>  
    /// <item>Parameterized queries preventing SQL injection</item>  
    /// <item>Comprehensive error handling with specific PostgreSQL error codes</item>  
    /// <item>Structured logging and telemetry tracking</item>  
    /// <item>Cancellation token support for graceful shutdown</item>  
    /// </list>  
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
        /// <param name="dbFactory">Database connection factory for creating PostgreSQL connections.</param>  
        /// <param name="logger">Logger instance for structured logging.</param>  
        /// <param name="telemetry">Custom telemetry service for tracking metrics and exceptions.</param>  
        /// <exception cref="ArgumentNullException">Thrown when any parameter is null.</exception>  
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
            ArgumentNullException.ThrowIfNull(subscription, nameof(subscription));

            if (string.IsNullOrWhiteSpace(subscription.SubscriptionId))
                throw new ArgumentException("SubscriptionId cannot be empty.", nameof(subscription));

            if (string.IsNullOrWhiteSpace(subscription.UserId))
                throw new ArgumentException("UserId cannot be empty.", nameof(subscription));

            if (subscription.SubscriptionExpirationTime <= DateTime.UtcNow)
                throw new ArgumentException(
                    "Expiration time must be in the future.",
                    nameof(subscription));

            // ✅ CRITICAL: Column names match existing database schema (lowercase, no underscores)  
            // Table: mailsubscriptions  
            // Columns: subscriptionid, userid, subscriptionstarttime, subscriptionexpirationtime,  
            //          notificationurl, clientstate, createddatetime, lastreneweddatetime,  
            //          subscriptionchangetype, applicationid  
            const string sql = @"  
                INSERT INTO mailsubscriptions (  
                    subscriptionid,  
                    userid,  
                    subscriptionstarttime,  
                    subscriptionexpirationtime,  
                    notificationurl,  
                    clientstate,  
                    createddatetime,  
                    lastreneweddatetime,  
                    subscriptionchangetype,  
                    applicationid  
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
                ON CONFLICT (subscriptionid)   
                DO UPDATE SET  
                    lastreneweddatetime = EXCLUDED.lastreneweddatetime,  
                    subscriptionexpirationtime = EXCLUDED.subscriptionexpirationtime;";

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
                        "⚠️ No rows affected for SubscriptionId: {SubscriptionId}. " +
                        "This may indicate a database issue or constraint violation.",
                        subscription.SubscriptionId);
                }
                else
                {
                    _logger.LogInformation(
                        "✅ Subscription persisted successfully. " +
                        "UserId: {UserId}, SubscriptionId: {SubscriptionId}, " +
                        "ExpiresAt: {ExpirationTime}, RowsAffected: {RowsAffected}",
                        subscription.UserId,
                        subscription.SubscriptionId,
                        subscription.SubscriptionExpirationTime,
                        rowsAffected);
                }

                _telemetry.TrackEvent("MailSubscription_Saved", new Dictionary<string, string>
                {
                    { "UserId", subscription.UserId },
                    { "SubscriptionId", subscription.SubscriptionId },
                    { "ExpirationTime", subscription.SubscriptionExpirationTime.ToString("O") },
                    { "RowsAffected", rowsAffected.ToString() }
                });
            }
            catch (PostgresException pgEx) when (pgEx.SqlState == "23505") // Unique constraint violation  
            {
                _logger.LogWarning(
                    pgEx,
                    "⚠️ Duplicate subscription detected for SubscriptionId: {SubscriptionId}. " +
                    "ON CONFLICT clause should have handled this - check database constraints.",
                    subscription.SubscriptionId);

                _telemetry.TrackException(pgEx, new Dictionary<string, string>
                {
                    { "Operation", "SaveSubscriptionAsync" },
                    { "ErrorType", "DuplicateKey" },
                    { "SubscriptionId", subscription.SubscriptionId },
                    { "SqlState", pgEx.SqlState ?? "Unknown" }
                });

                // ⚠️ NOTE: With ON CONFLICT, this should rarely occur  
                // If it does, it indicates a race condition or constraint issue  
                throw new InvalidOperationException(
                    $"Subscription with ID '{subscription.SubscriptionId}' already exists in the database. " +
                    "This may indicate a race condition or database constraint issue.",
                    pgEx);
            }
            catch (PostgresException pgEx) when (pgEx.SqlState == "23503") // Foreign key violation  
            {
                _logger.LogError(
                    pgEx,
                    "❌ Foreign key constraint violation for UserId: {UserId}. " +
                    "User does not exist in orguser table. " +
                    "ConstraintName: {ConstraintName}",
                    subscription.UserId,
                    pgEx.ConstraintName);

                _telemetry.TrackException(pgEx, new Dictionary<string, string>
                {
                    { "Operation", "SaveSubscriptionAsync" },
                    { "ErrorType", "ForeignKeyViolation" },
                    { "UserId", subscription.UserId },
                    { "ConstraintName", pgEx.ConstraintName ?? "Unknown" }
                });

                throw new InvalidOperationException(
                    $"User '{subscription.UserId}' does not exist in the orguser table. " +
                    "Please ensure the user is created before creating subscriptions.",
                    pgEx);
            }
            catch (PostgresException pgEx) when (pgEx.SqlState == "23514") // Check constraint violation  
            {
                _logger.LogError(
                    pgEx,
                    "❌ Check constraint violation for SubscriptionId: {SubscriptionId}. " +
                    "ConstraintName: {ConstraintName}, Message: {Message}",
                    subscription.SubscriptionId,
                    pgEx.ConstraintName,
                    pgEx.MessageText);

                _telemetry.TrackException(pgEx, new Dictionary<string, string>
                {
                    { "Operation", "SaveSubscriptionAsync" },
                    { "ErrorType", "CheckConstraintViolation" },
                    { "ConstraintName", pgEx.ConstraintName ?? "Unknown" },
                    { "SubscriptionId", subscription.SubscriptionId }
                });

                throw new InvalidOperationException(
                    $"Subscription data violates database constraint '{pgEx.ConstraintName}'. " +
                    $"Details: {pgEx.MessageText}",
                    pgEx);
            }
            catch (PostgresException pgEx) when (pgEx.SqlState?.StartsWith("08") == true) // Connection errors  
            {
                _logger.LogError(
                    pgEx,
                    "❌ Database connection error while saving subscription. " +
                    "SQL State: {SqlState}, Error Code: {ErrorCode}, Message: {Message}",
                    pgEx.SqlState,
                    pgEx.ErrorCode,
                    pgEx.MessageText);

                _telemetry.TrackException(pgEx, new Dictionary<string, string>
                {
                    { "Operation", "SaveSubscriptionAsync" },
                    { "ErrorType", "ConnectionError" },
                    { "SqlState", pgEx.SqlState ?? "Unknown" },
                    { "UserId", subscription.UserId }
                });

                throw new InvalidOperationException(
                    "Database connection error. Please check database connectivity and retry.",
                    pgEx);
            }
            catch (PostgresException pgEx)
            {
                _logger.LogError(
                    pgEx,
                    "❌ PostgreSQL error while saving subscription. " +
                    "SQL State: {SqlState}, Error Code: {ErrorCode}, Message: {Message}, " +
                    "Severity: {Severity}, Detail: {Detail}",
                    pgEx.SqlState,
                    pgEx.ErrorCode,
                    pgEx.MessageText,
                    pgEx.Severity,
                    pgEx.Detail);

                _telemetry.TrackException(pgEx, new Dictionary<string, string>
                {
                    { "Operation", "SaveSubscriptionAsync" },
                    { "ErrorType", "PostgresException" },
                    { "SqlState", pgEx.SqlState ?? "Unknown" },
                    { "ErrorCode", pgEx.ErrorCode.ToString() },
                    { "Severity", pgEx.Severity ?? "Unknown" },
                    { "UserId", subscription.UserId }
                });

                throw new InvalidOperationException(
                    $"Database error while saving subscription: {pgEx.MessageText}. " +
                    $"SQL State: {pgEx.SqlState}",
                    pgEx);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(
                    "⚠️ Save operation was cancelled for SubscriptionId: {SubscriptionId}",
                    subscription.SubscriptionId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "❌ Unexpected error while saving subscription for UserId: {UserId}. " +
                    "Exception Type: {ExceptionType}",
                    subscription.UserId,
                    ex.GetType().Name);

                _telemetry.TrackException(ex, new Dictionary<string, string>
                {
                    { "Operation", "SaveSubscriptionAsync" },
                    { "ErrorType", "UnexpectedException" },
                    { "ExceptionType", ex.GetType().Name },
                    { "UserId", subscription.UserId }
                });

                throw;
            }
        }

        ///////////// <summary>  
        ///////////// Retrieves subscriptions expiring before the specified date (for renewal processing).  
        ///////////// </summary>  
        ///////////// <param name="expirationThreshold">UTC datetime threshold for expiration check.</param>  
        ///////////// <param name="cancellationToken">Cancellation token for request cancellation.</param>  
        ///////////// <returns>Collection of subscriptions expiring before the threshold.</returns>  
        ///////////// <exception cref="InvalidOperationException">Thrown when database query fails.</exception>  
        //////////public async Task<IEnumerable<MailSubscription>> GetSubscriptionsExpiringBeforeAsync(
        //////////    DateTime expirationThreshold,
        //////////    CancellationToken cancellationToken = default)
        //////////{
        //////////    // ✅ CRITICAL: Column names match existing database schema  
        //////////    const string sql = @"  
        //////////        SELECT   
        //////////            subscriptionid AS SubscriptionId,  
        //////////            userid AS UserId,  
        //////////            subscriptionstarttime AS SubscriptionStartTime,  
        //////////            subscriptionexpirationtime AS SubscriptionExpirationTime,  
        //////////            notificationurl AS NotificationUrl,  
        //////////            clientstate AS ClientState,  
        //////////            createddatetime AS CreatedDateTime,  
        //////////            lastreneweddatetime AS LastRenewedDateTime,  
        //////////            subscriptionchangetype AS SubscriptionChangeType,  
        //////////            applicationid AS ApplicationId  
        //////////        FROM mailsubscriptions  
        //////////        WHERE subscriptionexpirationtime < @ExpirationThreshold  
        //////////        ORDER BY subscriptionexpirationtime ASC;";

        //////////    try
        //////////    {
        //////////        using var conn = await _dbFactory.CreateConnectionAsync(cancellationToken);

        //////////        var command = new CommandDefinition(
        //////////            sql,
        //////////            new { ExpirationThreshold = expirationThreshold },
        //////////            cancellationToken: cancellationToken,
        //////////            commandTimeout: 30
        //////////        );

        //////////        var subscriptions = await conn.QueryAsync<MailSubscription>(command);
        //////////        var subscriptionList = subscriptions.AsList();

        //////////        _logger.LogInformation(
        //////////            "📊 Retrieved {Count} subscriptions expiring before {Threshold:O}",
        //////////            subscriptionList.Count,
        //////////            expirationThreshold);

        //////////        _telemetry.TrackEvent("MailSubscription_Query_ExpiringBefore", new Dictionary<string, string>
        //////////        {
        //////////            { "Threshold", expirationThreshold.ToString("O") },
        //////////            { "ResultCount", subscriptionList.Count.ToString() }
        //////////        });

        //////////        return subscriptionList;
        //////////    }
        //////////    catch (PostgresException pgEx)
        //////////    {
        //////////        _logger.LogError(
        //////////            pgEx,
        //////////            "❌ Database error while querying expiring subscriptions. " +
        //////////            "SQL State: {SqlState}, Message: {Message}",
        //////////            pgEx.SqlState,
        //////////            pgEx.MessageText);

        //////////        _telemetry.TrackException(pgEx, new Dictionary<string, string>
        //////////        {
        //////////            { "Operation", "GetSubscriptionsExpiringBeforeAsync" },
        //////////            { "Threshold", expirationThreshold.ToString("O") },
        //////////            { "SqlState", pgEx.SqlState ?? "Unknown" }
        //////////        });

        //////////        throw new InvalidOperationException(
        //////////            $"Database error while retrieving expiring subscriptions: {pgEx.MessageText}",
        //////////            pgEx);
        //////////    }
        //////////    catch (Exception ex)
        //////////    {
        //////////        _logger.LogError(
        //////////            ex,
        //////////            "❌ Error retrieving subscriptions expiring before {Threshold:O}",
        //////////            expirationThreshold);

        //////////        _telemetry.TrackException(ex, new Dictionary<string, string>
        //////////        {
        //////////            { "Operation", "GetSubscriptionsExpiringBeforeAsync" },
        //////////            { "Threshold", expirationThreshold.ToString("O") }
        //////////        });

        //////////        throw;
        //////////    }
        //////////}

        ///////////// <summary>  
        ///////////// Updates an existing subscription (used during renewal).  
        ///////////// </summary>  
        ///////////// <param name="subscription">Subscription entity with updated expiration time.</param>  
        ///////////// <param name="cancellationToken">Cancellation token for request cancellation.</param>  
        ///////////// <exception cref="ArgumentNullException">Thrown when subscription is null.</exception>  
        ///////////// <exception cref="InvalidOperationException">Thrown when subscription not found or update fails.</exception>  
        //////////public async Task UpdateSubscriptionAsync(
        //////////    MailSubscription subscription,
        //////////    CancellationToken cancellationToken = default)
        //////////{
        //////////    ArgumentNullException.ThrowIfNull(subscription, nameof(subscription));

        //////////    if (string.IsNullOrWhiteSpace(subscription.SubscriptionId))
        //////////        throw new ArgumentException("SubscriptionId cannot be empty.", nameof(subscription));

        //////////    // ✅ CRITICAL: Column names match existing database schema  
        //////////    const string sql = @"  
        //////////        UPDATE mailsubscriptions  
        //////////        SET   
        //////////            subscriptionexpirationtime = @SubscriptionExpirationTime,  
        //////////            lastreneweddatetime = @LastRenewedDateTime  
        //////////        WHERE subscriptionid = @SubscriptionId;";

        //////////    try
        //////////    {
        //////////        using var conn = await _dbFactory.CreateConnectionAsync(cancellationToken);

        //////////        var command = new CommandDefinition(
        //////////            sql,
        //////////            new
        //////////            {
        //////////                subscription.SubscriptionId,
        //////////                subscription.SubscriptionExpirationTime,
        //////////                subscription.LastRenewedDateTime
        //////////            },
        //////////            cancellationToken: cancellationToken,
        //////////            commandTimeout: 30
        //////////        );

        //////////        var rowsAffected = await conn.ExecuteAsync(command);

        //////////        if (rowsAffected == 0)
        //////////        {
        //////////            _logger.LogWarning(
        //////////                "⚠️ No subscription found with SubscriptionId: {SubscriptionId} for update.",
        //////////                subscription.SubscriptionId);

        //////////            throw new InvalidOperationException(
        //////////                $"Subscription '{subscription.SubscriptionId}' not found in database.");
        //////////        }

        //////////        _logger.LogInformation(
        //////////            "✅ Subscription updated successfully. " +
        //////////            "SubscriptionId: {SubscriptionId}, NewExpirationTime: {ExpirationTime:O}",
        //////////            subscription.SubscriptionId,
        //////////            subscription.SubscriptionExpirationTime);

        //////////        _telemetry.TrackEvent("MailSubscription_Updated", new Dictionary<string, string>
        //////////        {
        //////////            { "SubscriptionId", subscription.SubscriptionId },
        //////////            { "NewExpirationTime", subscription.SubscriptionExpirationTime.ToString("O") },
        //////////            { "RowsAffected", rowsAffected.ToString() }
        //////////        });
        //////////    }
        //////////    catch (PostgresException pgEx)
        //////////    {
        //////////        _logger.LogError(
        //////////            pgEx,
        //////////            "❌ Database error while updating subscription {SubscriptionId}. " +
        //////////            "SQL State: {SqlState}, Message: {Message}",
        //////////            subscription.SubscriptionId,
        //////////            pgEx.SqlState,
        //////////            pgEx.MessageText);

        //////////        _telemetry.TrackException(pgEx, new Dictionary<string, string>
        //////////        {
        //////////            { "Operation", "UpdateSubscriptionAsync" },
        //////////            { "SubscriptionId", subscription.SubscriptionId },
        //////////            { "SqlState", pgEx.SqlState ?? "Unknown" }
        //////////        });

        //////////        throw new InvalidOperationException(
        //////////            $"Database error while updating subscription: {pgEx.MessageText}",
        //////////            pgEx);
        //////////    }
        //////////    catch (Exception ex)
        //////////    {
        //////////        _logger.LogError(
        //////////            ex,
        //////////            "❌ Error updating subscription {SubscriptionId}",
        //////////            subscription.SubscriptionId);

        //////////        _telemetry.TrackException(ex, new Dictionary<string, string>
        //////////        {
        //////////            { "Operation", "UpdateSubscriptionAsync" },
        //////////            { "SubscriptionId", subscription.SubscriptionId }
        //////////        });

        //////////        throw;
        //////////    }
        //////////}

        ///////////// <summary>  
        ///////////// Retrieves a subscription by its ID.  
        ///////////// </summary>  
        ///////////// <param name="subscriptionId">The subscription ID to retrieve.</param>  
        ///////////// <param name="cancellationToken">Cancellation token for request cancellation.</param>  
        ///////////// <returns>The subscription if found; otherwise, null.</returns>  
        //////////public async Task<MailSubscription?> GetSubscriptionByIdAsync(
        //////////    string subscriptionId,
        //////////    CancellationToken cancellationToken = default)
        //////////{
        //////////    ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId, nameof(subscriptionId));

        //////////    // ✅ CRITICAL: Column names match existing database schema  
        //////////    const string sql = @"  
        //////////        SELECT   
        //////////            subscriptionid AS SubscriptionId,  
        //////////            userid AS UserId,  
        //////////            subscriptionstarttime AS SubscriptionStartTime,  
        //////////            subscriptionexpirationtime AS SubscriptionExpirationTime,  
        //////////            notificationurl AS NotificationUrl,  
        //////////            clientstate AS ClientState,  
        //////////            createddatetime AS CreatedDateTime,  
        //////////            lastreneweddatetime AS LastRenewedDateTime,  
        //////////            subscriptionchangetype AS SubscriptionChangeType,  
        //////////            applicationid AS ApplicationId  
        //////////        FROM mailsubscriptions  
        //////////        WHERE subscriptionid = @SubscriptionId;";

        //////////    try
        //////////    {
        //////////        using var conn = await _dbFactory.CreateConnectionAsync(cancellationToken);

        //////////        var command = new CommandDefinition(
        //////////            sql,
        //////////            new { SubscriptionId = subscriptionId },
        //////////            cancellationToken: cancellationToken,
        //////////            commandTimeout: 30
        //////////        );

        //////////        var subscription = await conn.QuerySingleOrDefaultAsync<MailSubscription>(command);

        //////////        if (subscription == null)
        //////////        {
        //////////            _logger.LogWarning(
        //////////                "⚠️ Subscription not found: {SubscriptionId}",
        //////////                subscriptionId);
        //////////        }
        //////////        else
        //////////        {
        //////////            _logger.LogDebug(
        //////////                "✅ Retrieved subscription: {SubscriptionId} for UserId: {UserId}",
        //////////                subscription.SubscriptionId,
        //////////                subscription.UserId);
        //////////        }

        //////////        return subscription;
        //////////    }
        //////////    catch (Exception ex)
        //////////    {
        //////////        _logger.LogError(
        //////////            ex,
        //////////            "❌ Error retrieving subscription {SubscriptionId}",
        //////////            subscriptionId);

        //////////        _telemetry.TrackException(ex, new Dictionary<string, string>
        //////////        {
        //////////            { "Operation", "GetSubscriptionByIdAsync" },
        //////////            { "SubscriptionId", subscriptionId }
        //////////        });

        //////////        throw;
        //////////    }
        //////////}
    }
}