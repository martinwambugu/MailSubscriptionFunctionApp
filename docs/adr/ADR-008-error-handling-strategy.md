# ADR-008: Error Handling Strategy

**Status**: Accepted

**Date**: 2025-12-18

---

## Context

Comprehensive error handling needed for:
- Invalid user input (400 Bad Request)
- Authentication failures (401 Unauthorized)
- Graph API errors (503 Service Unavailable)
- Database connection failures (500 Internal Server Error)
- Unexpected exceptions (500 Internal Server Error)

Must provide actionable error messages without exposing internals.

---

## Decision

**Implement three-tier error handling:**

```
1. Middleware Layer (Global)
   └── FunctionExceptionMiddleware catches ALL unhandled exceptions

2. Function Layer (Input Validation)
   └── Validate HTTP requests, return 400 for bad input

3. Service Layer (Domain Errors)
   └── Throw InvalidOperationException with context
       (Graph API errors, DB constraint violations)
```

**Error Response Format**:
```json
{
  "error": "User-friendly error message",
  "correlationId": "abc-123-def",
  "timestamp": "2026-02-06T10:30:00Z"
}
```

---

## Implementation

**Function-Level Validation**:
```csharp
public async Task<HttpResponseData> Run(HttpRequestData req)
{
    try
    {
        // Validate Content-Type
        if (!req.Headers.GetValues("Content-Type").Contains("application/json"))
            return await BadRequest(req, "Content-Type must be 'application/json'");

        // Validate request body
        var request = await JsonSerializer.DeserializeAsync<CreateSubscriptionRequest>(req.Body);
        if (string.IsNullOrWhiteSpace(request?.UserId))
            return await BadRequest(req, "The 'userId' field is required");

        // Business logic...
    }
    catch (InvalidOperationException opEx)
    {
        // Known business logic error
        _logger.LogError(opEx, "Operation failed: {Message}", opEx.Message);
        return await InternalServerError(req, opEx.Message);
    }
    catch (OperationCanceledException)
    {
        // Request cancelled/timeout
        return req.CreateResponse(HttpStatusCode.RequestTimeout);
    }
    catch (Exception ex)
    {
        // Unexpected error (caught by middleware)
        throw;
    }
}
```

**Service-Level Error Handling**:
```csharp
public async Task<MailSubscription> CreateMailSubscriptionAsync(string userId)
{
    try
    {
        var created = await graphClient.Subscriptions.PostAsync(...);
        return MapToMailSubscription(created, userId);
    }
    catch (ODataError odataEx)
    {
        // Graph API specific error
        _logger.LogError(odataEx, "Graph API error: {Code} - {Message}",
            odataEx.Error?.Code, odataEx.Error?.Message);

        throw new InvalidOperationException(
            $"Graph API error: {odataEx.Error?.Message}",
            odataEx);
    }
    catch (HttpRequestException httpEx)
    {
        // Network/connectivity error
        throw new InvalidOperationException(
            $"Network error: {httpEx.Message}. Ensure connectivity to graph.microsoft.com",
            httpEx);
    }
}
```

**Repository-Level Error Handling**:
```csharp
public async Task SaveSubscriptionAsync(MailSubscription subscription)
{
    try
    {
        await conn.ExecuteAsync(sql, subscription);
    }
    catch (PostgresException pgEx) when (pgEx.SqlState == "23505")
    {
        // Unique constraint violation
        throw new InvalidOperationException(
            $"Subscription '{subscription.SubscriptionId}' already exists",
            pgEx);
    }
    catch (PostgresException pgEx) when (pgEx.SqlState == "23503")
    {
        // Foreign key violation
        throw new InvalidOperationException(
            $"User '{subscription.UserId}' does not exist in the database",
            pgEx);
    }
    catch (PostgresException pgEx) when (pgEx.SqlState?.StartsWith("08") == true)
    {
        // Connection error
        throw new InvalidOperationException(
            "Database connection error. Please retry",
            pgEx);
    }
}
```

**Middleware Global Handler**:
```csharp
public class FunctionExceptionMiddleware : IFunctionsWorkerMiddleware
{
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

            var response = req.CreateResponse(HttpStatusCode.InternalServerError);
            await response.WriteAsJsonAsync(new
            {
                error = "An unexpected error occurred",
                correlationId = GetCorrelationId(req),
                timestamp = DateTime.UtcNow
            });
            context.GetInvocationResult().Value = response;
        }
    }
}
```

---

## Error Classification

| HTTP Code | Scenario | Example |
|-----------|----------|---------|
| 400 | Bad Request | Missing userId, invalid JSON |
| 401 | Unauthorized | Missing/invalid API key or JWT |
| 408 | Request Timeout | OperationCanceledException |
| 500 | Internal Server Error | Graph API error, DB error, unexpected exception |

---

## Logging Strategy

**All Errors Logged With**:
- Exception type and message
- Stack trace (for 500 errors)
- Correlation ID (for request tracing)
- Context (userId, subscriptionId, etc.)
- Telemetry (custom events for business errors)

**Example**:
```csharp
_logger.LogError(ex,
    "❌ Failed to create subscription for UserId: {UserId}. " +
    "CorrelationId: {CorrelationId}",
    userId, correlationId);

_telemetry.TrackException(ex, new Dictionary<string, string>
{
    { "Operation", "CreateMailSubscription" },
    { "UserId", userId },
    { "CorrelationId", correlationId }
});
```

---

## Related

- [ADR-003: Dual Telemetry Backend Support](./ADR-003-dual-telemetry-backend.md)
- [ADR-004: Middleware Pipeline Architecture](./ADR-004-middleware-pipeline.md)

---

**References**:
- [HTTP Status Codes](https://developer.mozilla.org/en-US/docs/Web/HTTP/Status)
- [PostgreSQL Error Codes](https://www.postgresql.org/docs/current/errcodes-appendix.html)
