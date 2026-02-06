# ADR-006: Dual Authentication Strategy (API Key + Azure AD)

**Status**: Accepted

**Date**: 2025-12-17

---

## Context

Different environments require different authentication mechanisms:
- **Development/Testing**: API Key (simple, fast iteration)
- **Production**: Azure AD OAuth 2.0 (enterprise SSO, fine-grained permissions)

Hardcoding one approach blocks environment flexibility.

---

## Decision

**Implement configurable authentication via `Auth:Mode` setting:**

```csharp
// Configuration determines auth middleware
if (authMode == "apikey")
    builder.UseMiddleware<ApiKeyAuthMiddleware>();
else if (authMode == "azuread")
    builder.UseMiddleware<AzureAdAuthMiddleware>();
```

**API Key Mode** (`Auth:Mode=apikey`):
- Validates `x-api-key` header against configured key
- Case-insensitive comparison
- Exempts Swagger/OpenAPI endpoints

**Azure AD Mode** (`Auth:Mode=azuread`):
- Validates JWT Bearer token
- OpenID Connect discovery for signing keys
- Supports v1.0 and v2.0 endpoints
- 2-minute clock skew tolerance

---

## Implementation

**Configuration**:
```json
// Development
{
  "Auth:Mode": "apikey",
  "Auth:ApiKey": "dev-key-12345"
}

// Production
{
  "Auth:Mode": "azuread",
  "AzureAd:TenantId": "xxx",
  "AzureAd:ClientId": "xxx",
  "AzureAd:Audience": "api://xxx"
}
```

**Key Files**:
- [`Middleware/ApiKeyAuthMiddleware.cs`](../../Middleware/ApiKeyAuthMiddleware.cs)
- [`Middleware/AzureAdAuthMiddleware.cs`](../../Middleware/AzureAdAuthMiddleware.cs)
- [`Middleware/MiddlewareExtensions.cs`](../../Middleware/MiddlewareExtensions.cs)

---

## Related

- [ADR-004: Middleware Pipeline Architecture](./ADR-004-middleware-pipeline.md)

---

**References**:
- [Azure AD Token Validation](https://learn.microsoft.com/en-us/azure/active-directory/develop/access-tokens)
