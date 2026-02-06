# ADR-001: Use Azure Functions Isolated Worker Model

**Status**: Accepted

**Date**: 2025-12-15

**Authors**: Development Team

**Reviewers**: Architecture Team

---

## Context

### Problem Statement
The MailJournaling and Subscription Platform (MJSaPP) requires a scalable, serverless microservice architecture for managing Microsoft Graph mail subscriptions. The service needs to:
- Handle high-volume subscription creation requests
- Integrate with Microsoft Graph API
- Support both cloud (Azure) and on-premises telemetry
- Provide flexible authentication mechanisms
- Scale independently from other platform components

### Requirements
- **Scalability**: Auto-scale based on demand (0 to 1000+ requests/minute)
- **Isolation**: Separate runtime from Azure Functions host process
- **Flexibility**: Support .NET 8+ features and custom middleware
- **Cost-Efficiency**: Pay-per-execution model
- **Maintainability**: Easy to test, debug, and deploy
- **Security**: Support for custom authentication middleware

### Constraints
- Must integrate with existing Azure infrastructure
- Must support containerization for on-premises deployment
- Must provide OpenAPI documentation for external consumers
- Total cold start time < 3 seconds

---

## Decision

**Use Azure Functions v4 with .NET 8 Isolated Worker Model for the MailSubscriptionFunctionApp.**

The application will run as a separate process from the Azure Functions host, communicating via gRPC for performance. This enables:
1. Custom middleware pipeline for cross-cutting concerns
2. Full control over dependency injection
3. Use of latest .NET features without host version constraints
4. Independent scaling and resource allocation

---

## Consequences

### Positive
- **Better Performance**: Isolated process reduces memory pressure and improves CPU utilization
- **Modern .NET Features**: Access to .NET 8+ features (minimal APIs, performance improvements)
- **Custom Middleware**: Full ASP.NET Core-style middleware pipeline for authentication, logging, exception handling
- **Easier Testing**: Function code can be tested as standard .NET code without Functions runtime
- **Independent Upgrades**: Upgrade .NET version without waiting for Functions host updates
- **Container Support**: Natural fit for Docker/Kubernetes deployments
- **Better Debugging**: Standard .NET debugging tools work without special configuration
- **Reduced Cold Start**: Optimized startup compared to in-process model (~1.5s vs ~2.5s)

### Negative
- **Increased Complexity**: Two processes (host + worker) instead of one
- **Slightly Higher Memory**: ~50MB additional memory per instance
- **Middleware Required**: Authentication must be implemented via middleware (not attributes)
- **Learning Curve**: Different programming model from in-process functions
- **Limited Bindings**: Some trigger bindings not yet supported in isolated model
- **gRPC Dependency**: Communication between host and worker uses gRPC (adds network overhead)

### Risks
- **Breaking Changes**: Isolated worker is newer, may have more breaking changes between versions
  - *Mitigation*: Pin to specific NuGet package versions, comprehensive integration tests
- **Documentation Gaps**: Less community documentation vs in-process model
  - *Mitigation*: Maintain internal ADRs and code examples
- **Performance Edge Cases**: gRPC serialization overhead for large payloads
  - *Mitigation*: Monitor telemetry, optimize message sizes

---

## Alternatives Considered

### Alternative 1: Azure Functions In-Process Model
**Description**: Traditional in-process hosting where function code runs in the same process as the Functions host.

**Pros**:
- Simpler architecture (single process)
- Lower memory footprint (~30MB less per instance)
- More mature with extensive documentation
- Attribute-based authentication support
- Slightly faster for small payloads

**Cons**:
- Tied to .NET 6 (Functions host version dependency)
- No custom middleware support
- Limited control over DI container
- More complex cold start optimization
- Harder to unit test

**Reason for Rejection**:
Lack of middleware support blocks our dual authentication strategy (API Key + Azure AD). Unable to use .NET 8 features. Poor testability.

---

### Alternative 2: Azure Container Apps
**Description**: Deploy as containerized ASP.NET Core web API instead of Azure Functions.

**Pros**:
- Full ASP.NET Core feature set
- Better for long-running processes
- More control over scaling rules
- Native Kubernetes compatibility

**Cons**:
- Always-on pricing (not pay-per-execution)
- More complex infrastructure management
- No built-in triggers (HTTP, Timer, Queue)
- Manual scaling configuration required
- Higher minimum cost (~$50/month vs ~$0 idle)

**Reason for Rejection**:
Subscription creation is event-driven with variable load (0-500 req/hour). Functions' pay-per-execution model is more cost-effective. Built-in HTTP trigger simplifies implementation.

---

### Alternative 3: Azure Logic Apps
**Description**: Use Logic Apps for workflow-based subscription management.

**Pros**:
- Visual workflow designer
- Built-in connectors for Graph API
- No code required for basic scenarios
- Easy to maintain for non-developers

**Cons**:
- Limited error handling flexibility
- Expensive at scale ($0.000025 per action)
- Poor testability and version control
- Complex debugging
- No custom business logic support

**Reason for Rejection**:
Requires custom error handling, telemetry integration, and business logic that Logic Apps cannot accommodate. Not suitable for developer-maintained microservices.

---

## Implementation Notes

### For Developers

**Project Setup**:
```csharp
// Program.cs - Entry point
var host = new HostBuilder()
    .ConfigureFunctionsWebApplication((context, builder) =>
    {
        // Configure middleware pipeline
        builder.UseMiddleware<FunctionExceptionMiddleware>();
        builder.UseMiddleware<CorrelationIdMiddleware>();
        builder.UseMiddleware<ApiKeyAuthMiddleware>(); // Custom auth

        // Configure services
        builder.Services.AddScoped<IGraphSubscriptionClient, GraphSubscriptionService>();
    })
    .Build();

await host.RunAsync();
```

**Function Structure**:
```csharp
// Functions follow standard ASP.NET Core patterns
public class CreateMailSubscription
{
    private readonly IGraphSubscriptionClient _graphClient;
    private readonly ILogger<CreateMailSubscription> _logger;

    // Constructor injection
    public CreateMailSubscription(
        IGraphSubscriptionClient graphClient,
        ILogger<CreateMailSubscription> logger)
    {
        _graphClient = graphClient;
        _logger = logger;
    }

    [Function("CreateMailSubscription")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post")]
        HttpRequestData req,
        CancellationToken cancellationToken)
    {
        // Function implementation
        // Auth handled by middleware
    }
}
```

**Key Files**:
- [`Program.cs`](../../Program.cs) - Application bootstrapping, DI, middleware configuration
- [`host.json`](../../host.json) - Functions runtime configuration
- [`MailSubscriptionFunctionApp.csproj`](../../MailSubscriptionFunctionApp.csproj) - NuGet packages
- [`Middleware/`](../../Middleware/) - Custom middleware implementations

**Configuration** (`local.settings.json`):
```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "TELEMETRY_BACKEND": "azure",
    "Auth:Mode": "apikey"
  }
}
```

**NuGet Packages Required**:
```xml
<PackageReference Include="Microsoft.Azure.Functions.Worker" Version="2.51.0" />
<PackageReference Include="Microsoft.Azure.Functions.Worker.Sdk" Version="2.0.7" />
<PackageReference Include="Microsoft.Azure.Functions.Worker.Extensions.Http" Version="3.3.0" />
<PackageReference Include="Microsoft.Azure.Functions.Worker.Extensions.Http.AspNetCore" Version="2.1.0" />
```

### Testing Strategy

**Unit Tests**:
```csharp
[Fact]
public async Task CreateMailSubscription_ValidRequest_Returns201()
{
    // Arrange
    var mockGraphClient = new Mock<IGraphSubscriptionClient>();
    var mockRepo = new Mock<IMailSubscriptionRepository>();
    var function = new CreateMailSubscription(
        mockGraphClient.Object,
        mockRepo.Object,
        Mock.Of<ILogger<CreateMailSubscription>>(),
        Mock.Of<ICustomTelemetry>());

    var req = CreateMockHttpRequest("POST", @"{""userId"": ""test@example.com""}");

    // Act
    var response = await function.Run(req, CancellationToken.None);

    // Assert
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
}
```

**Integration Tests**:
```csharp
[Fact]
public async Task EndToEnd_SubscriptionCreation_SavesToDatabase()
{
    // Use TestServer from Microsoft.AspNetCore.TestHost
    var factory = new FunctionAppFactory<Program>();
    var client = factory.CreateClient();

    var response = await client.PostAsJsonAsync(
        "/api/subscriptions",
        new { userId = "test@example.com" });

    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
}
```

### Monitoring & Observability

**Key Metrics**:
- Function execution count (per function, per hour)
- Average execution duration (target: <500ms)
- Cold start frequency and duration (target: <2s)
- Error rate by function and error type
- Memory consumption (MB per instance)

**Application Insights Queries**:
```kql
// Function performance
requests
| where cloud_RoleName == "MailSubscriptionFunctionApp"
| summarize
    Count = count(),
    AvgDuration = avg(duration),
    P95Duration = percentile(duration, 95)
  by name, bin(timestamp, 1h)

// Cold starts
traces
| where message contains "Host lock lease acquired"
| summarize ColdStarts = count() by bin(timestamp, 1h)
```

**Health Check**:
```bash
# Verify function is running
curl https://<function-app>.azurewebsites.net/api/health
```

---

## Related Decisions

- [ADR-004: Middleware Pipeline Architecture](./ADR-004-middleware-pipeline.md) - Enabled by isolated worker
- [ADR-005: Dependency Injection and Service Lifetimes](./ADR-005-dependency-injection.md)
- [ADR-006: Dual Authentication Strategy](./ADR-006-dual-authentication-strategy.md) - Requires middleware

---

## References

- [Azure Functions Isolated Worker Guide](https://learn.microsoft.com/en-us/azure/azure-functions/dotnet-isolated-process-guide)
- [.NET Isolated vs In-Process Comparison](https://learn.microsoft.com/en-us/azure/azure-functions/dotnet-isolated-in-process-differences)
- [Azure Functions Best Practices](https://learn.microsoft.com/en-us/azure/azure-functions/functions-best-practices)
- [Performance Optimization Guide](https://learn.microsoft.com/en-us/azure/azure-functions/performance-reliability)

---

**Change Log**:
- 2025-12-15: Initial proposal by Development Team
- 2025-12-20: Accepted after architecture review
- 2026-01-10: Updated with .NET 8 migration notes
