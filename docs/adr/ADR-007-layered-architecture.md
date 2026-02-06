# ADR-007: Layered Architecture and Separation of Concerns

**Status**: Accepted

**Date**: 2025-12-18

---

## Context

Need clear architectural boundaries for maintainability and testability. Avoid "big ball of mud" where business logic, data access, and API concerns mix.

---

## Decision

**Adopt Layered Architecture with clear separation:**

```
┌─────────────────────────────────────────┐
│  Presentation Layer (Azure Functions)  │  ← HTTP Triggers, DTOs, OpenAPI
├─────────────────────────────────────────┤
│  Business Logic Layer (Services)       │  ← Domain logic, orchestration
├─────────────────────────────────────────┤
│  Data Access Layer (Repositories)      │  ← Database queries, persistence
├─────────────────────────────────────────┤
│  Infrastructure Layer                   │  ← Cross-cutting concerns
│  (Middleware, Telemetry, DI, Config)   │
└─────────────────────────────────────────┘
```

**Folder Structure**:
```
MailSubscriptionFunctionApp/
├── Functions/              # Presentation Layer
│   └── Subscriptions/
│       └── CreateMailSubscription.cs
├── Services/               # Business Logic
│   ├── GraphSubscriptionService.cs
│   └── MailSubscriptionService.cs
├── Infrastructure/         # Cross-Cutting
│   ├── BaseFunction.cs
│   ├── DbConnectionFactory.cs
│   └── Telemetry/
├── Middleware/             # Request Pipeline
│   ├── ApiKeyAuthMiddleware.cs
│   └── FunctionExceptionMiddleware.cs
├── Models/                 # Domain Entities
│   └── MailSubscription.cs
├── Interfaces/             # Contracts
│   ├── IGraphSubscriptionClient.cs
│   └── IMailSubscriptionRepository.cs
└── Program.cs              # Composition Root
```

---

## Principles

**1. Dependency Rule**: Dependencies point inward
- Presentation → Business Logic → Data Access
- Infrastructure is referenced by all layers
- No layer depends on presentation

**2. Interface Segregation**:
- Each service has a focused interface
- `IGraphSubscriptionClient` for Graph API
- `IMailSubscriptionRepository` for persistence

**3. Single Responsibility**:
- Functions: HTTP handling, validation
- Services: Business logic, orchestration
- Repositories: Data persistence only

**4. Dependency Inversion**:
- High-level modules depend on abstractions
- Example: `GraphSubscriptionService` depends on `IDbConnectionFactory`, not concrete Npgsql classes

---

## Implementation

**Function (Presentation Layer)**:
```csharp
public class CreateMailSubscription : BaseFunction
{
    private readonly IGraphSubscriptionClient _graphClient;
    private readonly IMailSubscriptionRepository _repository;

    public async Task<HttpResponseData> Run(HttpRequestData req)
    {
        // 1. Validate request
        var request = await ParseRequestAsync(req);

        // 2. Call business logic
        var subscription = await _graphClient.CreateMailSubscriptionAsync(
            request.UserId, cancellationToken);

        // 3. Persist
        await _repository.SaveSubscriptionAsync(subscription, cancellationToken);

        // 4. Return response
        return await CreateSuccessResponseAsync(req, subscription);
    }
}
```

**Service (Business Logic Layer)**:
```csharp
public class GraphSubscriptionService : IGraphSubscriptionClient
{
    public async Task<MailSubscription> CreateMailSubscriptionAsync(string userId)
    {
        // Business logic: Create Graph subscription
        // No HTTP concerns, no database logic
        var graphClient = await GetOrCreateGraphClientAsync();
        var subscription = await graphClient.Subscriptions.PostAsync(...);

        return MapToMailSubscription(subscription, userId);
    }
}
```

**Repository (Data Access Layer)**:
```csharp
public class MailSubscriptionService : IMailSubscriptionRepository
{
    public async Task SaveSubscriptionAsync(MailSubscription subscription)
    {
        // Data access only: Execute SQL
        // No business logic, no HTTP concerns
        using var conn = await _dbFactory.CreateConnectionAsync();
        await conn.ExecuteAsync(sql, subscription);
    }
}
```

---

## Benefits

- **Testability**: Mock interfaces at each layer boundary
- **Maintainability**: Changes localized to specific layers
- **Reusability**: Services can be reused across functions
- **Clarity**: Each file has obvious responsibility

---

## Related

- [ADR-002: PostgreSQL for Subscription Persistence](./ADR-002-postgresql-persistence.md)
- [ADR-005: Dependency Injection and Service Lifetimes](./ADR-005-dependency-injection.md)

---

**References**:
- [Clean Architecture](https://blog.cleancoder.com/uncle-bob/2012/08/13/the-clean-architecture.html)
