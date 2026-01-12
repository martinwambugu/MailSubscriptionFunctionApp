using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using MailSubscriptionFunctionApp.Interfaces;
using System;
using System.Collections.Generic;

namespace MailSubscriptionFunctionApp.Infrastructure
{
    /// <summary>  
    /// Azure Application Insights implementation of telemetry abstraction.  
    /// </summary>  
    public class AzureAppInsightsTelemetry : ICustomTelemetry
    {
        private readonly TelemetryClient _telemetryClient;

        public AzureAppInsightsTelemetry(TelemetryClient telemetryClient)
        {
            _telemetryClient = telemetryClient ?? throw new ArgumentNullException(nameof(telemetryClient));
        }

        /// <inheritdoc />  
        public void TrackEvent(string eventName, IDictionary<string, string>? properties = null)
        {
            _telemetryClient.TrackEvent(eventName, properties);
        }

        /// <inheritdoc />  
        public void TrackException(Exception exception, IDictionary<string, string>? properties = null)
        {
            _telemetryClient.TrackException(exception, properties);
        }

        /// <inheritdoc />  
        public void TrackTrace(string message, SeverityLevel severityLevel, IDictionary<string, string>? properties = null)
        {
            _telemetryClient.TrackTrace(message, severityLevel, properties);
        }

        /// <inheritdoc />  
        public void TrackMetric(string name, double value, IDictionary<string, string>? properties = null)
        {
            var metric = _telemetryClient.GetMetric(name);
            metric.TrackValue(value);

            // If you need to attach custom properties, use TrackMetric directly:  
            if (properties != null && properties.Count > 0)
            {
                _telemetryClient.TrackMetric(name, value, properties);
            }
        }

        /// <inheritdoc />  
        public void TrackDependency(
            string dependencyType,
            string dependencyName,
            DateTime startTime,
            TimeSpan duration,
            bool success)
        {
            _telemetryClient.TrackDependency(
                dependencyTypeName: dependencyType,
                target: dependencyName,
                dependencyName: dependencyName,
                data: null,
                startTime: startTime,
                duration: duration,
                resultCode: success ? "200" : "500",
                success: success);
        }
    }
}