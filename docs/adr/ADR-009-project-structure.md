# ADR-009: Project Structure and Naming Conventions

**Status**: Accepted

**Date**: 2025-12-18

---

## Context

Need consistent project organization for:
- Easy navigation (new developers find files quickly)
- Logical grouping (related code together)
- Scalability (structure supports 50+ functions)

---

## Decision

**Folder Structure**:
```
MailSubscriptionFunctionApp/
│
├── docs/                           # Documentation
│   ├── adr/                        # Architecture Decision Records
│   ├── api/                        # API documentation
│   └── deployment/                 # Deployment guides
│
├── Functions/                      # Presentation Layer
│   ├── Subscriptions/              # Group by feature area
│   │   ├── CreateMailSubscription.cs
│   │   ├── RenewMailSubscription.cs
│   │   └── DeleteMailSubscription.cs
│   └── Health/
│       └── HealthCheck.cs
│
├── Services/                       # Business Logic Layer
│   ├── GraphSubscriptionService.cs
│   └── MailSubscriptionService.cs
│
├── Infrastructure/                 # Cross-Cutting Concerns
│   ├── BaseFunction.cs
│   ├── DbConnectionFactory.cs
│   ├── AzureAppInsightsTelemetry.cs
│   ├── NoOpTelemetry.cs
│   ├── OpenApiConfig.cs
│   ├── SecurityDocumentFilter.cs
│   └── SwaggerAuthDefinitions.cs
│
├── Middleware/                     # Request Pipeline
│   ├── ApiKeyAuthMiddleware.cs
│   ├── AzureAdAuthMiddleware.cs
│   ├── CorrelationIdMiddleware.cs
│   ├── FunctionExceptionMiddleware.cs
│   ├── OperationNameMiddleware.cs
│   └── MiddlewareExtensions.cs
│
├── Models/                         # Domain Entities & DTOs
│   └── MailSubscription.cs
│
├── Interfaces/                     # Contracts
│   ├── ICustomTelemetry.cs
│   ├── IDbConnectionFactory.cs
│   ├── IGraphSubscriptionClient.cs
│   └── IMailSubscriptionRepository.cs
│
├── Program.cs                      # Application Entry Point
├── host.json                       # Functions Runtime Config
├── local.settings.json             # Local Development Config
├── Dockerfile                      # Container Image
└── MailSubscriptionFunctionApp.csproj
```

---

## Naming Conventions

**Files & Classes**:
- PascalCase: `CreateMailSubscription.cs`
- Match class name to file name
- Suffix service interfaces with purpose: `IGraphSubscriptionClient`, `IMailSubscriptionRepository`

**Functions**:
- Verb + Noun: `CreateMailSubscription`, `RenewMailSubscription`
- Grouped in folders by feature: `Functions/Subscriptions/`, `Functions/Notifications/`

**Routes**:
- Lowercase with hyphens: `/api/subscriptions`, `/api/health-check`
- RESTful conventions: `POST /api/subscriptions`, `GET /api/subscriptions/{id}`

**Configuration Keys**:
- Hierarchical with colons: `AzureAd:TenantId`, `Graph:NotificationUrl`
- Azure-compatible: Support double underscore (`AzureAd__TenantId`)

**Database**:
- Lowercase, no underscores: `mailsubscriptions`, `subscriptionid`
- Aligns with existing CRDB Bank conventions

---

## Scalability Pattern

**As Project Grows**:
```
Functions/
├── Subscriptions/
│   ├── CreateMailSubscription.cs
│   ├── RenewMailSubscription.cs
│   ├── DeleteMailSubscription.cs
│   └── GetSubscription.cs
├── Notifications/
│   ├── ReceiveNotification.cs
│   └── ValidateNotification.cs
├── Management/
│   ├── ListSubscriptions.cs
│   └── GetSubscriptionStats.cs
└── Health/
    └── HealthCheck.cs
```

**Service Organization**:
```
Services/
├── Graph/
│   ├── GraphSubscriptionService.cs
│   └── GraphNotificationService.cs
├── Persistence/
│   ├── MailSubscriptionService.cs
│   └── NotificationLogService.cs
└── External/
    └── EmailService.cs
```

---

## Related

- [ADR-007: Layered Architecture and Separation of Concerns](./ADR-007-layered-architecture.md)

---

**References**:
- [C# Naming Guidelines](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/identifier-names)
- [RESTful API Design](https://restfulapi.net/resource-naming/)
