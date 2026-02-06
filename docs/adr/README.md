# Architecture Decision Records (ADR)

## About

This directory contains Architecture Decision Records (ADRs) for the MailSubscriptionFunctionApp.

ADRs document significant architectural decisions made in this project, including context, rationale, consequences, and alternatives considered. They serve as a guide for future development and help new team members understand why certain technical choices were made.

## Format

Each ADR follows the MADR (Markdown Any Decision Records) format:

- **Status**: Proposed | Accepted | Deprecated | Superseded
- **Context**: The situation, problem, or opportunity
- **Decision**: The chosen solution
- **Consequences**: Positive and negative outcomes
- **Alternatives Considered**: Other options evaluated
- **Implementation Notes**: Practical guidance for developers

## Index of ADRs

### Core Architecture

- [ADR-001: Use Azure Functions Isolated Worker Model](./ADR-001-azure-functions-isolated-worker.md)
- [ADR-002: PostgreSQL for Subscription Persistence](./ADR-002-postgresql-persistence.md)
- [ADR-003: Dual Telemetry Backend Support](./ADR-003-dual-telemetry-backend.md)

### Infrastructure & Configuration

- [ADR-004: Middleware Pipeline Architecture](./ADR-004-middleware-pipeline.md)
- [ADR-005: Dependency Injection and Service Lifetimes](./ADR-005-dependency-injection.md)
- [ADR-006: Dual Authentication Strategy](./ADR-006-dual-authentication-strategy.md)

### Code Quality & Patterns

- [ADR-007: Layered Architecture and Separation of Concerns](./ADR-007-layered-architecture.md)
- [ADR-008: Error Handling Strategy](./ADR-008-error-handling-strategy.md)
- [ADR-009: Project Structure and Naming Conventions](./ADR-009-project-structure.md)

### Performance & Reliability

- [ADR-010: HTTP Client Configuration and Resilience](./ADR-010-http-client-resilience.md)
- [ADR-011: Database Connection Pooling Strategy](./ADR-011-connection-pooling.md)

## Creating New ADRs

When making a significant architectural decision:

1. Copy [ADR-TEMPLATE.md](./ADR-TEMPLATE.md)
2. Rename to `ADR-XXX-descriptive-title.md`
3. Fill in all sections
4. Submit for team review
5. Update this README with the new ADR link

## Status Definitions

- **Proposed**: Under discussion, not yet implemented
- **Accepted**: Approved and implemented
- **Deprecated**: No longer recommended, but still in use
- **Superseded**: Replaced by a newer decision (reference the new ADR)

## Related Documentation

- [Project Architecture Overview](../architecture/overview.md)
- [Deployment Guide](../deployment/README.md)
- [API Documentation](../../swagger.json)
- [Database Schema](../database/schema.md)

---

**Last Updated**: 2026-02-06
**Maintained By**: Development Team
