using MailSubscriptionFunctionApp.Interfaces;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MailSubscriptionFunctionApp.Infrastructure
{
    /// <summary>  
    /// No-operation telemetry implementation for on-premises scenarios.  
    /// Logs telemetry calls locally without sending to external systems.  
    /// </summary>  
    public class NoOpTelemetry : ICustomTelemetry
    {
        private readonly ILogger<NoOpTelemetry> _logger;

        public NoOpTelemetry(ILogger<NoOpTelemetry> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />  
        public void TrackEvent(string eventName, IDictionary<string, string>? properties = null)
        {
            _logger.LogInformation("📊 Telemetry Event: {EventName} {@Properties}", eventName, properties);
        }

        /// <inheritdoc />  
        public void TrackException(Exception exception, IDictionary<string, string>? properties = null)
        {
            _logger.LogError(exception, "❌ Telemetry Exception {@Properties}", properties);
        }

        /// <inheritdoc />  
        public void TrackTrace(string message, SeverityLevel severityLevel, IDictionary<string, string>? properties = null)
        {
            var logLevel = severityLevel switch
            {
                SeverityLevel.Critical => LogLevel.Critical,
                SeverityLevel.Error => LogLevel.Error,
                SeverityLevel.Warning => LogLevel.Warning,
                SeverityLevel.Information => LogLevel.Information,
                SeverityLevel.Verbose => LogLevel.Debug,
                _ => LogLevel.Information
            };

            _logger.Log(logLevel, "📝 Telemetry Trace: {Message} {@Properties}", message, properties);
        }

        /// <inheritdoc />  
        public void TrackMetric(string name, double value, IDictionary<string, string>? properties = null)
        {
            _logger.LogInformation("📈 Telemetry Metric: {MetricName} = {Value} {@Properties}",
                name, value, properties);
        }

        /// <inheritdoc />  
        public void TrackDependency(
            string dependencyType,
            string dependencyName,
            DateTime startTime,
            TimeSpan duration,
            bool success)
        {
            _logger.LogInformation(
                "🔗 Telemetry Dependency: {DependencyType}/{DependencyName} | Duration: {Duration}ms | Success: {Success}",
                dependencyType,
                dependencyName,
                duration.TotalMilliseconds,
                success);
        }
    }
}
