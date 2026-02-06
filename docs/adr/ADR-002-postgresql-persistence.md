# ADR-002: PostgreSQL for Subscription Persistence with Dapper ORM

**Status**: Accepted

**Date**: 2025-12-15

**Authors**: Development Team, Database Team

**Reviewers**: Architecture Team

---

## Context

### Problem Statement
The MailSubscriptionFunctionApp needs a persistent data store for Microsoft Graph subscription records. Requirements include:
- Store subscription metadata (ID, expiration, notification URL)
- Support upsert operations for subscription renewals
- Query subscriptions expiring within time windows
- Provide ACID guarantees for critical subscription data
- Scale to 100,000+ active subscriptions
- Support both cloud and on-premises deployments

### Requirements
- **ACID Compliance**: Ensure data consistency for subscription lifecycle
- **High Availability**: 99.9% uptime for subscription queries
- **Performance**: <50ms query latency for single subscription lookups
- **Concurrency**: Handle concurrent subscription creates/renewals safely
- **Indexing**: Efficient queries by userId and expirationTime
- **Backup**: Point-in-time recovery with 15-minute RPO
- **Security**: TLS encryption, connection pooling, parameterized queries

### Constraints
- Must integrate with existing CRDB Bank PostgreSQL infrastructure
- Database: `CRDBTZ-PR-MJDB1.crdbbank.co.tz:55432`
- Must use existing database naming conventions (lowercase, no underscores)
- Must support PostgreSQL 14+ features
- ORM must be lightweight (no >100MB dependencies)

---

## Decision

**Use PostgreSQL 14+ as the primary data store with Dapper as the micro-ORM for data access.**

**Data Model**:
```sql
CREATE TABLE mailsubscriptions (
    subscriptionid VARCHAR(100) PRIMARY KEY,
    userid VARCHAR(256) NOT NULL,
    subscriptionstarttime TIMESTAMP NOT NULL,
    subscriptionexpirationtime TIMESTAMP NOT NULL,
    notificationurl VARCHAR(512) NOT NULL,
    clientstate VARCHAR(64) NOT NULL,
    createddatetime TIMESTAMP NOT NULL,
    lastreneweddatetime TIMESTAMP NULL,
    subscriptionchangetype VARCHAR(10) NOT NULL,
    applicationid VARCHAR(50) NULL
);

-- Indexes for query performance
CREATE INDEX idx_mailsubscriptions_userid ON mailsubscriptions(userid);
CREATE INDEX idx_mailsubscriptions_expiration ON mailsubscriptions(subscriptionexpirationtime);
```

**ORM**: Dapper 2.1+ for lightweight, high-performance data access

---

## Consequences

### Positive
- **Performance**: Dapper provides near-raw ADO.NET performance (2-3x faster than EF Core)
- **Simplicity**: No complex ORM abstractions, direct SQL control
- **Lightweight**: 200KB NuGet package vs 15MB+ for EF Core
- **SQL Control**: Full control over queries for optimization
- **Existing Infrastructure**: Leverages CRDB Bank's PostgreSQL expertise
- **ACID Guarantees**: Built-in transaction support
- **Proven Reliability**: PostgreSQL's 20+ year track record
- **Advanced Features**: JSON columns, full-text search, CTE support
- **Connection Pooling**: Npgsql provides robust connection management
- **Cost-Effective**: Lower licensing costs than SQL Server

### Negative
- **Manual Schema Management**: No automatic migrations like EF Core
- **No Change Tracking**: Must manually track entity changes
- **SQL Maintenance**: SQL strings in C# code (no compile-time checking)
- **No LINQ Support**: Must write raw SQL queries
- **Mapping Code**: Manual property mapping (though Dapper helps)
- **Migration Scripts**: Manual DDL scripts for schema changes
- **Testing Complexity**: Requires database for integration tests

### Risks
- **SQL Injection**: If parameterized queries not used consistently
  - *Mitigation*: Code review checklist, static analysis (SonarQube), all queries use `@parameters`
- **N+1 Query Problem**: Dapper doesn't optimize multi-entity queries
  - *Mitigation*: Use explicit JOIN queries, monitor query performance
- **Schema Drift**: Manual migrations can cause dev/prod differences
  - *Mitigation*: Version-controlled migration scripts, automated deployment validation
- **Connection Leaks**: Improper disposal can exhaust connection pool
  - *Mitigation*: Enforce `using` statements, connection leak detection in tests

---

## Alternatives Considered

### Alternative 1: Entity Framework Core 8
**Description**: Microsoft's full-featured ORM with migrations, change tracking, and LINQ support.

**Pros**:
- Automatic migrations from code-first models
- LINQ query support (type-safe)
- Change tracking for updates
- Eager/lazy loading for related entities
- Better for complex domain models
- Extensive Microsoft documentation

**Cons**:
- 10x slower than Dapper for simple CRUD (50ms vs 5ms)
- 15MB+ NuGet package footprint
- Higher memory usage (~50MB per context)
- Generated SQL sometimes inefficient
- Overkill for simple data access patterns
- Cold start penalty in serverless

**Reason for Rejection**:
Performance is critical for subscription creation (<500ms total). Dapper's 2-3x speed advantage is significant. Our domain model is simple (1 table), so EF Core's features aren't needed. Serverless cold start is already a challenge; EF Core's assembly loading adds 200-300ms.

---

### Alternative 2: Azure Cosmos DB
**Description**: NoSQL document database with global distribution and automatic scaling.

**Pros**:
- Automatic scaling and global distribution
- <10ms read/write latency (with provisioned throughput)
- Built-in replication
- Schema-less (flexible data model)
- Native JSON document storage

**Cons**:
- Significantly more expensive ($0.008 per RU/s vs PostgreSQL's fixed cost)
- Eventual consistency by default (strong consistency costs 2x)
- No ACID transactions across documents
- Limited query capabilities vs SQL
- Learning curve for team familiar with SQL
- Not compatible with on-premises deployment

**Reason for Rejection**:
CRDB Bank has existing PostgreSQL infrastructure and expertise. Cosmos DB's cost model is prohibitive for 100K+ subscriptions. ACID guarantees needed for subscription lifecycle. On-premises deployment requirement rules out cloud-only services.

---

### Alternative 3: Azure SQL Database
**Description**: Managed SQL Server database in Azure.

**Pros**:
- Enterprise-grade reliability
- Advanced query optimization
- Built-in monitoring and tuning
- Familiar to Microsoft-centric teams
- Excellent .NET integration

**Cons**:
- 3x more expensive than PostgreSQL (DTU pricing)
- Vendor lock-in to Azure
- No on-premises compatibility
- Less open-source ecosystem
- Lower performance for connection-heavy workloads

**Reason for Rejection**:
Existing infrastructure is PostgreSQL. Team expertise is PostgreSQL-focused. Cost savings of PostgreSQL are significant. No compelling SQL Server-specific features needed.

---

## Implementation Notes

### For Developers

**Connection Factory Pattern**:
```csharp
// Infrastructure/DbConnectionFactory.cs
public class DbConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;
    private readonly ILogger<DbConnectionFactory> _logger;

    public DbConnectionFactory(IConfiguration configuration, ILogger<DbConnectionFactory> logger)
    {
        _connectionString = configuration.GetConnectionString("PostgreSqlConnection");

        // Configure connection pooling
        var builder = new NpgsqlConnectionStringBuilder(_connectionString)
        {
            Pooling = true,
            MinPoolSize = 5,
            MaxPoolSize = 100,
            ConnectionIdleLifetime = 300,
            SslMode = SslMode.Require
        };

        _connectionString = builder.ToString();
    }

    public async Task<IDbConnection> CreateConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
```

**Repository Pattern with Dapper**:
```csharp
// Services/MailSubscriptionService.cs
public class MailSubscriptionService : IMailSubscriptionRepository
{
    private readonly IDbConnectionFactory _dbFactory;

    public async Task SaveSubscriptionAsync(
        MailSubscription subscription,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
            INSERT INTO mailsubscriptions (
                subscriptionid, userid, subscriptionstarttime,
                subscriptionexpirationtime, notificationurl,
                clientstate, createddatetime, lastreneweddatetime,
                subscriptionchangetype, applicationid
            )
            VALUES (
                @SubscriptionId, @UserId, @SubscriptionStartTime,
                @SubscriptionExpirationTime, @NotificationUrl,
                @ClientState, @CreatedDateTime, @LastRenewedDateTime,
                @SubscriptionChangeType, @ApplicationId
            )
            ON CONFLICT (subscriptionid)
            DO UPDATE SET
                lastreneweddatetime = EXCLUDED.lastreneweddatetime,
                subscriptionexpirationtime = EXCLUDED.subscriptionexpirationtime;";

        using var conn = await _dbFactory.CreateConnectionAsync(cancellationToken);

        // Dapper automatically maps object properties to @parameters
        await conn.ExecuteAsync(new CommandDefinition(
            sql,
            subscription,
            cancellationToken: cancellationToken,
            commandTimeout: 30
        ));
    }
}
```

**Key Files**:
- [`Infrastructure/DbConnectionFactory.cs`](../../Infrastructure/DbConnectionFactory.cs) - Connection management
- [`Services/MailSubscriptionService.cs`](../../Services/MailSubscriptionService.cs) - Repository implementation
- [`Models/MailSubscription.cs`](../../Models/MailSubscription.cs) - Entity model
- [`Interfaces/IMailSubscriptionRepository.cs`](../../Interfaces/IMailSubscriptionRepository.cs) - Repository contract

**Configuration** (`local.settings.json`):
```json
{
  "ConnectionStrings": {
    "PostgreSqlConnection": "Host=localhost;Port=5432;Database=mjsapp;Username=mjsapp_user;Password=***;Pooling=true;MinPoolSize=5;MaxPoolSize=100;SslMode=Require"
  }
}
```

**NuGet Packages Required**:
```xml
<PackageReference Include="Dapper" Version="2.1.66" />
<PackageReference Include="Npgsql" Version="10.0.0" />
```

### Database Schema Management

**Migration Script Template** (`migrations/V001__Create_MailSubscriptions_Table.sql`):
```sql
-- Migration: V001__Create_MailSubscriptions_Table.sql
-- Description: Initial schema for mail subscriptions
-- Author: Development Team
-- Date: 2025-12-15

BEGIN;

-- Create table
CREATE TABLE IF NOT EXISTS mailsubscriptions (
    subscriptionid VARCHAR(100) PRIMARY KEY,
    userid VARCHAR(256) NOT NULL,
    subscriptionstarttime TIMESTAMP NOT NULL,
    subscriptionexpirationtime TIMESTAMP NOT NULL,
    notificationurl VARCHAR(512) NOT NULL,
    clientstate VARCHAR(64) NOT NULL,
    createddatetime TIMESTAMP NOT NULL DEFAULT NOW(),
    lastreneweddatetime TIMESTAMP NULL,
    subscriptionchangetype VARCHAR(10) NOT NULL,
    applicationid VARCHAR(50) NULL,

    CONSTRAINT chk_expiration CHECK (subscriptionexpirationtime > subscriptionstarttime)
);

-- Create indexes
CREATE INDEX idx_mailsubscriptions_userid
    ON mailsubscriptions(userid);

CREATE INDEX idx_mailsubscriptions_expiration
    ON mailsubscriptions(subscriptionexpirationtime);

CREATE INDEX idx_active_subscriptions
    ON mailsubscriptions(subscriptionexpirationtime, userid)
    WHERE subscriptionexpirationtime > NOW();

-- Foreign key to orguser table (if exists)
-- ALTER TABLE mailsubscriptions
--     ADD CONSTRAINT fk_mailsubscriptions_user
--     FOREIGN KEY (userid) REFERENCES orguser(userid);

COMMIT;
```

**Rollback Script** (`migrations/V001__Create_MailSubscriptions_Table_Rollback.sql`):
```sql
-- Rollback: V001__Create_MailSubscriptions_Table_Rollback.sql

BEGIN;

DROP INDEX IF EXISTS idx_active_subscriptions;
DROP INDEX IF EXISTS idx_mailsubscriptions_expiration;
DROP INDEX IF EXISTS idx_mailsubscriptions_userid;
DROP TABLE IF EXISTS mailsubscriptions;

COMMIT;
```

### Testing Strategy

**Unit Tests** (with in-memory or test database):
```csharp
public class MailSubscriptionServiceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres;
    private IDbConnectionFactory _dbFactory;

    public MailSubscriptionServiceTests()
    {
        // Testcontainers for PostgreSQL
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:14")
            .Build();
    }

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        // Run migration scripts
        var connectionString = _postgres.GetConnectionString();
        await RunMigrations(connectionString);

        _dbFactory = new DbConnectionFactory(
            CreateConfiguration(connectionString),
            Mock.Of<ILogger<DbConnectionFactory>>());
    }

    [Fact]
    public async Task SaveSubscriptionAsync_NewSubscription_Inserts()
    {
        // Arrange
        var service = new MailSubscriptionService(_dbFactory, ...);
        var subscription = new MailSubscription
        {
            SubscriptionId = "test-123",
            UserId = "test@example.com",
            SubscriptionExpirationTime = DateTime.UtcNow.AddHours(48)
        };

        // Act
        await service.SaveSubscriptionAsync(subscription, CancellationToken.None);

        // Assert
        using var conn = await _dbFactory.CreateConnectionAsync();
        var saved = await conn.QuerySingleOrDefaultAsync<MailSubscription>(
            "SELECT * FROM mailsubscriptions WHERE subscriptionid = @Id",
            new { Id = "test-123" });

        Assert.NotNull(saved);
        Assert.Equal("test@example.com", saved.UserId);
    }

    public async Task DisposeAsync() => await _postgres.DisposeAsync();
}
```

**Integration Tests**:
```bash
# Run integration tests against test database
dotnet test --filter "Category=Integration"
```

### Monitoring & Observability

**Key Metrics**:
- Connection pool utilization (current/max)
- Average query execution time (<50ms target)
- Database CPU and memory usage
- Active subscription count
- Subscription renewal rate
- Failed transaction count

**PostgreSQL Monitoring Queries**:
```sql
-- Connection pool stats
SELECT count(*), state FROM pg_stat_activity
WHERE datname = 'mjsapp'
GROUP BY state;

-- Slow queries (>100ms)
SELECT query, calls, mean_exec_time, max_exec_time
FROM pg_stat_statements
WHERE mean_exec_time > 100
ORDER BY mean_exec_time DESC
LIMIT 10;

-- Table size and index usage
SELECT
    schemaname, tablename,
    pg_size_pretty(pg_total_relation_size(schemaname||'.'||tablename)) AS size,
    idx_scan, seq_scan
FROM pg_stat_user_tables
WHERE tablename = 'mailsubscriptions';
```

**Application Insights Queries**:
```kql
// Database dependency calls
dependencies
| where type == "SQL"
| where target contains "CRDBTZ-PR-MJDB1"
| summarize
    Count = count(),
    AvgDuration = avg(duration),
    P95Duration = percentile(duration, 95),
    FailureRate = countif(success == false) * 100.0 / count()
  by name
| where AvgDuration > 50  // Alert if >50ms
```

---

## Related Decisions

- [ADR-005: Dependency Injection and Service Lifetimes](./ADR-005-dependency-injection.md)
- [ADR-008: Error Handling Strategy](./ADR-008-error-handling-strategy.md)
- [ADR-011: Database Connection Pooling Strategy](./ADR-011-connection-pooling.md)

---

## References

- [Dapper Documentation](https://github.com/DapperLib/Dapper)
- [Npgsql Documentation](https://www.npgsql.org/doc/)
- [PostgreSQL Performance Tuning](https://wiki.postgresql.org/wiki/Performance_Optimization)
- [Dapper vs EF Core Performance](https://exceptionnotfound.net/dapper-vs-entity-framework-core-query-performance-benchmarking-2019/)

---

**Change Log**:
- 2025-12-15: Initial proposal by Development Team
- 2025-12-18: Database Team review - approved schema
- 2025-12-20: Accepted after performance benchmarks
- 2026-01-05: Added migration script templates
