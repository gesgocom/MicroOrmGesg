# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

MicroOrmGesg is a micro-ORM library for PostgreSQL built on top of Dapper and Npgsql. It provides a generic repository pattern with CRUD operations, pagination, filtering, JSONB support, and soft delete functionality. The library is packaged as a NuGet package (Gesgocom.MicroOrmGesg).

## Build Commands

### Building and Packaging
```bash
# Build and create NuGet package (Release configuration)
dotnet build --configuration Release

# Or use make (reads version from .csproj)
make build

# Show current version
make show-version
```

### Publishing to NuGet
```bash
# Publish current version
make push

# Publish specific version
make push-version V=1.2.3

# Increment patch version and publish (1.0.0 -> 1.0.1)
make release-patch

# Increment minor version and publish (1.0.0 -> 1.1.0)
make release-minor

# Increment major version and publish (1.0.0 -> 2.0.0)
make release-major
```

### Cleaning
```bash
make clean
dotnet clean MicroOrmGesg
```

## Architecture

### Core Components

1. **DbSession** (`Repository/DbSession.cs`)
   - Manages a single NpgsqlConnection and optional NpgsqlTransaction per scope
   - Designed to be registered as Scoped in DI (one per HTTP request)
   - Implements IDisposable and IAsyncDisposable
   - Key methods: `OpenAsync()`, `BeginTransactionAsync()`, `CommitAsync()`, `RollbackAsync()`
   - Always call `OpenAsync()` before using Connection or Transaction properties
   - Only one transaction per DbSession instance (throws if you try to nest)

2. **DataMicroOrmRepository<T>** (`Repository/DataMicroOrmRepository.cs`)
   - Generic repository providing CRUD operations for entities
   - All operations require an IDbSession parameter (dependency injection pattern)
   - Respects entity mapping attributes for table/column names
   - Handles JSONB serialization explicitly using NpgsqlDbType.Jsonb
   - All public methods accept CancellationToken for cooperative cancellation

3. **EntityMap** (`Utils/EntityMap.cs`)
   - Cached reflection-based mapping between C# entities and PostgreSQL tables
   - Resolves table names, column names, primary keys, and writable properties
   - Generates SQL fragments: `SelectColsCsv` (with aliases), `TableFullQuoted`
   - Detects soft delete columns automatically or via [SoftDelete] attribute
   - Thread-safe caching via lock on first access per type

4. **IDataFunctions** (`Interfaces/IDataFunctions.cs`, `Repository/DataFunctionsRepository.cs`)
   - Generic execution of PostgreSQL functions without coupling to entity types
   - `CallFunctionAsync<T>`: scalar or single row results (SELECT schema.func(...))
   - `CallFunctionListAsync<T>`: multiple rows (SELECT * FROM schema.func(...))
   - `CallVoidFunctionAsync`: functions with no return value
   - Accepts args as anonymous objects or IDictionary<string, object?>

### Mapping Attributes (`Attributes.cs`)

- **[Table("name", Schema = "schema")]**: Map entity to table (defaults to snake_case of class name)
- **[Column("column_name")]**: Explicit column name (defaults to snake_case of property name)
- **[Key]**: Primary key with auto-increment (serial/identity) - excluded from INSERT unless ExplicitKey
- **[ExplicitKey]**: Primary key with manual value - INCLUDED in INSERT
- **[Computed]**: Read-only column, excluded from INSERT/UPDATE
- **[Write(Include = false)]**: Exclude property from writes
- **[SoftDelete]**: Boolean column for soft delete (UPDATE instead of DELETE)
- **[Jsonb]**: Force JSONB handling for the property
- **[Required]**: Validation attribute (not null, not empty for strings)
- **[StringLength(min, max)]**: Validation attribute for string length

### JSONB Handling

**Write Operations (INSERT/UPDATE):**
- Properties marked with [Jsonb] or of type JObject/JArray/JToken are serialized to JSON strings
- Uses `NpgsqlJsonbParameter` (ICustomQueryParameter) to explicitly set NpgsqlDbType.Jsonb
- Ensures PostgreSQL treats the value as JSONB, not text

**Read Operations (SELECT):**
- Register Dapper type handlers in application startup:
  ```csharp
  SqlMapper.AddTypeHandler(new MicroOrmGesg.Utils.JObjectTypeHandler());
  SqlMapper.AddTypeHandler(new MicroOrmGesg.Utils.JArrayTypeHandler());
  SqlMapper.AddTypeHandler(new MicroOrmGesg.Utils.JTokenTypeHandler());
  ```
- Handlers automatically deserialize JSONB columns to JObject/JArray/JToken

## Important Patterns

### DbSession Usage Pattern
```csharp
// In a service or controller with injected IDbSession
await _db.OpenAsync(ct);
await _db.BeginTransactionAsync(ct: ct);
try
{
    // Execute operations using _db.Connection and _db.Transaction
    await _db.CommitAsync(ct);
}
catch
{
    await _db.RollbackAsync(ct);
    throw;
}
// Disposal handled by DI container at end of scope
```

### Dependency Injection Registration
```csharp
// NpgsqlDataSource as Singleton (connection pool)
services.AddSingleton(sp =>
{
    var cs = Configuration.GetConnectionString("ServidorSQL") ?? string.Empty;
    var dsBuilder = new NpgsqlDataSourceBuilder(cs);
    dsBuilder.UseNodaTime();
    return dsBuilder.Build();
});

// DbSession as Scoped
services.AddScoped<IDbSession, DbSession>();

// Generic repository
services.AddScoped(typeof(IDataMicroOrm<>), typeof(DataMicroOrmRepository<>));

// Functions executor
services.AddScoped<IDataFunctions, DataFunctionsRepository>();

// Health check (optional)
services.AddScoped<IDbHealthCheck, DbHealthCheck>();
```

### Column Name Resolution
When accepting column names from external input (orderBy, filterField, UpdateSetAsync), the repository uses a whitelist approach via `ResolveColumn`:
1. Tries to match C# property name (case-insensitive)
2. Tries to match snake_case of property name (case-insensitive)
3. Tries to match [Column("...")] attribute value (case-insensitive)
4. Returns null if no match (security: prevents SQL injection via column names)

All resolved column names are quoted with double quotes for PostgreSQL.

### Soft Delete Behavior
- If entity has [SoftDelete] attribute or property named "eliminado"/"is_deleted" (bool/bool?):
  - `DeleteAsync()` performs UPDATE setting column to true
  - `GetAllAsync()`, `CountAsync()`, `PageAsync()` filter out soft-deleted rows by default (unless `includeSoftDeleted=true`)
  - Column is excluded from WritableProps (cannot be manually updated via UpdateAsync/UpdateSetAsync)

### CancellationToken Propagation
- All async methods accept `CancellationToken ct = default`
- Always pass CancellationToken to Dapper via `CommandDefinition(..., cancellationToken: ct)`
- In ASP.NET Core controllers, use `HttpContext.RequestAborted` as the CancellationToken source

## Security Considerations

1. **SQL Injection Prevention**
   - All dynamic values are passed as Dapper parameters
   - Column names for ordering/filtering are whitelisted via ResolveColumn
   - Identifiers (table/column names) are quoted with double quotes

2. **Parameterization**
   - NEVER concatenate user input directly into SQL
   - Use DynamicParameters or anonymous objects with Dapper
   - For JSONB, use NpgsqlJsonbParameter to ensure type safety

3. **API Keys in Makefile**
   - The Makefile contains a NuGet API key hardcoded (line 6)
   - NEVER commit real API keys to version control
   - Use environment variables or secure key management instead

## Common Gotchas

1. **InvalidOperationException: "La sesión está cerrada"**
   - Always call `await _db.OpenAsync(ct)` before using `_db.Connection` or `_db.Transaction`

2. **"Ya existe una transacción activa"**
   - DbSession supports only one transaction at a time
   - Do not call BeginTransactionAsync twice on the same DbSession instance

3. **JSONB not deserializing on SELECT**
   - Ensure type handlers are registered in application startup (see JSONB Handling section)

4. **"No hay columnas válidas para actualizar con el patch proporcionado"**
   - UpdateSetAsync whitelist rejected all provided fields
   - Check that field names match property names, snake_case, or [Column] values
   - Primary key and soft delete columns are always excluded

5. **Primary Key Missing Error**
   - Every entity must have [Key] or [ExplicitKey] on one property
   - Use [Key] for auto-increment columns (serial/identity)
   - Use [ExplicitKey] for manually assigned PKs (GUIDs, composite keys managed externally)

## Testing Approach

When writing tests for this library:
- Use a real PostgreSQL instance or container (Testcontainers recommended)
- Test EntityMap caching and column resolution logic
- Verify JSONB round-trip (insert JObject, read back, assert equality)
- Test soft delete behavior (deleted items excluded by default)
- Test UpdateSetAsync whitelist rejection (PK, soft delete, non-existent columns)
- Test CancellationToken propagation (use CancellationTokenSource with timeout)

## Key Design Decisions

1. **Explicit JSONB handling in writes**: Avoids ambiguity with Npgsql's automatic type inference
2. **Single transaction per DbSession**: Simplifies transaction lifecycle and prevents nesting issues
3. **Whitelist-based column resolution**: Security-first approach to prevent SQL injection
4. **Convention over configuration**: Snake_case naming by default, attributes for exceptions
5. **CancellationToken everywhere**: First-class support for cooperative cancellation in ASP.NET Core
