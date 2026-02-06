# ADR-005: Dependency Injection and Service Lifetimes

**Status**: Accepted

**Date**: 2025-12-17

**Authors**: Development Team

**Reviewers**: Architecture Team

---

## Context

### Problem Statement
The application requires a consistent strategy for managing service lifetimes, dependency injection, and resource disposal. Incorrect lifetimes can cause:
- Memory leaks (singleton services holding references to scoped resources)
- Concurrency bugs (shared state in scoped services)
- Performance issues (creating expensive singletons per request)
- Database connection exhaustion

### Requirements
- **Testability**: Services must be mockable via interfaces
- **Resource Management**: Proper disposal of connections, HTTP clients
- **Thread Safety**: Singleton services must be thread-safe
- **Performance**: Minimize object allocations per request
- **Azure Functions Compatibility**: Work with isolated worker model

---

## Decision

**Use Microsoft.Extensions.DependencyInjection with the following lifetime patterns:**

| Service Type | Lifetime | Rationale |
|-------------|----------|-----------|
| **Infrastructure** | | |
| `IDbConnectionFactory` | Scoped | Creates connections per request, proper disposal |
| `ICustomTelemetry` | Singleton | Thread-safe, stateless telemetry client |
| `IMemoryCache` | Singleton | Shared cache across all requests |
| `HttpClient` (via factory) | Managed | Pooled by `IHttpClientFactory` |
| **Domain Services** | | |
| `IGraphSubscriptionClient` | Scoped | Uses HttpClient, may hold request state |
| `IMailSubscriptionRepository` | Scoped | Uses database connections |
| **Middleware** | Singleton | Stateless, no per-request data |
| **Functions** | Transient | Default for Azure Functions |

**Registration** (`Program.cs`):
```csharp
var host = new HostBuilder()
    .ConfigureFunctionsWebApplication((context, builder) =>
    {
        // Singleton: Stateless, thread-safe services
        builder.Services.AddSingleton<ICustomTelemetry, AzureAppInsightsTelemetry>();
        builder.Services.AddSingleton<TelemetryClient>();
        builder.Services.AddMemoryCache(); // Singleton

        // Scoped: Per-request services with disposal
        builder.Services.AddScoped<IDbConnectionFactory, DbConnectionFactory>();
        builder.Services.AddScoped<IGraphSubscriptionClient, GraphSubscriptionService>();
        builder.Services.AddScoped<IMailSubscriptionRepository, MailSubscriptionService>();

        // HttpClient via factory (managed lifecycle)
        builder.Services.AddHttpClient("GraphClient", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(100);
        })
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { ... })
        .AddStandardResilienceHandler();
    })
    .Build();
```

---

## Consequences

### Positive
- **Predictable Behavior**: Consistent lifetime patterns across codebase
- **Resource Safety**: Scoped services disposed at end of request
- **Memory Efficient**: Singletons shared across requests
- **Testable**: All dependencies injected via interfaces
- **Connection Pooling**: Scoped DbConnectionFactory works with Npgsql pooling

### Negative
- **Learning Curve**: Team must understand lifetime implications
- **Debugging Complexity**: Lifetime issues can be subtle (e.g., singleton → scoped dependency)
- **Memory Usage**: Scoped services create new instances per request

### Risks
- **Captive Dependencies**: Singleton holding scoped service reference causes memory leak
  - *Mitigation*: Build-time validation via `ValidateScopes()` in development
- **Thread Safety Bugs**: Mutable state in singleton causes race conditions
  - *Mitigation*: Code review checklist, static analysis

---

## Implementation Notes

### Service Registration Patterns

**Pattern 1: Simple Interface → Implementation**
```csharp
builder.Services.AddScoped<IMailSubscriptionRepository, MailSubscriptionService>();
```

**Pattern 2: Factory Pattern for Complex Construction**
```csharp
builder.Services.AddScoped<IGraphSubscriptionClient>(provider =>
{
    var config = provider.GetRequiredService<IConfiguration>();
    var logger = provider.GetRequiredService<ILogger<GraphSubscriptionService>>();
    var telemetry = provider.GetRequiredService<ICustomTelemetry>();
    var cache = provider.GetRequiredService<IMemoryCache>();

    return new GraphSubscriptionService(config, logger, telemetry, cache);
});
```

**Pattern 3: HttpClient with Named Configuration**
```csharp
builder.Services.AddHttpClient("GraphClient", client =>
{
    client.Timeout = TimeSpan.FromSeconds(100);
    client.DefaultRequestHeaders.Add("User-Agent", "MailSubscriptionApp/1.0");
})
.AddStandardResilienceHandler(options =>
{
    options.Retry.MaxRetryAttempts = 3;
});

// Usage in service:
public class GraphSubscriptionService
{
    private readonly HttpClient _httpClient;

    public GraphSubscriptionService(IHttpClientFactory httpClientFactory)
    {
        _httpClient = httpClientFactory.CreateClient("GraphClient");
    }
}
```

### Lifetime Validation

**Development Environment** (`Program.cs`):
```csharp
var host = new HostBuilder()
    .ConfigureFunctionsWebApplication(...)
    .ConfigureServices((context, services) =>
    {
        // Validate service lifetimes in development
        if (context.HostingEnvironment.IsDevelopment())
        {
            services.AddOptions<ServiceProviderOptions>()
                .Configure(options =>
                {
                    options.ValidateScopes = true; // Throws if singleton → scoped
                    options.ValidateOnBuild = true; // Validates at startup
                });
        }
    })
    .Build();
```

### Testing with DI

**Unit Test Example**:
```csharp
public class CreateMailSubscriptionTests
{
    [Fact]
    public async Task Run_ValidRequest_Returns201()
    {
        // Arrange: Create test service provider
        var services = new ServiceCollection();

        // Register mocks
        services.AddScoped<IGraphSubscriptionClient>(_ =>
            Mock.Of<IGraphSubscriptionClient>(m =>
                m.CreateMailSubscriptionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())
                == Task.FromResult(new MailSubscription { SubscriptionId = "test-123" })));

        services.AddScoped<IMailSubscriptionRepository, FakeMailSubscriptionRepository>();
        services.AddSingleton<ILogger<CreateMailSubscription>>(_ =>
            Mock.Of<ILogger<CreateMailSubscription>>());
        services.AddSingleton<ICustomTelemetry, NoOpTelemetry>();

        var provider = services.BuildServiceProvider();

        // Act: Resolve function with dependencies
        var function = ActivatorUtilities.CreateInstance<CreateMailSubscription>(provider);
        var response = await function.Run(CreateMockRequest(), CancellationToken.None);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
```

### Common Anti-Patterns to Avoid

**❌ Anti-Pattern 1: Singleton → Scoped Dependency (Captive Dependency)**
```csharp
// BAD: Singleton service depends on scoped service
builder.Services.AddSingleton<MySingleton>(provider =>
{
    var scoped = provider.GetRequiredService<IDbConnectionFactory>(); // WRONG!
    return new MySingleton(scoped);
});
```

**✅ Correct Approach**: Use factory pattern or make service scoped
```csharp
builder.Services.AddScoped<MySingleton>();
// OR
builder.Services.AddSingleton<MySingleton>(provider =>
{
    // Inject IServiceProvider, resolve scoped services per operation
    return new MySingleton(provider);
});
```

**❌ Anti-Pattern 2: Disposing Singleton Service**
```csharp
// BAD: Singleton with IDisposable
public class MySingleton : IDisposable
{
    private readonly HttpClient _httpClient = new HttpClient(); // Memory leak!

    public void Dispose() => _httpClient.Dispose(); // Never called!
}
```

**✅ Correct Approach**: Use IHttpClientFactory or register as Scoped
```csharp
public class MyService
{
    private readonly HttpClient _httpClient;

    public MyService(IHttpClientFactory httpClientFactory)
    {
        _httpClient = httpClientFactory.CreateClient("MyClient");
    }
}
```

---

## Related Decisions

- [ADR-001: Use Azure Functions Isolated Worker Model](./ADR-001-azure-functions-isolated-worker.md)
- [ADR-002: PostgreSQL for Subscription Persistence](./ADR-002-postgresql-persistence.md)
- [ADR-011: Database Connection Pooling Strategy](./ADR-011-connection-pooling.md)

---

## References

- [Dependency Injection in .NET](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection)
- [Service Lifetimes](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection#service-lifetimes)
- [Captive Dependencies](https://blog.ploeh.dk/2014/06/02/captive-dependency/)

---

**Change Log**:
- 2025-12-17: Initial proposal
- 2025-12-18: Accepted
