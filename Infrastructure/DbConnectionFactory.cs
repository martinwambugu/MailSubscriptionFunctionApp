using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using MailSubscriptionFunctionApp.Interfaces;

namespace MailSubscriptionFunctionApp.Infrastructure
{
    /// <summary>  
    /// PostgreSQL implementation of <see cref="IDbConnectionFactory"/> with connection pooling.  
    /// </summary>  
    /// <remarks>  
    /// <para>  
    /// This factory creates and opens PostgreSQL connections asynchronously with optimized pooling settings.  
    /// Connection strings are retrieved from configuration and should be stored in Azure Key Vault for production.  
    /// </para>  
    /// <para>  
    /// <b>Connection Pooling Configuration:</b>  
    /// <list type="bullet">  
    ///     <item>Min Pool Size: 5 connections</item>  
    ///     <item>Max Pool Size: 100 connections</item>  
    ///     <item>Connection Idle Lifetime: 5 minutes</item>  
    ///     <item>Keep-Alive: Enabled (30 seconds)</item>  
    /// </list>  
    /// </para>  
    /// </remarks>  
    public class DbConnectionFactory : IDbConnectionFactory
    {
        private readonly string _connectionString;
        private readonly ILogger<DbConnectionFactory> _logger;

        /// <summary>  
        /// Initializes a new instance of the <see cref="DbConnectionFactory"/> class.  
        /// </summary>  
        /// <param name="configuration">Application configuration containing the connection string.</param>  
        /// <param name="logger">Logger for diagnostics and telemetry.</param>  
        /// <exception cref="ArgumentNullException">Thrown when configuration or logger is null.</exception>  
        /// <exception cref="InvalidOperationException">Thrown when connection string is not configured.</exception>  
        public DbConnectionFactory(IConfiguration configuration, ILogger<DbConnectionFactory> logger)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(logger);

            _logger = logger;

            var connectionString = configuration.GetConnectionString("PostgreSqlConnection");

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                _logger.LogError("❌ Database connection string 'PostgreSqlConnection' is not configured.");
                throw new InvalidOperationException(
                    "Database connection string 'PostgreSqlConnection' is not configured. " +
                    "Please ensure it is set in appsettings.json, local.settings.json, or Azure Key Vault.");
            }

            // ✅ Configure connection pooling for optimal performance  
            var builder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                // Connection pooling settings  
                Pooling = true,
                MinPoolSize = 5,                    // Minimum connections in pool  
                MaxPoolSize = 100,                  // Maximum connections in pool  
                ConnectionIdleLifetime = 300,       // 5 minutes - close idle connections  
                ConnectionPruningInterval = 10,     // 10 seconds - check for idle connections  

                // Performance settings  
                CommandTimeout = 30,                // 30 seconds command timeout  
                Timeout = 15,                       // 15 seconds connection timeout  
                NoResetOnClose = true,              // ✅ Reuse connection state (performance boost)  
                MaxAutoPrepare = 20,                // ✅ Auto-prepare frequently used commands  
                AutoPrepareMinUsages = 2,           // ✅ Prepare after 2 usages  

                // Reliability settings  
                KeepAlive = 30,                     // 30 seconds keep-alive 

                // Security settings  
                SslMode = SslMode.Require,          // ✅ Require SSL/TLS encryption  
                TrustServerCertificate = false      // ✅ Validate server certificate (production-safe)  
            };

            _connectionString = builder.ToString();

            // ✅ Log sanitized connection string (mask password)  
            var safeConnString = $"Host={builder.Host};Port={builder.Port};Database={builder.Database};" +
                                $"Username={builder.Username};Password=****;Pooling={builder.Pooling};" +
                                $"MinPoolSize={builder.MinPoolSize};MaxPoolSize={builder.MaxPoolSize};" +
                                $"SslMode={builder.SslMode}";

            _logger.LogInformation("📡 PostgreSQL connection factory initialized: {ConnectionString}", safeConnString);
        }

        /// <inheritdoc/>  
        public async Task<IDbConnection> CreateConnectionAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("Creating new PostgreSQL connection from pool...");

            NpgsqlConnection? connection = null;

            try
            {
                connection = new NpgsqlConnection(_connectionString);

                // ✅ Attach state change event handler for diagnostics  
                connection.StateChange += OnConnectionStateChange;

                // ✅ Open connection asynchronously  
                await connection.OpenAsync(cancellationToken);

                _logger.LogDebug("✅ PostgreSQL connection opened successfully. State: {State}", connection.State);

                return connection;
            }
            catch (NpgsqlException npgEx)
            {
                _logger.LogError(npgEx,
                    "❌ Failed to open PostgreSQL connection. Error Code: {ErrorCode}, SQL State: {SqlState}",
                    npgEx.ErrorCode, npgEx.SqlState);

                connection?.Dispose();

                throw new InvalidOperationException(
                    $"Failed to connect to PostgreSQL database. Error: {npgEx.Message}",
                    npgEx);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("⚠️ Connection opening was cancelled.");
                connection?.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Unexpected error while opening database connection.");
                connection?.Dispose();
                throw;
            }
        }

        /// <summary>  
        /// Handles connection state changes for diagnostics.  
        /// </summary>  
        private void OnConnectionStateChange(object sender, StateChangeEventArgs e)
        {
            _logger.LogDebug(
                "🔌 PostgreSQL connection state changed: {OriginalState} → {CurrentState}",
                e.OriginalState, e.CurrentState);
        }
    }
}