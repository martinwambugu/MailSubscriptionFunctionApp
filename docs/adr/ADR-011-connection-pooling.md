# ADR-011: Database Connection Pooling Strategy

**Status**: Accepted

**Date**: 2025-12-19

---

## Context

Database connections are expensive to create (~50ms overhead). Without pooling:
- Each request creates new connection
- Port exhaustion risk (max 65,535 ports)
- Database connection limit exceeded
- Poor performance under load

Need efficient connection reuse without connection leaks.

---

## Decision

**Use Npgsql's built-in connection pooling with optimized settings:**

```csharp
var builder = new NpgsqlConnectionStringBuilder(connectionString)
{
    // Connection Pooling
    Pooling = true,
    MinPoolSize = 5,                    // Keep 5 connections warm
    MaxPoolSize = 100,                  // Max 100 concurrent connections
    ConnectionIdleLifetime = 300,       // Close idle connections after 5 min
    ConnectionPruningInterval = 10,     // Check for idle every 10 sec

    // Performance
    CommandTimeout = 30,                // 30 sec query timeout
    Timeout = 15,                       // 15 sec connection timeout
    NoResetOnClose = true,              // Reuse connection state (faster)
    MaxAutoPrepare = 20,                // Cache 20 prepared statements
    AutoPrepareMinUsages = 2,           // Prepare after 2 usages

    // Reliability
    KeepAlive = 30,                     // TCP keep-alive every 30 sec

    // Security
    SslMode = SslMode.Require,          // Enforce SSL/TLS
    TrustServerCertificate = false      // Validate certificates
};
```

**Usage Pattern**:
```csharp
// Factory creates connections from pool
public async Task<IDbConnection> CreateConnectionAsync()
{
    var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();  // Gets from pool
    return connection;
}

// Always use 'using' for proper disposal
using var conn = await _dbFactory.CreateConnectionAsync();
await conn.ExecuteAsync(sql, parameters);
// Connection returned to pool on dispose
```

---

## Pool Configuration Explained

**MinPoolSize = 5**:
- Keeps 5 connections open even when idle
- Avoids connection creation delay for first requests
- Trade-off: Uses database resources when idle

**MaxPoolSize = 100**:
- Limits concurrent database connections
- Prevents overwhelming database
- Should match database's `max_connections` setting
- Azure Functions: 100 connections per instance is safe

**ConnectionIdleLifetime = 300 seconds**:
- Closes connections idle for 5+ minutes
- Prevents stale connections
- Balances resource usage vs warmth

**NoResetOnClose = true**:
- Skips `DISCARD ALL` on connection return
- 10-20ms performance gain per query
- **Risk**: Leaks temporary tables or session state
- **Mitigation**: Always use parameterized queries, never temp tables

**MaxAutoPrepare = 20**:
- Caches up to 20 prepared statements
- Prepared statements execute faster (~30% speedup)
- Auto-prepares after 2 usages
- Trade-off: Uses ~2KB memory per statement

---

## Connection Leak Prevention

**Pattern 1: Always Use 'using' Statement**:
```csharp
// ✅ Good: Connection auto-disposed
using var conn = await _dbFactory.CreateConnectionAsync();
await conn.ExecuteAsync(sql, parameters);

// ❌ Bad: Connection leaked if exception occurs
var conn = await _dbFactory.CreateConnectionAsync();
await conn.ExecuteAsync(sql, parameters);
conn.Dispose();  // May not execute if exception thrown
```

**Pattern 2: Scoped DbConnectionFactory**:
```csharp
// Register as Scoped (not Singleton)
builder.Services.AddScoped<IDbConnectionFactory, DbConnectionFactory>();

// Scoped lifetime ensures disposal at end of request
```

**Pattern 3: Test for Leaks**:
```csharp
[Fact]
public async Task Repository_ConnectionLeak_Test()
{
    var initialConnections = GetActiveConnectionCount();

    for (int i = 0; i < 100; i++)
    {
        await _repository.SaveSubscriptionAsync(subscription);
    }

    var finalConnections = GetActiveConnectionCount();
    Assert.Equal(initialConnections, finalConnections);
}
```

---

## Monitoring

**Application Telemetry**:
```csharp
connection.StateChange += (sender, args) =>
{
    _logger.LogDebug(
        "Connection state: {OriginalState} → {CurrentState}",
        args.OriginalState, args.CurrentState);
};
```

**PostgreSQL Queries**:
```sql
-- Active connections by state
SELECT count(*), state, wait_event_type
FROM pg_stat_activity
WHERE datname = 'mjsapp'
GROUP BY state, wait_event_type;

-- Connection pool stats (if using PgBouncer)
SHOW POOLS;

-- Long-running queries (>1 second)
SELECT pid, usename, query, now() - query_start AS duration
FROM pg_stat_activity
WHERE state = 'active'
  AND now() - query_start > interval '1 second'
ORDER BY duration DESC;
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
    Failures = countif(success == false)
  by name
| where AvgDuration > 50  // Alert if queries >50ms
```

---

## Troubleshooting

**Problem**: `Npgsql.NpgsqlException: The connection pool has been exhausted`

**Causes**:
1. Connection leaks (missing `using` statements)
2. Long-running queries blocking pool
3. MaxPoolSize too low for load

**Solutions**:
```csharp
// Increase pool size
MaxPoolSize = 200

// Add connection timeout
Timeout = 15  // Fail fast if pool exhausted

// Enable connection leak detection
builder.Services.Configure<NpgsqlConnectionStringBuilderOptions>(options =>
{
    options.LogConnectionLeaks = true;
});
```

---

**Problem**: Performance degradation over time

**Cause**: Connections not returned to pool (leak)

**Solution**: Audit code for missing `using` statements:
```bash
# Find ExecuteAsync without 'using'
git grep -B5 "ExecuteAsync" | grep -v "using"
```

---

## Related

- [ADR-002: PostgreSQL for Subscription Persistence](./ADR-002-postgresql-persistence.md)
- [ADR-005: Dependency Injection and Service Lifetimes](./ADR-005-dependency-injection.md)

---

**References**:
- [Npgsql Connection Pooling](https://www.npgsql.org/doc/connection-string-parameters.html#pooling)
- [PostgreSQL Connection Limits](https://www.postgresql.org/docs/current/runtime-config-connection.html)

---

**Change Log**:
- 2025-12-19: Initial proposal
- 2025-12-20: Accepted after load testing validation
