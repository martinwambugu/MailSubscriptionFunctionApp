# ADR-004: Middleware Pipeline Architecture

**Status**: Accepted

**Date**: 2025-12-17

**Authors**: Development Team

**Reviewers**: Architecture Team

---

## Context

### Problem Statement
Azure Functions v4 with isolated worker model requires cross-cutting concerns to be implemented as middleware rather than attributes. The application needs:
- Authentication (API Key or Azure AD)
- Exception handling with standardized error responses
- Request correlation for distributed tracing
- Operation naming for telemetry
- CORS handling

Traditional .NET attributes (`[Authorize]`, `[ValidateAntiForgeryToken]`) don't work in isolated worker model.

### Requirements
- **Composable**: Middleware should be independently testable and reusable
- **Order-Dependent**: Authentication must run before authorization, exception handling must be outermost
- **Configurable**: Enable/disable middleware via configuration
- **Performance**: <10ms total overhead for entire pipeline
- **Logging**: Each middleware logs entry/exit for troubleshooting

---

## Decision

**Implement an ASP.NET Core-style middleware pipeline using `IFunctionsWorkerMiddleware` with the following order:**

```
Request
  ↓
1. FunctionExceptionMiddleware (catches all unhandled exceptions)
  ↓
2. CorrelationIdMiddleware (adds x-correlation-id header)
  ↓
3. OperationNameMiddleware (logs function name for telemetry)
  ↓
4. [ApiKeyAuthMiddleware OR AzureAdAuthMiddleware] (based on Auth:Mode config)
  ↓
Function Execution
  ↓
Response
```

**Registration Pattern** (`Program.cs`):
```csharp
// MiddlewareExtensions.cs
public static class MiddlewareExtensions
{
    public static IServiceCollection AddCustomMiddlewares(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<FunctionExceptionMiddleware>();
        services.AddSingleton<CorrelationIdMiddleware>();
        services.AddSingleton<OperationNameMiddleware>();

        var authMode = configuration["Auth:Mode"];
        if (authMode?.Equals("apikey", StringComparison.OrdinalIgnoreCase) == true)
        {
            services.AddSingleton<ApiKeyAuthMiddleware>();
        }
        else if (authMode?.Equals("azuread", StringComparison.OrdinalIgnoreCase) == true)
        {
            services.AddSingleton<AzureAdAuthMiddleware>();
        }

        return services;
    }

    public static IFunctionsWorkerApplicationBuilder UseCustomMiddlewares(
        this IFunctionsWorkerApplicationBuilder builder,
        IConfiguration configuration)
    {
        // Order matters! Exception handler must be first
        builder.UseMiddleware<FunctionExceptionMiddleware>();
        builder.UseMiddleware<CorrelationIdMiddleware>();
        builder.UseMiddleware<OperationNameMiddleware>();

        var authMode = configuration["Auth:Mode"];
        if (authMode?.Equals("apikey", StringComparison.OrdinalIgnoreCase) == true)
        {
            builder.UseMiddleware<ApiKeyAuthMiddleware>();
        }
        else if (authMode?.Equals("azuread", StringComparison.OrdinalIgnoreCase) == true)
        {
            builder.UseMiddleware<AzureAdAuthMiddleware>();
        }

        return builder;
    }
}
```

---

## Consequences

### Positive
- **Separation of Concerns**: Each middleware has single responsibility
- **Reusability**: Middleware can be reused across multiple functions
- **Testability**: Each middleware can be unit tested independently
- **Flexibility**: Easy to add/remove middleware without changing function code
- **Consistent Error Handling**: All exceptions caught and formatted uniformly
- **Distributed Tracing**: Correlation IDs flow through entire request

### Negative
- **Performance Overhead**: Each middleware adds ~1-2ms latency (5-10ms total)
- **Complexity**: Middleware order is critical but not enforced at compile time
- **Debugging**: Stack traces can be deeper with multiple middleware layers
- **Configuration Errors**: Misconfigured middleware can silently fail

### Risks
- **Middleware Order Bug**: Wrong order causes auth bypass or broken error handling
  - *Mitigation*: Integration tests verify order, code review checklist
- **Exception Swallowing**: Middleware catches exception but doesn't rethrow
  - *Mitigation*: All middleware must call `await next(context)` in try block

---

## Alternatives Considered

### Alternative 1: Function-Level Authorization Checks
**Description**: Check authentication in each function's code instead of middleware.

**Reason for Rejection**: Code duplication, inconsistent error responses, harder to test, violates DRY principle.

### Alternative 2: Azure API Management
**Description**: Use APIM for authentication, rate limiting, CORS.

**Reason for Rejection**: Additional cost (~$150/month), added latency (50-100ms), doesn't work for on-premises deployment.

---

## Implementation Notes

### Middleware Template

```csharp
public class ExampleMiddleware : IFunctionsWorkerMiddleware
{
    private readonly ILogger<ExampleMiddleware> _logger;

    public ExampleMiddleware(ILogger<ExampleMiddleware> logger)
    {
        _logger = logger;
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        // Get HTTP request if available
        var req = await context.GetHttpRequestDataAsync();
        if (req == null)
        {
            // Not an HTTP trigger, skip
            await next(context);
            return;
        }

        // Pre-processing logic
        _logger.LogDebug("ExampleMiddleware: Before");

        try
        {
            // Call next middleware
            await next(context);
        }
        finally
        {
            // Post-processing logic (always runs)
            _logger.LogDebug("ExampleMiddleware: After");
        }
    }
}
```

### Middleware Implementations

**1. FunctionExceptionMiddleware** ([Source](../../Middleware/FunctionExceptionMiddleware.cs))
```csharp
// Catches all unhandled exceptions, returns 500 with standardized error
public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
{
    try
    {
        await next(context);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Unhandled exception in {FunctionName}",
            context.FunctionDefinition.Name);

        var req = await context.GetHttpRequestDataAsync();
        if (req != null)
        {
            var response = req.CreateResponse(HttpStatusCode.InternalServerError);
            await response.WriteAsJsonAsync(new
            {
                error = "An unexpected error occurred.",
                correlationId = req.Headers.GetValues("x-correlation-id").FirstOrDefault()
            });
            context.GetInvocationResult().Value = response;
        }
    }
}
```

**2. CorrelationIdMiddleware** ([Source](../../Middleware/CorrelationIdMiddleware.cs))
```csharp
// Generates x-correlation-id if missing, adds to response headers
public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
{
    var req = await context.GetHttpRequestDataAsync();
    if (req != null)
    {
        if (!req.Headers.TryGetValues("x-correlation-id", out var correlationIds))
        {
            var correlationId = Guid.NewGuid().ToString();
            req.Headers.Add("x-correlation-id", correlationId);
            _logger.LogInformation("Generated correlation ID: {CorrelationId}", correlationId);
        }
    }

    await next(context);
}
```

**3. ApiKeyAuthMiddleware** ([Source](../../Middleware/ApiKeyAuthMiddleware.cs))
```csharp
// Validates x-api-key header, skips Swagger endpoints
public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
{
    var req = await context.GetHttpRequestDataAsync();
    if (req == null)
    {
        await next(context);
        return;
    }

    // Skip authentication for documentation
    var path = req.Url.AbsolutePath?.ToLowerInvariant() ?? string.Empty;
    if (path.Contains("swagger") || path.Contains("openapi"))
    {
        await next(context);
        return;
    }

    // Validate API key
    var expectedKey = _config["Auth:ApiKey"];
    if (!req.Headers.TryGetValues("x-api-key", out var apiKeyHeaders) ||
        !string.Equals(apiKeyHeaders.FirstOrDefault(), expectedKey, StringComparison.OrdinalIgnoreCase))
    {
        var response = req.CreateResponse(HttpStatusCode.Unauthorized);
        await response.WriteAsJsonAsync(new { error = "Invalid API key" });
        context.GetInvocationResult().Value = response;
        return;
    }

    await next(context);
}
```

**4. AzureAdAuthMiddleware** ([Source](../../Middleware/AzureAdAuthMiddleware.cs))
```csharp
// Validates JWT Bearer token from Azure AD
public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
{
    var req = await context.GetHttpRequestDataAsync();
    if (req == null || !req.Headers.TryGetValues("Authorization", out var authHeaders))
    {
        await WriteUnauthorizedAsync(context, "Missing Authorization header");
        return;
    }

    var token = authHeaders.FirstOrDefault()?.Replace("Bearer ", "");
    if (string.IsNullOrEmpty(token))
    {
        await WriteUnauthorizedAsync(context, "Invalid Authorization header format");
        return;
    }

    try
    {
        var openIdConfig = await _configurationManager.GetConfigurationAsync(CancellationToken.None);
        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = new[] { $"https://login.microsoftonline.com/{_tenantId}/v2.0" },
            ValidateAudience = true,
            ValidAudience = _config["AzureAd:Audience"],
            IssuerSigningKeys = openIdConfig.SigningKeys,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromMinutes(2)
        };

        var handler = new JwtSecurityTokenHandler();
        var principal = handler.ValidateToken(token, validationParameters, out _);

        // Attach claims to context for downstream use
        context.Items["User"] = principal;

        await next(context);
    }
    catch (SecurityTokenValidationException ex)
    {
        _logger.LogWarning(ex, "Token validation failed");
        await WriteUnauthorizedAsync(context, "Token validation failed");
    }
}
```

### Configuration

**Development** (`local.settings.json`):
```json
{
  "Values": {
    "Auth:Mode": "apikey",
    "Auth:ApiKey": "dev-api-key-12345"
  }
}
```

**Production** (Azure App Settings):
```
Auth:Mode = azuread
AzureAd:TenantId = <tenant-guid>
AzureAd:ClientId = <app-registration-guid>
AzureAd:Audience = api://<app-registration-guid>
```

### Testing Middleware

```csharp
public class ApiKeyAuthMiddlewareTests
{
    [Fact]
    public async Task Invoke_ValidApiKey_CallsNext()
    {
        // Arrange
        var config = CreateConfiguration(new Dictionary<string, string>
        {
            { "Auth:Mode", "apikey" },
            { "Auth:ApiKey", "test-key" }
        });

        var middleware = new ApiKeyAuthMiddleware(config, Mock.Of<ILogger<ApiKeyAuthMiddleware>>());

        var context = CreateFunctionContext();
        var req = CreateHttpRequest();
        req.Headers.Add("x-api-key", "test-key");

        bool nextCalled = false;
        Task Next(FunctionContext ctx)
        {
            nextCalled = true;
            return Task.CompletedTask;
        }

        // Act
        await middleware.Invoke(context, Next);

        // Assert
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Invoke_InvalidApiKey_Returns401()
    {
        // Similar setup, assert 401 response
    }
}
```

---

## Related Decisions

- [ADR-001: Use Azure Functions Isolated Worker Model](./ADR-001-azure-functions-isolated-worker.md)
- [ADR-006: Dual Authentication Strategy](./ADR-006-dual-authentication-strategy.md)
- [ADR-008: Error Handling Strategy](./ADR-008-error-handling-strategy.md)

---

## References

- [Azure Functions Middleware](https://learn.microsoft.com/en-us/azure/azure-functions/dotnet-isolated-process-guide#middleware)
- [ASP.NET Core Middleware](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/middleware/)

---

**Change Log**:
- 2025-12-17: Initial proposal
- 2025-12-18: Accepted after team review
