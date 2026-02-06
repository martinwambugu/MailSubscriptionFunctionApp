# ADR-010: HTTP Client Configuration and Resilience

**Status**: Accepted

**Date**: 2025-12-19

---

## Context

Microsoft Graph API calls can fail due to:
- Transient network errors
- Rate limiting (HTTP 429)
- Service unavailability (HTTP 503)
- Timeout under load

Need resilient HTTP client that retries intelligently without exhausting resources.

---

## Decision

**Use `IHttpClientFactory` with Polly resilience policies:**

```csharp
builder.Services.AddHttpClient("GraphClient", client =>
{
    client.Timeout = TimeSpan.FromSeconds(100);
    client.DefaultRequestHeaders.Add("User-Agent", "MailSubscriptionApp/1.0");
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
    MaxConnectionsPerServer = 100,
    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
})
.AddStandardResilienceHandler(options =>
{
    // Retry with exponential backoff
    options.Retry.MaxRetryAttempts = 3;
    options.Retry.Delay = TimeSpan.FromSeconds(1);
    options.Retry.BackoffType = Polly.DelayBackoffType.Exponential;

    // Circuit breaker (open after 50% failure rate)
    options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
    options.CircuitBreaker.FailureRatio = 0.5;
    options.CircuitBreaker.MinimumThroughput = 10;
    options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);

    // Timeout per attempt
    options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);
    options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(100);
});
```

**Retry Pattern**:
- Attempt 1: Immediate
- Attempt 2: After 1 second
- Attempt 3: After 2 seconds (exponential)
- Attempt 4: After 4 seconds

**Circuit Breaker**:
- Monitors last 60 seconds of requests
- Opens if >50% fail and >10 requests made
- Stays open for 30 seconds
- Automatically half-opens to test recovery

---

## TLS Configuration

**Global Settings** (`Program.cs`):
```csharp
// Force TLS 1.2+ before any HTTP calls
ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
ServicePointManager.DefaultConnectionLimit = 100;
ServicePointManager.Expect100Continue = false;
```

**HttpClient Settings**:
```csharp
var handler = new SocketsHttpHandler
{
    SslOptions = new SslClientAuthenticationOptions
    {
        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
    },
    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
    MaxConnectionsPerServer = 20,
    ConnectTimeout = TimeSpan.FromSeconds(15)
};

var httpClient = new HttpClient(handler)
{
    Timeout = TimeSpan.FromSeconds(100),
    DefaultRequestVersion = HttpVersion.Version20,  // HTTP/2
    DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
};
```

---

## Connection Pooling

**Benefits**:
- Reuses TCP connections (avoids handshake overhead)
- Prevents port exhaustion (max 65,535 ports)
- Reduces latency (no connection setup per request)

**Configuration**:
```csharp
ServicePointManager.DefaultConnectionLimit = 100;  // Max concurrent connections

// Per HttpClient
MaxConnectionsPerServer = 20;  // Per destination
PooledConnectionLifetime = TimeSpan.FromMinutes(10);  // Recycle connections
PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2);  // Close idle connections
```

---

## GraphServiceClient Caching

**Problem**: Creating GraphServiceClient per request is expensive (token acquisition ~200ms).

**Solution**: Cache GraphServiceClient in MemoryCache for 50 minutes:
```csharp
private async Task<GraphServiceClient> GetOrCreateGraphClientAsync()
{
    const string cacheKey = "GraphServiceClient";

    if (_cache.TryGetValue(cacheKey, out GraphServiceClient cachedClient))
        return cachedClient;

    var graphClient = new GraphServiceClient(httpClient, credential, scopes);

    _cache.Set(cacheKey, graphClient, new MemoryCacheEntryOptions
    {
        AbsoluteExpiration = TimeSpan.FromMinutes(50),  // Token lifetime - buffer
        Priority = CacheItemPriority.High,
        PostEvictionCallback = (key, value, reason, state) =>
        {
            // Dispose HttpClient on eviction
            (value as IDisposable)?.Dispose();
        }
    });

    return graphClient;
}
```

---

## Monitoring

**Key Metrics**:
```kql
// HTTP dependency calls
dependencies
| where type == "Http"
| summarize
    Count = count(),
    AvgDuration = avg(duration),
    FailureRate = countif(success == false) * 100.0 / count()
  by name, resultCode

// Circuit breaker state
traces
| where message contains "Circuit breaker"
| project timestamp, message, severityLevel
```

---

## Related

- [ADR-001: Use Azure Functions Isolated Worker Model](./ADR-001-azure-functions-isolated-worker.md)
- [ADR-005: Dependency Injection and Service Lifetimes](./ADR-005-dependency-injection.md)

---

**References**:
- [HttpClient Best Practices](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/http/httpclient-guidelines)
- [Polly Resilience](https://www.pollydocs.org/)
- [Microsoft.Extensions.Http.Resilience](https://learn.microsoft.com/en-us/dotnet/core/resilience/)
