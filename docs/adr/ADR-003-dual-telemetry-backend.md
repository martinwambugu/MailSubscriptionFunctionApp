# ADR-003: Dual Telemetry Backend Support (Azure + On-Premises)

**Status**: Accepted

**Date**: 2025-12-16

**Authors**: Development Team, Operations Team

**Reviewers**: Architecture Team, Security Team

---

## Context

### Problem Statement
The MailJournaling and Subscription Platform (MJSaPP) operates in a hybrid cloud environment:
- **Cloud Deployment**: Azure Functions in Azure West Europe region
- **On-Premises Deployment**: Containerized deployment in CRDB Bank data centers

Both deployments require:
- Centralized logging and monitoring
- Performance metrics and tracing
- Error tracking and alerting
- Custom business event tracking

However, on-premises infrastructure cannot send telemetry to Azure Application Insights due to:
- Regulatory compliance (data sovereignty)
- Network security policies (no outbound HTTPS to Microsoft endpoints)
- Cost optimization (avoid Azure egress charges)

### Requirements
- **Dual Backend**: Support both Azure Application Insights and on-premises OpenTelemetry
- **Zero Code Changes**: Switch backends via configuration only
- **Consistent API**: Same telemetry interface regardless of backend
- **Performance**: <5ms overhead per telemetry call
- **Vendor Neutrality**: Avoid lock-in to Azure-specific APIs
- **Structured Logging**: JSON-formatted logs with correlation IDs
- **Metrics**: Performance counters, custom business metrics
- **Distributed Tracing**: Trace requests across microservices

### Constraints
- On-premises environment uses OpenTelemetry Collector (OTLP endpoint)
- Azure environment must use Application Insights for Azure Portal integration
- Cannot introduce breaking changes to existing logging code
- Must work with Azure Functions isolated worker model

---

## Decision

**Implement a dual telemetry architecture using:**

1. **OpenTelemetry** as the unified instrumentation layer
2. **Custom abstraction** (`ICustomTelemetry`) for business events
3. **Configuration-driven backends**: Switch between Azure and OTLP via environment variable

**Architecture**:
```
Application Code
    ↓
ICustomTelemetry (abstraction)
    ↓
┌─────────────────────┬────────────────────┐
│ AzureAppInsights    │   NoOpTelemetry    │
│ Implementation      │   (On-premises)    │
└─────────────────────┴────────────────────┘
    ↓                          ↓
Azure Monitor            OpenTelemetry
Application Insights     Collector (OTLP)
```

**Configuration** (`TELEMETRY_BACKEND` environment variable):
- `"azure"` → Azure Application Insights
- `"onprem"` → OpenTelemetry OTLP Exporter + NoOp custom telemetry

---

## Consequences

### Positive
- **Deployment Flexibility**: Single codebase for cloud and on-premises
- **Vendor Independence**: Can switch to Datadog, Grafana, Prometheus without code changes
- **Regulatory Compliance**: On-premises telemetry stays within network boundary
- **Cost Optimization**: Avoid Azure ingestion costs for on-premises (~$2.30/GB)
- **Consistent Developer Experience**: Same ILogger and ICustomTelemetry APIs
- **Future-Proof**: OpenTelemetry is CNCF standard (vendor-neutral)
- **Rich Ecosystem**: Works with Jaeger, Prometheus, Grafana, Elastic
- **Automatic Instrumentation**: HTTP, runtime, ASP.NET Core metrics included

### Negative
- **Configuration Complexity**: Two telemetry stacks to configure
- **Testing Overhead**: Must test both Azure and OTLP backends
- **Limited Feature Parity**: Application Insights has features OTLP doesn't (Live Metrics, Smart Detection)
- **Troubleshooting**: Different query languages (KQL vs PromQL/LogQL)
- **Dashboard Duplication**: Separate dashboards for Azure Portal vs Grafana
- **Learning Curve**: Team must learn both Azure Monitor and OpenTelemetry

### Risks
- **Configuration Drift**: Azure and on-premises environments diverge
  - *Mitigation*: Shared configuration templates, automated deployment validation
- **Missing Telemetry**: Accidental misconfiguration causes data loss
  - *Mitigation*: Health checks verify telemetry backend connectivity, alerts on silence
- **Performance Overhead**: Dual instrumentation adds latency
  - *Mitigation*: Benchmarking shows <3ms overhead, acceptable for 500ms function target
- **Hybrid Mode Conflict**: Running both backends simultaneously causes duplication
  - *Mitigation*: Disabled by default, only enable for migration scenarios

---

## Alternatives Considered

### Alternative 1: Azure Application Insights Only
**Description**: Use Application Insights for both cloud and on-premises, with Azure Private Link for on-premises connectivity.

**Pros**:
- Single backend simplifies operations
- Unified dashboards in Azure Portal
- Advanced features (Live Metrics, Smart Detection, Profiler)
- Native Azure Functions integration

**Cons**:
- Requires Azure ExpressRoute or VPN for on-premises (~$500/month)
- Data egress costs for on-premises telemetry (~$2.30/GB)
- Regulatory risk (data leaves on-premises boundary)
- Vendor lock-in to Azure ecosystem
- Single point of failure (Azure outage blocks telemetry)

**Reason for Rejection**:
Regulatory compliance prohibits sending on-premises telemetry to Azure. Cost model is prohibitive for high-volume logging (estimated $1,000/month). Vendor lock-in conflicts with multi-cloud strategy.

---

### Alternative 2: Serilog with Multiple Sinks
**Description**: Use Serilog with Azure Application Insights sink and file/Seq sink for on-premises.

**Pros**:
- Mature, well-documented library
- Rich sink ecosystem (50+ sinks)
- Structured logging with message templates
- Good performance (<2ms per log)

**Cons**:
- No distributed tracing support
- No automatic metrics collection
- Custom code for business event tracking
- Metrics require separate library (App Metrics)
- Limited Azure Functions integration
- No standard protocol for on-premises

**Reason for Rejection**:
Serilog is logging-only; doesn't cover metrics and tracing. OpenTelemetry provides unified observability (logs, metrics, traces). Serilog's Azure sink is deprecated in favor of OpenTelemetry.

---

### Alternative 3: Dual Implementation (Separate Code Paths)
**Description**: Write separate telemetry code for Azure (`ILogger<T>`) and on-premises (custom logging).

**Pros**:
- Full control over each backend
- Optimize for each platform's strengths
- No abstraction overhead

**Cons**:
- Code duplication and maintenance burden
- Inconsistent telemetry between environments
- High risk of divergence
- Testing complexity (2x test matrix)
- Violates DRY principle

**Reason for Rejection**:
Maintenance nightmare. Code duplication leads to bugs and inconsistencies. Abstraction overhead is negligible (<1ms).

---

## Implementation Notes

### For Developers

**Program.cs Configuration**:
```csharp
// Load telemetry backend configuration
string telemetryBackend = configuration["TELEMETRY_BACKEND"]?.ToLowerInvariant() ?? "azure";

// Configure OpenTelemetry Logging
builder.Services.AddLogging(logging =>
{
    logging.AddOpenTelemetry(options =>
    {
        options.IncludeScopes = true;
        options.IncludeFormattedMessage = true;

        if (telemetryBackend == "azure")
        {
            options.AddAzureMonitorLogExporter(o =>
            {
                o.ConnectionString = configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
            });
        }
        else if (telemetryBackend == "onprem")
        {
            options.AddOtlpExporter(otlp =>
            {
                otlp.Endpoint = new Uri(configuration["OTLP_ENDPOINT"] ?? "http://localhost:4317");
            });
        }
    });
});

// Configure OpenTelemetry Metrics
var otel = builder.Services.AddOpenTelemetry();
otel.WithMetrics(metrics =>
{
    metrics
        .AddRuntimeInstrumentation()
        .AddHttpClientInstrumentation()
        .AddAspNetCoreInstrumentation();

    if (telemetryBackend == "azure")
    {
        metrics.AddAzureMonitorMetricExporter(o =>
        {
            o.ConnectionString = configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        });
    }
    else if (telemetryBackend == "onprem")
    {
        metrics.AddOtlpExporter(otlp =>
        {
            otlp.Endpoint = new Uri(configuration["OTLP_ENDPOINT"] ?? "http://localhost:4317");
        });

        // Optional: Prometheus scrape endpoint
        if (bool.Parse(configuration["PROMETHEUS_ENABLED"] ?? "false"))
        {
            metrics.AddPrometheusHttpListener(options =>
            {
                options.UriPrefixes = new[] { "http://+:9464/" };
            });
        }
    }
});

// Configure OpenTelemetry Tracing
otel.WithTracing(traces =>
{
    traces
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation();

    if (telemetryBackend == "azure")
    {
        traces.AddAzureMonitorTraceExporter(o =>
        {
            o.ConnectionString = configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        });
    }
    else if (telemetryBackend == "onprem")
    {
        traces.AddOtlpExporter(otlp =>
        {
            otlp.Endpoint = new Uri(configuration["OTLP_ENDPOINT"] ?? "http://localhost:4317");
        });
    }
});

// Custom Telemetry Abstraction
if (telemetryBackend == "azure")
{
    builder.Services.AddApplicationInsightsTelemetryWorkerService();
    builder.Services.AddSingleton<ICustomTelemetry, AzureAppInsightsTelemetry>();
}
else
{
    builder.Services.AddSingleton<ICustomTelemetry, NoOpTelemetry>();
}
```

**Custom Telemetry Interface**:
```csharp
// Interfaces/ICustomTelemetry.cs
public interface ICustomTelemetry
{
    void TrackEvent(string eventName, IDictionary<string, string>? properties = null);
    void TrackException(Exception exception, IDictionary<string, string>? properties = null);
    void TrackMetric(string name, double value, IDictionary<string, string>? properties = null);
    void TrackDependency(string type, string name, DateTimeOffset startTime, TimeSpan duration, bool success);
}
```

**Azure Implementation**:
```csharp
// Infrastructure/AzureAppInsightsTelemetry.cs
public class AzureAppInsightsTelemetry : ICustomTelemetry
{
    private readonly TelemetryClient _telemetryClient;

    public AzureAppInsightsTelemetry(TelemetryClient telemetryClient)
    {
        _telemetryClient = telemetryClient;
    }

    public void TrackEvent(string eventName, IDictionary<string, string>? properties = null)
    {
        _telemetryClient.TrackEvent(eventName, properties);
    }

    // ... other methods
}
```

**On-Premises Implementation**:
```csharp
// Infrastructure/NoOpTelemetry.cs
public class NoOpTelemetry : ICustomTelemetry
{
    private readonly ILogger<NoOpTelemetry> _logger;

    public NoOpTelemetry(ILogger<NoOpTelemetry> logger)
    {
        _logger = logger;
    }

    public void TrackEvent(string eventName, IDictionary<string, string>? properties = null)
    {
        // Log to OpenTelemetry via ILogger
        _logger.LogInformation("Event: {EventName}, Properties: {@Properties}",
            eventName, properties);
    }

    // ... other methods log instead of tracking
}
```

**Usage in Application Code**:
```csharp
public class CreateMailSubscription
{
    private readonly ILogger<CreateMailSubscription> _logger;
    private readonly ICustomTelemetry _telemetry;

    public async Task<HttpResponseData> Run(HttpRequestData req)
    {
        _logger.LogInformation("Creating subscription for user {UserId}", userId);

        _telemetry.TrackEvent("MailSubscription_Create_Start", new Dictionary<string, string>
        {
            { "UserId", userId },
            { "CorrelationId", correlationId }
        });

        // ... implementation
    }
}
```

**Key Files**:
- [`Program.cs`](../../Program.cs) - Telemetry configuration (lines 72-166)
- [`Infrastructure/AzureAppInsightsTelemetry.cs`](../../Infrastructure/AzureAppInsightsTelemetry.cs) - Azure implementation
- [`Infrastructure/NoOpTelemetry.cs`](../../Infrastructure/NoOpTelemetry.cs) - On-premises implementation
- [`Interfaces/ICustomTelemetry.cs`](../../Interfaces/ICustomTelemetry.cs) - Abstraction

**Configuration**:

*Azure Deployment* (`appsettings.json`):
```json
{
  "TELEMETRY_BACKEND": "azure",
  "APPLICATIONINSIGHTS_CONNECTION_STRING": "InstrumentationKey=xxx;IngestionEndpoint=https://westeurope-5.in.applicationinsights.azure.com/"
}
```

*On-Premises Deployment* (`docker-compose.yml`):
```yaml
environment:
  - TELEMETRY_BACKEND=onprem
  - OTLP_ENDPOINT=http://otel-collector:4317
  - PROMETHEUS_ENABLED=true
```

**NuGet Packages**:
```xml
<!-- OpenTelemetry Core -->
<PackageReference Include="OpenTelemetry" Version="1.14.0" />
<PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="1.14.0" />

<!-- Instrumentation -->
<PackageReference Include="OpenTelemetry.Instrumentation.AspNetCore" Version="1.14.0" />
<PackageReference Include="OpenTelemetry.Instrumentation.Http" Version="1.14.0" />
<PackageReference Include="OpenTelemetry.Instrumentation.Runtime" Version="1.14.0" />

<!-- Azure Exporters -->
<PackageReference Include="Azure.Monitor.OpenTelemetry.Exporter" Version="1.5.0" />
<PackageReference Include="Microsoft.ApplicationInsights.WorkerService" Version="2.23.0" />

<!-- OTLP Exporters -->
<PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.14.0" />
<PackageReference Include="OpenTelemetry.Exporter.Prometheus.HttpListener" Version="1.14.0-beta.1" />
```

### On-Premises Infrastructure

**OpenTelemetry Collector Configuration** (`otel-collector-config.yaml`):
```yaml
receivers:
  otlp:
    protocols:
      grpc:
        endpoint: 0.0.0.0:4317
      http:
        endpoint: 0.0.0.0:4318

processors:
  batch:
    timeout: 10s
    send_batch_size: 1024

exporters:
  logging:
    loglevel: debug

  prometheus:
    endpoint: "0.0.0.0:8889"

  loki:
    endpoint: http://loki:3100/loki/api/v1/push

  jaeger:
    endpoint: jaeger:14250
    tls:
      insecure: true

service:
  pipelines:
    logs:
      receivers: [otlp]
      processors: [batch]
      exporters: [loki, logging]

    metrics:
      receivers: [otlp]
      processors: [batch]
      exporters: [prometheus, logging]

    traces:
      receivers: [otlp]
      processors: [batch]
      exporters: [jaeger, logging]
```

**Docker Compose** (`docker-compose.yml`):
```yaml
version: '3.8'

services:
  mailsubscription-function:
    image: mailsubscriptionapp:latest
    environment:
      - TELEMETRY_BACKEND=onprem
      - OTLP_ENDPOINT=http://otel-collector:4317
      - PROMETHEUS_ENABLED=true
    ports:
      - "80:80"
      - "9464:9464"  # Prometheus metrics

  otel-collector:
    image: otel/opentelemetry-collector:0.91.0
    volumes:
      - ./otel-collector-config.yaml:/etc/otel-collector-config.yaml
    command: ["--config=/etc/otel-collector-config.yaml"]
    ports:
      - "4317:4317"  # OTLP gRPC
      - "4318:4318"  # OTLP HTTP
      - "8889:8889"  # Prometheus

  loki:
    image: grafana/loki:2.9.3
    ports:
      - "3100:3100"

  grafana:
    image: grafana/grafana:10.2.3
    ports:
      - "3000:3000"
    environment:
      - GF_AUTH_ANONYMOUS_ENABLED=true

  jaeger:
    image: jaegertracing/all-in-one:1.52
    ports:
      - "16686:16686"  # UI
      - "14250:14250"  # gRPC
```

### Testing Strategy

**Verify Azure Backend**:
```csharp
[Fact]
public async Task Telemetry_AzureBackend_SendsToApplicationInsights()
{
    // Arrange
    var config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string>
        {
            { "TELEMETRY_BACKEND", "azure" },
            { "APPLICATIONINSIGHTS_CONNECTION_STRING", "InstrumentationKey=test" }
        })
        .Build();

    var telemetry = new AzureAppInsightsTelemetry(new TelemetryClient());

    // Act
    telemetry.TrackEvent("TestEvent", new Dictionary<string, string>
    {
        { "TestProperty", "TestValue" }
    });

    // Assert
    // Verify via Application Insights Query API
}
```

**Verify OTLP Backend**:
```bash
# Send test request
curl -X POST http://localhost:4318/v1/logs \
  -H "Content-Type: application/json" \
  -d '{"resourceLogs":[{"scopeLogs":[{"logRecords":[{"body":{"stringValue":"test"}}]}]}]}'

# Verify in Grafana Loki
curl http://localhost:3100/loki/api/v1/query_range \
  --data-urlencode 'query={job="mailsubscription"}' \
  --data-urlencode 'limit=10'
```

### Monitoring & Observability

**Health Check**:
```csharp
[Function("TelemetryHealthCheck")]
public async Task<HttpResponseData> CheckTelemetry(
    [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health/telemetry")]
    HttpRequestData req)
{
    var backend = _config["TELEMETRY_BACKEND"];
    var healthy = true;

    if (backend == "azure")
    {
        // Test Application Insights connectivity
        try
        {
            _telemetryClient.TrackEvent("HealthCheck");
            _telemetryClient.Flush();
            await Task.Delay(1000); // Wait for flush
        }
        catch
        {
            healthy = false;
        }
    }
    else if (backend == "onprem")
    {
        // Test OTLP endpoint connectivity
        var otlpEndpoint = _config["OTLP_ENDPOINT"];
        using var httpClient = new HttpClient();
        try
        {
            var response = await httpClient.GetAsync($"{otlpEndpoint}/health");
            healthy = response.IsSuccessStatusCode;
        }
        catch
        {
            healthy = false;
        }
    }

    var statusCode = healthy ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable;
    var response = req.CreateResponse(statusCode);
    await response.WriteAsJsonAsync(new
    {
        backend = backend,
        healthy = healthy,
        timestamp = DateTime.UtcNow
    });
    return response;
}
```

**Azure Monitor Query** (KQL):
```kql
// Custom events
customEvents
| where name == "MailSubscription_Create_Start"
| summarize Count = count() by bin(timestamp, 5m)
| render timechart

// Performance
requests
| where name == "CreateMailSubscription"
| summarize
    AvgDuration = avg(duration),
    P95Duration = percentile(duration, 95)
  by bin(timestamp, 1h)
```

**Grafana Query** (PromQL for metrics):
```promql
# Request rate
rate(http_server_requests_total{job="mailsubscription"}[5m])

# Request duration P95
histogram_quantile(0.95,
  rate(http_server_request_duration_seconds_bucket[5m])
)
```

**Grafana Query** (LogQL for logs):
```logql
{job="mailsubscription"} |= "MailSubscription_Create_Start" | json
```

---

## Related Decisions

- [ADR-001: Use Azure Functions Isolated Worker Model](./ADR-001-azure-functions-isolated-worker.md)
- [ADR-008: Error Handling Strategy](./ADR-008-error-handling-strategy.md)

---

## References

- [OpenTelemetry .NET Documentation](https://opentelemetry.io/docs/instrumentation/net/)
- [Azure Monitor OpenTelemetry Exporter](https://learn.microsoft.com/en-us/azure/azure-monitor/app/opentelemetry-enable)
- [OpenTelemetry Collector](https://opentelemetry.io/docs/collector/)
- [Grafana Observability Stack](https://grafana.com/docs/)
- [Application Insights Overview](https://learn.microsoft.com/en-us/azure/azure-monitor/app/app-insights-overview)

---

**Change Log**:
- 2025-12-16: Initial proposal by Development Team
- 2025-12-20: Operations Team review - added OTLP collector config
- 2025-12-22: Accepted after successful on-premises proof of concept
- 2026-01-15: Updated with Grafana dashboard examples
