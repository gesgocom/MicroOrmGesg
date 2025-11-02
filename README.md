# MicroOrmGesg — Guía de uso por temas

Este documento está organizado por temas con un índice navegable.

Tabla de contenidos
- [Introducción](#toc-introduccion)
- [Instalación y registro (DI)](#toc-instalacion-di)
- [DbSession (conexiones y transacciones)](#toc-dbsession)
    - [Características](#toc-db-caracteristicas)
    - [Registro en el contenedor](#toc-di-registro)
    - [Ejemplos de uso (servicio y controlador)](#toc-db-ejemplos)
    - [Métodos disponibles](#toc-db-metodos)
- [Repositorio genérico (Dapper + Npgsql)](#toc-repo-generico)
    - [Interfaz y firma de métodos](#toc-repo-interfaz)
    - [Comportamiento de cada método](#toc-repo-comportamiento)
- [Atributos y EntityMap](#toc-atributos-entitymap)
- [JSONB con Newtonsoft.Json](#toc-jsonb)
    - [Escritura (INSERT/UPDATE)](#toc-jsonb-escritura)
    - [Lectura (SELECT)](#toc-jsonb-lectura)
    - [Modelado](#toc-jsonb-modelado)
- [Paginación y conteo](#toc-paginacion)
- [Validación de modelos](#toc-validacion)
- [Ejecución genérica de funciones PostgreSQL](#toc-funciones)
- [Sistema de Migraciones](#toc-migraciones)
    - [Introducción y características](#toc-migraciones-intro)
    - [Configuración y registro](#toc-migraciones-config)
    - [Formato del script SQL](#toc-migraciones-formato)
    - [Ejecución de migraciones](#toc-migraciones-ejecucion)
    - [Checksums y detección de drift](#toc-migraciones-drift)
    - [Tabla journal](#toc-migraciones-journal)
    - [Buenas prácticas](#toc-migraciones-buenas-practicas)
- [Ejemplos prácticos](#toc-ejemplos)
- [Notas y buenas prácticas](#toc-notas)
- [Checklist rápido](#toc-checklist)
- [Historial de Cambios](#toc-historial)
- [Anexo (mejor explicado)](#anexo)

<a id="toc-introduccion"></a>
## Introducción
MicroOrmGesg es un micro ORM basado en Dapper y Npgsql que facilita operaciones CRUD, paginación, filtrado y manejo de JSONB en PostgreSQL. Incluye:
- **DbSession**: Gestión de conexiones y transacciones por unidad de trabajo
- **Repositorio genérico tipado**: CRUD completo con paginación, filtros y soft delete
- **Ejecución de funciones PostgreSQL**: Invocación genérica de funciones sin acoplamiento
- **Sistema de migraciones**: Migraciones idempotentes con checksums, advisory locks y detección de drift
- **Utilidades de mapeo**: Convenciones snake_case con atributos para excepciones
- **Soporte JSONB**: Serialización/deserialización nativa con Newtonsoft.Json

<a id="toc-instalacion-di"></a>
## Instalación y registro (DI)
Compila e instala el paquete según tu flujo (NuGet). Ejemplo de build local:
```
dotnet build --configuration Release
```
<a id="toc-di-registro"></a>
Registros típicos en Program.cs / Startup.cs:
```csharp
// NpgsqlDataSource como Singleton (pool de conexiones)
services.AddSingleton(sp =>
{
    var cs = Configuration.GetConnectionString("ServidorSQL") ?? string.Empty;
    var dsBuilder = new NpgsqlDataSourceBuilder(cs);
    dsBuilder.UseNodaTime();
    return dsBuilder.Build();
});

// DbSession como Scoped
services.AddScoped<IDbSession, DbSession>();

// Repositorio genérico por entidad
services.AddScoped(typeof(MicroOrmGesg.Interfaces.IDataMicroOrm<>), typeof(MicroOrmGesg.Repository.DataMicroOrmRepository<>));

// Ejecutor genérico de funciones PostgreSQL
services.AddScoped<MicroOrmGesg.Interfaces.IDataFunctions, MicroOrmGesg.Repository.DataFunctionsRepository>();

// Health check de base de datos (opcional)
services.AddScoped<MicroOrmGesg.Interfaces.IDbHealthCheck, MicroOrmGesg.Repository.DbHealthCheck>();

// Sistema de migraciones (opcional - ver sección Sistema de Migraciones)
services.AddPgMigrations(options =>
{
    options.AdvisoryLockKey = "myapp:migrations";
    options.DriftPolicy = DriftPolicy.WarnAndSkip;
    options.StopOnError = true;
});
```

### Comprobación de disponibilidad de BD (Health Check)
Puedes verificar si la base de datos está lista antes de iniciar sesión o arrancar la aplicación. Inyecta `IDbHealthCheck` y llama a `CheckAsync`.

Ejemplos:
```csharp
// En Program.cs, antes de arrancar (opcional, con timeout/cancelación)
using var scope = app.Services.CreateScope();
var hc = scope.ServiceProvider.GetRequiredService<MicroOrmGesg.Interfaces.IDbHealthCheck>();
var (okDefault, msgDefault) = await hc.CheckAsync(ct: CancellationToken.None);
if (!okDefault)
{
    app.Logger.LogError("Base de datos no disponible: {Msg}", msgDefault);
    return; // o Environment.Exit(1);
}

// En pantalla de login: validar con usuario/contraseña proporcionados
var (okUser, msgUser) = await hc.CheckAsync(username: userInput, password: passwordInput, ct: CancellationToken.None);
if (!okUser)
{
    ModelState.AddModelError("", $"No se pudo conectar a la BD: {msgUser}");
    // impedir login/arranque según tu flujo
}
```

`CheckAsync` devuelve:
- Ok = true cuando puede abrir conexión y ejecutar `SELECT 1`.
- Message = texto explicativo (incluye versión del servidor si OK, o detalle del error si falla).

<a id="toc-dbsession"></a>
## DbSession (conexiones y transacciones)
`DbSession` encapsula la gestión de una conexión y una transacción opcional por unidad de trabajo, pensado para ASP.NET Core y Npgsql.

<a id="toc-db-caracteristicas"></a>
### Características
- Scoped por request (si se registra como Scoped).
- Métodos asíncronos y síncronos.
- Control explícito de transacciones: BeginTransaction/Commit/Rollback.
- Libera recursos de forma segura (IDisposable/IAsyncDisposable).
- Integración directa con Npgsql.

<a id="toc-db-ejemplos"></a>
### Ejemplo de uso en un servicio
```csharp
public class PersonaService
{
    private readonly IDbSession _db;

    public PersonaService(IDbSession db)
    {
        _db = db;
    }

    public async Task CrearPersonaAsync(string nombre, CancellationToken ct = default)
    {
        await _db.OpenAsync(ct);
        await _db.BeginTransactionAsync(ct: ct);

        try
        {
            var sql = "INSERT INTO personas(nombre) VALUES (@nombre)";
            await _db.Connection!.ExecuteAsync(sql, new { nombre }, _db.Transaction);
            await _db.CommitAsync(ct);
        }
        catch
        {
            await _db.RollbackAsync(ct);
            throw;
        }
    }
}
```

<a id="toc-db-ejemplos"></a>
### Ejemplo de uso en un controlador
```csharp
[ApiController]
[Route("api/personas")]
public class PersonasController : ControllerBase
{
    private readonly IDbSession _db;

    public PersonasController(IDbSession db)
    {
        _db = db;
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        await _db.OpenAsync(ct);
        using var cmd = new CommandDefinition("SELECT * FROM personas WHERE id = @id", new { id }, cancellationToken: ct);
        var result = await _db.Connection!.QueryFirstOrDefaultAsync<object>(cmd);
        return result is null ? NotFound() : Ok(result);
    }
}
```

<a id="toc-db-metodos"></a>
### Métodos disponibles en DbSession
- OpenAsync/BeginTransactionAsync/CommitAsync/RollbackAsync/DisposeAsync… (ver secciones superiores para detalles y firmas completas).

<a id="toc-repo-generico"></a>
## Repositorio genérico (Dapper + Npgsql)
Interfaz principal tipada por entidad que ofrece CRUD, listado, filtro, paginación y conteo.

<a id="toc-repo-interfaz"></a>
### Interfaz (resumen)
- GetByIdAsync
- GetAllAsync
- InsertAsync / InsertAsyncReturnId
- UpdateAsync
- DeleteAsync
- CountAsync
- PageAsync

<a id="toc-repo-comportamiento"></a>
### Comportamiento de cada método
- Respeta columnas ignoradas, PK, soft delete y atributos de mapeo; usa parámetros para evitar SQL injection; propaga CancellationToken.

<a id="toc-atributos-entitymap"></a>
## Atributos y EntityMap
Soporta atributos como [Table], [Column], [Key], [Ignore], [Jsonb], etc., además de convenciones de nombre (snake_case) mediante EntityMap.

<a id="toc-jsonb"></a>
## JSONB con Newtonsoft.Json
<a id="toc-jsonb-escritura"></a>
### Escritura (INSERT/UPDATE)
Para propiedades [Jsonb] o tipos JToken/JObject/JArray, en escritura se usa `NpgsqlParameter` con `NpgsqlDbType.Jsonb`:
```csharp
var jsonString = value != null ? JsonConvert.SerializeObject(value) : null;
var np = new NpgsqlParameter(paramName, NpgsqlDbType.Jsonb)
{
    Value = (object?)jsonString ?? DBNull.Value
};
dp.Add(paramName, np);
```
<a id="toc-jsonb-lectura"></a>
### Lectura (SELECT)
Mantén los TypeHandlers para deserializar automáticamente a JObject/JArray/JToken.

<a id="toc-jsonb-modelado"></a>
### Modelado
- Propiedades complejas pueden mapearse a jsonb. Puedes pasar JObject/JArray/JToken o un objeto serializable si marcas con [Jsonb].

<a id="toc-paginacion"></a>
## Paginación y conteo
- PageAsync devuelve datos y total; CountAsync permite conteos simples con filtros seguros.

<a id="toc-validacion"></a>
## Validación de modelos
- Atributos de validación en modelos y utilidades para comprobar reglas antes de persistir.

<a id="toc-funciones"></a>
## Ejecución genérica de funciones PostgreSQL
Usa IDataFunctions para invocar funciones sin acoplar a una entidad T.
```csharp
public interface IDataFunctions
{
    Task<TResult?> CallFunctionAsync<TResult>(IDbSession session, string functionName, object? args = null, string? schema = null, CancellationToken ct = default);
    Task<List<TResult>> CallFunctionListAsync<TResult>(IDbSession session, string functionName, object? args = null, string? schema = null, CancellationToken ct = default);
    Task CallVoidFunctionAsync(IDbSession session, string functionName, object? args = null, string? schema = null, CancellationToken ct = default);
}
```
Reglas:
- Escalar/un solo registro: `SELECT schema.func(@p1, ...)`.
- Conjuntos (TABLE/SETOF): `SELECT * FROM schema.func(@p1, ...)`.
- `args` puede ser anónimo o IDictionary<string, object?>.
  Ejemplo:
```csharp
await _db.OpenAsync(ct);
string? token = await _funcs.CallFunctionAsync<string>(_db, "generar_token_recuperacion", new { p_idusuario = 1, p_ip_solicitud = "1.2.3.4", p_duracion_horas = 24 }, schema: null, ct);

public sealed class ValidacionDto { public bool Valido { get; set; } public int? IdUsuario { get; set; } public string? Mensaje { get; set; } public int Intentos { get; set; } }
var lista = await _funcs.CallFunctionListAsync<ValidacionDto>(_db, "validar_token_recuperacion", new { p_token = token }, schema: null, ct);
await _funcs.CallVoidFunctionAsync(_db, "incrementar_intento_token", new { p_token = token }, ct: ct);
bool ok = await _funcs.CallFunctionAsync<bool>(_db, "usar_token_cambiar_password", new { p_token = token, p_nuevo_password_hash = hash, p_ip_uso = "1.2.3.4" }, schema: null, ct) ?? false;
int? eliminados = await _funcs.CallFunctionAsync<int>(_db, "limpiar_tokens_expirados", ct: ct);
```

<a id="toc-migraciones"></a>
## Sistema de Migraciones

<a id="toc-migraciones-intro"></a>
### Introducción y características

MicroOrmGesg incluye un sistema completo de migraciones para PostgreSQL con las siguientes características:

- **Idempotencia**: Las migraciones pueden ejecutarse múltiples veces de forma segura
- **Checksums SHA-256**: Detecta cambios en scripts ya aplicados (drift)
- **Advisory Locks**: Previene ejecuciones concurrentes en despliegues multi-instancia
- **Transacciones por paso**: Cada migración se ejecuta en su propia transacción (rollback individual)
- **@check directives**: Verificación SQL personalizada para determinar si un paso ya está aplicado
- **Journal table**: Registro completo de todas las migraciones ejecutadas con timestamps y duración
- **PostgreSQL-aware SQL parsing**: Soporta dollar quoting ($$), comentarios y literales de cadena
- **Logging integrado**: Usa ILogger<T> para trazabilidad completa
- **Independiente de DbSession**: Usa NpgsqlDataSource directamente (diseñado para startup, no requests)

<a id="toc-migraciones-config"></a>
### Configuración y registro

Registra el sistema de migraciones en `Program.cs`:

```csharp
// 1. NpgsqlDataSource (requerido, debe estar registrado antes)
services.AddSingleton(sp =>
{
    var cs = Configuration.GetConnectionString("PostgreSQL") ?? string.Empty;
    var dsBuilder = new NpgsqlDataSourceBuilder(cs);
    dsBuilder.UseNodaTime(); // Opcional
    return dsBuilder.Build();
});

// 2. Sistema de migraciones
services.AddPgMigrations(options =>
{
    // Clave única para advisory lock (previene ejecuciones concurrentes)
    options.AdvisoryLockKey = "myapp:migrations:v1";

    // Timeout para comandos SQL individuales (en segundos)
    options.CommandTimeoutSeconds = 120;

    // Política de manejo de drift (ver sección Checksums y drift)
    options.DriftPolicy = DriftPolicy.WarnAndSkip;

    // Detener al primer error o continuar con siguientes pasos
    options.StopOnError = true;

    // Nombre de la tabla journal (default: __micro_orm_migrations)
    options.JournalTableName = "__micro_orm_migrations";

    // Schema de la tabla journal (default: public)
    options.JournalSchema = null;
});
```

**Opciones de DriftPolicy:**
- `Fail`: Lanza excepción si el checksum difiere
- `WarnAndSkip` (default): Registra warning y omite re-ejecución
- `Reapply`: Re-ejecuta el paso aunque @check devuelva true (útil para funciones/vistas)

<a id="toc-migraciones-formato"></a>
### Formato del script SQL

Crea un archivo SQL (p. ej., `scripts/schema.sql`) con tus migraciones usando directivas `@step` y `@check`:

```sql
-- Ejemplo 1: Tabla simple con IF NOT EXISTS (sin @check necesario)
-- @step id:001 name:create.users
CREATE TABLE IF NOT EXISTS users(
  id serial PRIMARY KEY,
  username text NOT NULL UNIQUE,
  email text NOT NULL UNIQUE,
  password_hash text NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  eliminado boolean NOT NULL DEFAULT false
);

-- Ejemplo 2: Índice con IF NOT EXISTS (sin @check necesario)
-- @step id:002 name:index.users.email
CREATE INDEX IF NOT EXISTS idx_users_email ON users(email) WHERE eliminado = false;

-- Ejemplo 3: Función con CREATE OR REPLACE (sin @check necesario)
-- @step id:003 name:function.update_updated_at
CREATE OR REPLACE FUNCTION update_updated_at_column()
RETURNS TRIGGER AS $$
BEGIN
  NEW.updated_at = now();
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- Ejemplo 4: ALTER COLUMN sin IF NOT EXISTS (CON @check útil)
-- @step id:004 name:alter.users.email_type
-- @check SELECT EXISTS(
--   SELECT 1 FROM information_schema.columns
--   WHERE table_name='users' AND column_name='email'
--     AND data_type='character varying' AND character_maximum_length=255
-- );
ALTER TABLE users ALTER COLUMN email TYPE varchar(255);

-- Ejemplo 5: Inserción de datos inicial (CON @check útil)
-- @step id:005 name:insert.admin.user
-- @check SELECT EXISTS(SELECT 1 FROM users WHERE username = 'admin');
INSERT INTO users(username, email, password_hash, eliminado)
VALUES('admin', 'admin@example.com', 'hash_aqui', false);
```

**Formato de directivas:**

- **@step id:XXX name:YYYY**: Define un paso de migración
  - `id`: Identificador único (alfanumérico, puede incluir guiones). Usar secuencial: 001, 002, 003...
  - `name`: Descripción del paso (usar puntos para namespace: create.users, alter.users.add_column)

- **@check SQL_QUERY**: Verificación opcional que debe devolver boolean
  - SQL que devuelve `true` si el paso YA está aplicado, `false` si NO está aplicado
  - Se ejecuta ANTES del SQL del paso para decidir si es necesario ejecutarlo
  - **Casos de uso principales**:
    1. **Adoptar esquemas pre-existentes**: Si tu BD ya tiene tablas creadas manualmente
    2. **Operaciones sin IF NOT EXISTS**: ALTER COLUMN, constraints antiguos
    3. **Validaciones específicas**: Verificar tipos de columnas, parámetros de funciones
  - **NO es necesario** cuando usas `IF NOT EXISTS` (ya es idempotente)
  - Puede usar múltiples líneas (todas las líneas después de @check hasta el SQL del paso)

**SQL soportado:**
- Múltiples sentencias por paso (separadas por `;`)
- Dollar quoting: `$$` o `$tag$...$tag$`
- Comentarios: `--` (línea) y `/* ... */` (bloque)
- Literales de cadena con escape: `'texto''con''comillas'`

<a id="toc-migraciones-ejecucion"></a>
### Ejecución de migraciones

**Opción 1: Ejecución manual en Program.cs**

```csharp
var app = builder.Build();

// Ejecutar migraciones ANTES de iniciar la aplicación
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var migrator = scope.ServiceProvider.GetRequiredService<IPgMigrator>();

    var scriptPath = Path.Combine(AppContext.BaseDirectory, "scripts", "schema.sql");
    var source = new FileMigrationSource(scriptPath);

    logger.LogInformation("Ejecutando migraciones de base de datos...");

    try
    {
        var result = await migrator.RunAsync(source, CancellationToken.None);

        if (result.IsSuccess)
        {
            logger.LogInformation(
                "Migraciones completadas: {Applied} aplicadas, {Skipped} omitidas en {Duration:N2}s",
                result.StepsApplied, result.StepsSkipped, result.TotalDuration.TotalSeconds);
        }
        else
        {
            logger.LogError(
                "Migraciones fallidas: {Failed} pasos fallaron de {Total}",
                result.StepsFailed, result.Steps.Count);

            // Mostrar detalles de pasos fallidos
            foreach (var step in result.Steps.Where(s => !s.Success))
            {
                logger.LogError("Paso {StepId} ({Name}) falló: {Message}",
                    step.StepId, step.StepName, step.Message);
            }

            // Opcional: detener la aplicación
            Environment.Exit(1);
        }

        if (result.StepsWithDrift > 0)
        {
            logger.LogWarning("Drift detectado en {Count} paso(s)", result.StepsWithDrift);
        }
    }
    catch (FileNotFoundException ex)
    {
        logger.LogWarning("Archivo de migraciones no encontrado: {Path}", ex.FileName);
        // Opcional: continuar sin migraciones si es aceptable
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error fatal durante migraciones");
        Environment.Exit(1);
    }
}

// Iniciar aplicación
app.MapGet("/", () => "Aplicación iniciada correctamente!");
await app.RunAsync();
```

**Opción 2: IHostedService automático**

Crea un servicio hosteado para ejecutar migraciones al arranque:

```csharp
public class MigrationHostedService : IHostedService
{
    private readonly IPgMigrator _migrator;
    private readonly ILogger<MigrationHostedService> _logger;
    private readonly IHostApplicationLifetime _appLifetime;

    public MigrationHostedService(
        IPgMigrator migrator,
        ILogger<MigrationHostedService> logger,
        IHostApplicationLifetime appLifetime)
    {
        _migrator = migrator;
        _logger = logger;
        _appLifetime = appLifetime;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Iniciando migraciones de base de datos...");

        try
        {
            var scriptPath = Path.Combine(AppContext.BaseDirectory, "scripts", "schema.sql");
            var source = new FileMigrationSource(scriptPath);
            var result = await _migrator.RunAsync(source, cancellationToken);

            if (!result.IsSuccess)
            {
                _logger.LogError("Migraciones fallidas. Deteniendo aplicación.");
                _appLifetime.StopApplication();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fatal durante migraciones");
            _appLifetime.StopApplication();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

// Registrar en Program.cs
builder.Services.AddHostedService<MigrationHostedService>();
```

**Opción 3: Aplicación de consola (CI/CD)**

```csharp
var services = new ServiceCollection();
services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));

var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")!;
services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
services.AddPgMigrations(options => { options.StopOnError = true; });

var provider = services.BuildServiceProvider();
var migrator = provider.GetRequiredService<IPgMigrator>();
var source = new FileMigrationSource(args[0]); // Ruta desde argumentos

var result = await migrator.RunAsync(source);
return result.IsSuccess ? 0 : 1;
```

<a id="toc-migraciones-drift"></a>
### Checksums y detección de drift

El sistema calcula un **checksum SHA-256** del SQL de cada paso y lo almacena en la tabla journal. Esto permite detectar "drift" (cambios en scripts ya aplicados).

**Flujo de decisión por paso:**

1. **Leer journal**: Verificar si el paso ya fue aplicado
2. **Comparar checksums**:
   - Si coincide y `success=true` → **Omitir** (ya aplicado)
   - Si difiere → **Drift detectado** (el SQL cambió)
3. **Ejecutar @check** (si existe):
   - Si devuelve `false` → **Ejecutar SQL** del paso
   - Si devuelve `true` → El paso ya está aplicado en BD
4. **Aplicar política de drift** si hay diferencia de checksum:
   - `Fail`: Lanzar excepción (fuerza que se resuelva manualmente)
   - `WarnAndSkip`: Registrar warning en journal y omitir
   - `Reapply`: Re-ejecutar el SQL (útil para funciones que pueden recrearse)

**Ejemplo de drift:**

```sql
-- Script original (aplicado)
-- @step id:001 name:create.users
CREATE TABLE users(id serial PRIMARY KEY);

-- Luego modificas el script (DRIFT!)
-- @step id:001 name:create.users
CREATE TABLE users(
  id serial PRIMARY KEY,
  email text NOT NULL  -- ← Columna nueva
);
```

El checksum cambiará y se detectará drift. **Solución correcta**: No modificar pasos aplicados, sino crear uno nuevo:

```sql
-- @step id:001 name:create.users
CREATE TABLE users(id serial PRIMARY KEY);

-- @step id:002 name:alter.users.add_email
-- @check SELECT EXISTS(
--   SELECT 1 FROM information_schema.columns
--   WHERE table_name='users' AND column_name='email'
-- );
ALTER TABLE users ADD COLUMN IF NOT EXISTS email text NOT NULL DEFAULT '';
```

<a id="toc-migraciones-journal"></a>
### Tabla journal

El sistema crea automáticamente la tabla `__micro_orm_migrations` (nombre configurable) con la siguiente estructura:

```sql
CREATE TABLE __micro_orm_migrations(
  step_id text PRIMARY KEY,           -- ID del paso (001, 002, ...)
  step_name text NOT NULL,            -- Nombre descriptivo
  checksum text NOT NULL,             -- SHA-256 del SQL
  applied_at timestamptz NOT NULL,    -- Timestamp de ejecución
  duration_ms int NOT NULL,           -- Duración en milisegundos
  success boolean NOT NULL,           -- true=éxito, false=fallo
  message text NULL                   -- Error o advertencia
);
```

**Consultas útiles:**

```sql
-- Ver todas las migraciones aplicadas
SELECT step_id, step_name, applied_at, duration_ms, success
FROM __micro_orm_migrations
ORDER BY step_id;

-- Verificar drift (pasos con mensaje de advertencia)
SELECT step_id, step_name, message
FROM __micro_orm_migrations
WHERE message LIKE '%Drift%';

-- Pasos fallidos
SELECT step_id, step_name, message
FROM __micro_orm_migrations
WHERE success = false;

-- Última migración aplicada
SELECT step_id, step_name, applied_at
FROM __micro_orm_migrations
WHERE success = true
ORDER BY applied_at DESC
LIMIT 1;
```

<a id="toc-migraciones-buenas-practicas"></a>
### Buenas prácticas

1. **Usar IF NOT EXISTS siempre que sea posible**
   ```sql
   CREATE TABLE IF NOT EXISTS users(...);
   CREATE INDEX IF NOT EXISTS idx_users_email ON users(email);
   ALTER TABLE posts ADD COLUMN IF NOT EXISTS view_count int DEFAULT 0;
   ```

2. **Cuándo usar (y NO usar) @check**

   **❌ NO usar @check cuando tienes IF NOT EXISTS (redundante):**
   ```sql
   -- MAL: @check es innecesario aquí
   -- @step id:001 name:create.users
   -- @check SELECT to_regclass('public.users') IS NOT NULL;
   CREATE TABLE IF NOT EXISTS users(id serial PRIMARY KEY);

   -- BIEN: Solo IF NOT EXISTS es suficiente
   -- @step id:001 name:create.users
   CREATE TABLE IF NOT EXISTS users(id serial PRIMARY KEY);
   ```

   **✅ Caso 1: Verificar OBJETOS de base de datos (tablas, índices, funciones)**

   Usa `to_regclass` para tablas, `pg_indexes` para índices, `pg_proc` para funciones:
   ```sql
   -- Verificar si una tabla existe (útil para adopción de esquemas)
   -- @step id:001 name:create.users
   -- @check SELECT to_regclass('public.users') IS NOT NULL;
   CREATE TABLE users(id serial PRIMARY KEY);  -- Sin IF NOT EXISTS

   -- Verificar si un índice existe (PostgreSQL < 14 sin IF NOT EXISTS)
   -- @step id:002 name:create.idx_users_email
   -- @check SELECT EXISTS(
   --   SELECT 1 FROM pg_indexes
   --   WHERE schemaname='public' AND indexname='idx_users_email'
   -- );
   CREATE INDEX idx_users_email ON users(email);

   -- Verificar si una función existe con firma específica
   -- @step id:003 name:create.calculate_total
   -- @check SELECT EXISTS(
   --   SELECT 1 FROM pg_proc p
   --   JOIN pg_namespace n ON p.pronamespace = n.oid
   --   WHERE n.nspname='public' AND p.proname='calculate_total'
   -- );
   CREATE FUNCTION calculate_total() RETURNS int AS $$ ... $$;
   ```

   **✅ Caso 2: Verificar ESTRUCTURA de objetos (tipos, constraints)**

   Usa `information_schema.columns` para verificar columnas y tipos:
   ```sql
   -- Verificar que una columna existe con tipo específico
   -- @step id:004 name:alter.users.email_varchar
   -- @check SELECT EXISTS(
   --   SELECT 1 FROM information_schema.columns
   --   WHERE table_name='users' AND column_name='email'
   --     AND data_type='character varying'
   --     AND character_maximum_length=255
   -- );
   ALTER TABLE users ALTER COLUMN email TYPE varchar(255);

   -- Verificar que un constraint existe
   -- @step id:005 name:add.users.email_check
   -- @check SELECT EXISTS(
   --   SELECT 1 FROM pg_constraint
   --   WHERE conname='users_email_valid' AND conrelid='users'::regclass
   -- );
   ALTER TABLE users ADD CONSTRAINT users_email_valid
     CHECK (email LIKE '%@%');
   ```

   **✅ Caso 3: Verificar DATOS (migración de datos)**

   Usa `SELECT EXISTS` para verificar si ya existen datos específicos:
   ```sql
   -- Insertar datos de configuración inicial SOLO si no existen
   -- @step id:006 name:insert.config.defaults
   -- @check SELECT EXISTS(
   --   SELECT 1 FROM config WHERE key = 'app.version'
   -- );
   INSERT INTO config(key, value) VALUES('app.version', '1.0.0');

   -- Insertar usuario admin SOLO si no existe
   -- @step id:007 name:insert.admin.user
   -- @check SELECT EXISTS(
   --   SELECT 1 FROM users WHERE username = 'admin'
   -- );
   INSERT INTO users(username, email, role)
   VALUES('admin', 'admin@example.com', 'ADMIN');
   ```

   **Diferencias clave:**
   - `to_regclass('tabla')` → Verifica si el OBJETO tabla existe
   - `EXISTS(SELECT 1 FROM tabla WHERE ...)` → Verifica si existen DATOS en la tabla
   - `information_schema.columns` → Verifica ESTRUCTURA/configuración de columnas

   **Tabla de decisión rápida:**

   | Tu SQL contiene... | ¿Necesitas @check? | Razón |
   |-------------------|-------------------|-------|
   | `CREATE TABLE IF NOT EXISTS` | ❌ NO | Ya es idempotente |
   | `CREATE INDEX IF NOT EXISTS` | ❌ NO | Ya es idempotente |
   | `CREATE OR REPLACE FUNCTION` | ❌ NO | Ya es idempotente |
   | `ALTER TABLE ADD COLUMN IF NOT EXISTS` | ❌ NO | Ya es idempotente |
   | `ALTER TABLE ALTER COLUMN TYPE` | ✅ SÍ | No tiene IF NOT EXISTS |
   | `ALTER TABLE ADD CONSTRAINT` | ✅ SÍ | PostgreSQL < 16 no tiene IF NOT EXISTS |
   | `CREATE TRIGGER` | ✅ SÍ | No tiene IF NOT EXISTS |
   | `INSERT INTO ... VALUES` | ✅ SÍ | Para evitar duplicados |
   | Migración a BD existente | ✅ SÍ | Para adoptar objetos pre-existentes |

   **Regla de oro:** Si tu SQL tiene `IF NOT EXISTS` o `CREATE OR REPLACE`, NO necesitas `@check`

   **Prompt para LLMs: Generación automática de @check**

   Si tienes varias sentencias SQL y necesitas generar los `@check` correspondientes, puedes usar este prompt con cualquier LLM (Claude, GPT-4, etc.):

   ```
   Tengo las siguientes sentencias SQL de PostgreSQL y necesito generar las directivas @check correspondientes para un sistema de migraciones idempotentes.

   REGLAS:
   1. Si la sentencia tiene IF NOT EXISTS, CREATE OR REPLACE, o ADD COLUMN IF NOT EXISTS → NO generar @check (ya es idempotente)
   2. Para CREATE TABLE sin IF NOT EXISTS → usar to_regclass('schema.tabla')
   3. Para CREATE INDEX sin IF NOT EXISTS → usar pg_indexes con schemaname e indexname
   4. Para CREATE FUNCTION sin CREATE OR REPLACE → usar pg_proc con proname
   5. Para ALTER TABLE ALTER COLUMN TYPE → usar information_schema.columns para verificar tipo y longitud
   6. Para ALTER TABLE ADD CONSTRAINT → usar pg_constraint con conname
   7. Para CREATE TRIGGER → usar pg_trigger con tgname
   8. Para INSERT INTO → usar EXISTS(SELECT 1 FROM tabla WHERE condicion_unica)
   9. Para migraciones de datos (UPDATE, DELETE) → usar EXISTS con condición que verifique el resultado esperado

   FORMATO DE SALIDA:
   Para cada sentencia SQL, genera:
   -- @step id:XXX name:descripcion.del.paso
   -- @check SELECT ...;  (SOLO si es necesario según las reglas)
   [SQL original aquí]

   SENTENCIAS SQL A PROCESAR:
   [Pega aquí tus sentencias SQL]
   ```

   **Ejemplo de uso del prompt:**

   ```
   [Copias el prompt anterior y añades tus sentencias SQL]

   SENTENCIAS SQL A PROCESAR:
   CREATE TABLE usuarios(
     id serial PRIMARY KEY,
     username text NOT NULL UNIQUE
   );

   CREATE INDEX idx_usuarios_username ON usuarios(username);

   CREATE OR REPLACE FUNCTION update_timestamp()
   RETURNS TRIGGER AS $$
   BEGIN
     NEW.updated_at = now();
     RETURN NEW;
   END;
   $$ LANGUAGE plpgsql;

   ALTER TABLE usuarios ALTER COLUMN username TYPE varchar(100);

   INSERT INTO usuarios(username) VALUES('admin');
   ```

   **Salida esperada del LLM:**

   ```sql
   -- @step id:001 name:create.usuarios
   -- @check SELECT to_regclass('public.usuarios') IS NOT NULL;
   CREATE TABLE usuarios(
     id serial PRIMARY KEY,
     username text NOT NULL UNIQUE
   );

   -- @step id:002 name:index.usuarios.username
   -- @check SELECT EXISTS(
   --   SELECT 1 FROM pg_indexes
   --   WHERE schemaname='public' AND indexname='idx_usuarios_username'
   -- );
   CREATE INDEX idx_usuarios_username ON usuarios(username);

   -- @step id:003 name:function.update_timestamp
   CREATE OR REPLACE FUNCTION update_timestamp()
   RETURNS TRIGGER AS $$
   BEGIN
     NEW.updated_at = now();
     RETURN NEW;
   END;
   $$ LANGUAGE plpgsql;

   -- @step id:004 name:alter.usuarios.username_varchar
   -- @check SELECT EXISTS(
   --   SELECT 1 FROM information_schema.columns
   --   WHERE table_schema='public' AND table_name='usuarios'
   --     AND column_name='username'
   --     AND data_type='character varying'
   --     AND character_maximum_length=100
   -- );
   ALTER TABLE usuarios ALTER COLUMN username TYPE varchar(100);

   -- @step id:005 name:insert.usuarios.admin
   -- @check SELECT EXISTS(SELECT 1 FROM usuarios WHERE username = 'admin');
   INSERT INTO usuarios(username) VALUES('admin');
   ```

3. **NUNCA modificar un paso ya aplicado en producción**
   - Los checksums detectarán el cambio
   - Crear un nuevo paso en su lugar
   - Facilita auditoría y rollback mental

4. **Un cambio lógico por paso**
   - Mejor: `001-create-users`, `002-index-users-email`
   - Evitar: un paso que crea 10 tablas y 20 índices
   - Facilita debugging y permite rollback granular

5. **IDs secuenciales y descriptivos**
   ```sql
   -- Bueno
   -- @step id:001 name:create.users
   -- @step id:002 name:create.posts
   -- @step id:003 name:index.users.email

   -- También válido
   -- @step id:001-create-users name:create.users
   -- @step id:002-create-posts name:create.posts
   ```

6. **Testear en copia de producción**
   - Ejecutar migraciones en staging/pre-prod primero
   - Verificar tiempos de ejecución con volúmenes reales
   - Validar que índices se crean correctamente

7. **Incluir el archivo SQL en el proyecto**
   ```xml
   <!-- En el .csproj -->
   <ItemGroup>
     <None Update="scripts\schema.sql">
       <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
     </None>
   </ItemGroup>
   ```

8. **Advisory locks automáticos**
   - En despliegues multi-instancia, solo una ejecuta migraciones
   - Otras esperan automáticamente
   - No requiere configuración adicional

9. **Logs detallados**
   - Nivel Information: resumen de ejecución
   - Nivel Debug: cada paso y SQL ejecutado
   - Nivel Trace: SQL individual de cada sentencia

10. **Funciones y vistas con CREATE OR REPLACE**
    ```sql
    -- @step id:010 name:function.calculate_total
    -- @check SELECT EXISTS(SELECT 1 FROM pg_proc WHERE proname='calculate_total');
    CREATE OR REPLACE FUNCTION calculate_total(...)
    RETURNS ... AS $$ ... $$ LANGUAGE plpgsql;
    ```
    Usar `DriftPolicy.Reapply` si quieres que se actualicen automáticamente

**Ejemplo completo de flujo de trabajo:**

Ver `examples/schema.sql` y `examples/MigrationUsageExample.cs` para ejemplos completos con:
- 10 pasos de migración (tablas, índices, funciones, triggers, alteraciones)
- 3 patrones de ejecución (manual, IHostedService, consola)
- Manejo de errores y logging
- Validaciones @check para diferentes casos de uso

<a id="toc-ejemplos"></a>
## Ejemplos prácticos
- Updates parciales y columnas específicas (PATCH): uso de DTO o Dictionary, exclusión de PK/soft delete y mapeo flexible de nombres.
- Controladores ASP.NET Core con CancellationToken.

<a id="toc-notas"></a>
## Notas y buenas prácticas
- Usa CancellationToken (`HttpContext.RequestAborted`) y propágalo.
- Maneja jsonb explícitamente en escritura (NpgsqlDbType.Jsonb).
- Asegura filtros parametrizados y sanitización al construir SQL dinámico.

<a id="toc-checklist"></a>
## Checklist rápido
- DataSource registrado como Singleton; DbSession como Scoped.
- TypeHandlers de Newtonsoft para JObject/JArray/JToken.
- Atributos de mapeo correctos ([Table], [Column], [Key], [Ignore], [Jsonb]).
- Propagación de CancellationToken en todos los métodos asíncronos.
- Repositorios y ejecutor de funciones registrados en DI.
- Sistema de migraciones configurado (opcional) con advisory lock key único.
- Script SQL de migraciones incluido en el proyecto con CopyToOutputDirectory.
- Migraciones ejecutadas al inicio de la aplicación (antes de procesar requests).

<a id="toc-historial"></a>
## Historial de Cambios (resumen)
- **v1.0.1**: Sistema de migraciones completo con checksums SHA-256, advisory locks, detección de drift y journal table.
- CancellationToken en API pública (cooperación con ASP.NET Core).
- JSONB explícito en escritura (NpgsqlDbType.Jsonb) manteniendo lectura con TypeHandlers.
- Ejecución genérica de funciones PostgreSQL mediante IDataFunctions.




---

<a id="anexo"></a>
# Anexo — Guía completa y detallada (profundización por temas)

Enlaces rápidos a temas
- [Introducción](#toc-introduccion)
- [Instalación y registro (DI)](#toc-instalacion-di)
- [DbSession](#toc-dbsession)
- [Repositorio genérico](#toc-repo-generico)
- [Atributos y EntityMap](#toc-atributos-entitymap)
- [JSONB](#toc-jsonb)
- [Paginación y conteo](#toc-paginacion)
- [Validación de modelos](#toc-validacion)
- [Funciones PostgreSQL](#toc-funciones)
- [Ejemplos](#toc-ejemplos)
- [Notas y buenas prácticas](#toc-notas)
- [Checklist](#toc-checklist)
- [Historial](#toc-historial)

Este anexo amplía en profundidad cada tema del índice principal. Mantiene la estructura temática, añade explicaciones exhaustivas, ejemplos prácticos, firmas exactas, decisiones de diseño, gotchas, FAQ, rendimiento y seguridad. Todos los fragmentos reflejan el código actual del repositorio.

Contenido del anexo
- Conceptos y objetivos del micro ORM
- Infraestructura de acceso a datos: DbSession
    - Ciclo de vida, transacciones, disposiciones
    - Firmas exactas e invariantes
    - Patrones de uso en ASP.NET Core
    - Errores comunes y troubleshooting
- Repositorio genérico DataMicroOrmRepository<T>
    - Contrato (IDataMicroOrm<T>) con firmas exactas
    - Inserción, actualización, borrado (soft/duro)
    - Lectura: GetById/GetAll, orden, filtros y paginación
    - UpdateSet (parches parciales y columnas específicas)
    - Generación de SQL segura (whitelist/alias/quoting)
    - Ejemplos end-to-end
- Atributos de mapeo y convenciones (Attributes.cs + EntityMap)
    - [Table], [Key], [ExplicitKey], [Computed], [Write], [Column], [SoftDelete], [Jsonb]
    - Reglas de escritura y selección
    - Detección de soft delete y nombres convencionales
- JSONB con Newtonsoft.Json y Dapper
    - Handlers para JToken/JObject/JArray (lectura)
    - Escritura explícita como jsonb
    - Modelado con DTOs tipados vs dinámicos
- Paginación y conteo: Page<T>, CountAsync, PageAsync
- Validación de modelos: [Required], [StringLength] y ModelValidator
- Ejecución genérica de funciones PostgreSQL (IDataFunctions)
    - Escalares, conjuntos (TABLE/SETOF) y void
    - Normalización de parámetros
- Seguridad, rendimiento y buenas prácticas
    - Cancelación (CancellationToken) en toda la pila
    - Sanitización de columnas y parámetros
    - Pooling, transacciones y aislamiento
- Migraciones y evolución de esquemas
- Pruebas (ideas, patrones de test)
- FAQ (preguntas frecuentes)
- Guía de resolución de problemas (Troubleshooting)


## Conceptos y objetivos del micro ORM

MicroOrmGesg busca el equilibrio entre:
- Control explícito del SQL (Dapper) y
- Conveniencias típicas de un ORM (mapeo de entidades, CRUD genérico, paginación, filtros, soft delete, JSONB),
  con un coste cognitivo y de mantenimiento reducido.

Decisiones clave:
- Mapeo por convención con posibilidad de anotar excepciones por atributos.
- Seguridad ante inyecciones a través de parámetros y whitelists de columnas.
- Compatibilidad nativa con PostgreSQL (Npgsql) y JSONB (Newtonsoft.Json + Dapper).
- APIs explícitas que propagan CancellationToken.


## Infraestructura de acceso a datos: DbSession

DbSession encapsula una única conexión y una transacción opcional por scope (generalmente una petición HTTP en ASP.NET Core). Implementa IDisposable e IAsyncDisposable para liberar recursos de forma segura.

Firmas exactas (IDbSession):
```csharp
public interface IDbSession : IAsyncDisposable, IDisposable
{
    NpgsqlConnection? Connection { get; }
    NpgsqlTransaction? Transaction { get; }

    Task<NpgsqlConnection> OpenAsync(CancellationToken ct = default);
    Task BeginTransactionAsync(IsolationLevel isolation = IsolationLevel.ReadCommitted, CancellationToken ct = default);
    Task CommitAsync(CancellationToken ct = default);
    Task RollbackAsync(CancellationToken ct = default);
    void Close();
}
```

Comportamiento y ciclo de vida (DbSession.cs):
- OpenAsync abre la conexión desde NpgsqlDataSource (pool). Si ya está abierta, reutiliza.
- BeginTransactionAsync inicia una transacción (única por scope) y lanza si ya hay una activa.
- CommitAsync confirma y libera la transacción; RollbackAsync revierte si existe, y libera.
- Close cierra y dispone la conexión si está abierta; es opcional llamarlo manualmente.
- Dispose/DisposeAsync liberan transacción y conexión; el contenedor DI suele gestionarlo al final del scope.

Buenas prácticas:
- Registrar NpgsqlDataSource como Singleton y DbSession como Scoped.
- Abrir conexión lo más tarde posible, cerrar/liberar al terminar.
- En escenarios con alta concurrencia, preferir una transacción por operación lógica (unidad de trabajo) y evitar transacciones de larga duración.

Patrón de uso (servicio):
```csharp
public sealed class PersonasService
{
    private readonly IDbSession _db;
    public PersonasService(IDbSession db) => _db = db;

    public async Task CrearAsync(string nombre, CancellationToken ct)
    {
        await _db.OpenAsync(ct);
        await _db.BeginTransactionAsync(ct: ct);
        try
        {
            const string sql = "insert into personas(nombre) values (@nombre)";
            await _db.Connection!.ExecuteAsync(new CommandDefinition(sql, new { nombre }, _db.Transaction, cancellationToken: ct));
            await _db.CommitAsync(ct);
        }
        catch
        {
            await _db.RollbackAsync(ct);
            throw;
        }
    }
}
```

Errores comunes y cómo evitarlos:
- InvalidOperationException: "La sesión está cerrada. Llama a OpenAsync() primero." → Asegúrate de invocar OpenAsync antes de usar Connection/Transaction.
- "Ya existe una transacción activa." → No anides transacciones con la misma DbSession; crea un scope adicional si necesitas otra unidad transaccional.
- ObjectDisposedException(nameof(DbSession)) → No uses la instancia después de Dispose/DisposeAsync.


## Repositorio genérico DataMicroOrmRepository<T>

Contrato (Interfaces/IDataMicroOrm.cs):
```csharp
public interface IDataMicroOrm<T> where T : class
{
    Task<T?> GetByIdAsync(IDbSession session, object id, CancellationToken ct = default);
    Task<List<T>> GetAllAsync(
        IDbSession session,
        bool includeSoftDeleted = false,
        string? orderBy = null,
        SortDirection dir = SortDirection.Asc,
        int? limit = null,
        int? offset = null,
        string? filterField = null,
        object? filterValue = null,
        StringFilterMode stringMode = StringFilterMode.Equals,
        bool forceLowerCase = false,
        CancellationToken ct = default);

    Task<int> CountAsync(
        IDbSession session,
        bool includeSoftDeleted = false,
        string? filterField = null,
        object? filterValue = null,
        StringFilterMode stringMode = StringFilterMode.Equals,
        bool forceLowerCase = false,
        CancellationToken ct = default);

    Task<Page<T>> PageAsync(
        IDbSession session,
        int page,
        int size,
        bool includeSoftDeleted = false,
        string? orderBy = null,
        SortDirection dir = SortDirection.Asc,
        string? filterField = null,
        object? filterValue = null,
        StringFilterMode stringMode = StringFilterMode.Equals,
        bool forceLowerCase = false,
        CancellationToken ct = default);

    Task<int> InsertAsync(IDbSession session, T data, CancellationToken ct = default);
    Task<object?> InsertAsyncReturnId(IDbSession session, T data, CancellationToken ct = default);
    Task<bool> UpdateAsync(IDbSession session, T data, CancellationToken ct = default);
    Task<bool> UpdateSetAsync(IDbSession session, object id, object patch, CancellationToken ct = default);
    Task<bool> DeleteAsync(IDbSession session, object id, CancellationToken ct = default);
}
```

Resumen de comportamiento (DataMicroOrmRepository.cs):
- GetByIdAsync: SELECT por PK con alias "PropiedadCSharp" para mapeo limpio.
- GetAllAsync: soporta soft delete (WHERE eliminado=false), ordenación por propiedad o snake_case, filtros seguros por columna whitelisteada, y paginado con limit/offset parametrizados.
- InsertAsync / InsertAsyncReturnId: generan columnas/valores conforme WritableProps. ReturnId usa RETURNING PK.
- UpdateAsync: SET para columnas escribibles, WHERE por PK, excluye Computed/Write(false)/soft delete/PK implícita.
- UpdateSetAsync: parches parciales con whitelist de columnas; permite nombres C#, snake_case o [Column]; excluye PK y soft delete; trata JSONB como tal.
- DeleteAsync: si hay soft delete, UPDATE a true; si no, DELETE físico.
- CountAsync y PageAsync: reutilizan filtros y reglas de whitelist/soft delete.

Detalles de seguridad y generación de SQL:
- Todas las variables dinámicas viajan como parámetros Dapper/ICustomQueryParameter.
- Las columnas dinámicas (orden/filtro/update-set) se resuelven con ResolveColumn y whitelists basadas en EntityMap.
- Identificadores quotados con Naming.Quote para evitar colisiones y respetar mayúsculas/minúsculas.

Ejemplos:
```csharp
// Listado paginado con filtro contiene (case-insensitive) y orden por propiedad
var page = await repo.PageAsync(db,
    page: 2,
    size: 10,
    includeSoftDeleted: false,
    orderBy: "Usuario",
    dir: SortDirection.Desc,
    filterField: "Nombre",
    filterValue: "ali",
    stringMode: StringFilterMode.Contains,
    forceLowerCase: true,
    ct: ct);

// Inserción devolviendo id
var id = await repo.InsertAsyncReturnId(db, new Usuario {
    Nombre = "Alice",
    Usuario = "alice",
    PasswordHash = "xxx",
    Preferencias = JObject.FromObject(new { tema = "dark", notificaciones = true })
}, ct);

// PATCH parcial por columnas específicas (C# o snake_case o [Column])
var ok = await repo.UpdateSetAsync(db, id!, new { password_hash = "x" }, ct);
```


## Atributos de mapeo y convenciones (Attributes.cs + EntityMap)

Atributos disponibles (resumen con comentarios):
```csharp
[AttributeUsage(AttributeTargets.Class)]
public sealed class TableAttribute : Attribute
{
    public TableAttribute(string name) => Name = name; // Nombre de tabla (sin quotes)
    public string Name { get; }
    public string? Schema { get; init; }              // Opcional: esquema
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class KeyAttribute : Attribute {}          // PK autoincrement (serial/identity)
[AttributeUsage(AttributeTargets.Property)]
public sealed class ExplicitKeyAttribute : Attribute {}  // PK manual (no generada por la BD)
[AttributeUsage(AttributeTargets.Property)]
public sealed class ComputedAttribute : Attribute {}     // Solo lectura (excluir en INSERT/UPDATE)
[AttributeUsage(AttributeTargets.Property)]
public sealed class SoftDeleteAttribute : Attribute {}   // Marca la columna booleana de soft delete
[AttributeUsage(AttributeTargets.Property)]
public sealed class WriteAttribute : Attribute           // Para excluir columnas en writes
{
    public bool Include { get; init; } = true;
}
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class ColumnAttribute : Attribute          // Nombre exacto de columna
{
    public ColumnAttribute(string name) { Name = name; }
    public string Name { get; }
}
[AttributeUsage(AttributeTargets.Property)]
public sealed class JsonbAttribute : Attribute { }       // Fuerza tratamiento jsonb
```

Convenciones aplicadas por EntityMap:
- Tabla: [Table(Name, Schema)] o nombre de clase → snake_case. Se quita por partes: "schema"."tabla".
- Columnas: [Column] o snake_case de la propiedad.
- SELECT: "columna_real" AS "PropiedadCSharp" con alias seguro de 63 caracteres.
- WritableProps: excluye Computed, Write(false), PK implícita (permite ExplicitKey), y columna de soft delete.
- Soft delete: detecta [SoftDelete] o nombres convencionales "eliminado"/"is_deleted" con tipo bool/bool? y guarda nombre real.

Ejemplo de entidad típica:
```csharp
[Table("usuarios", Schema = "public")]
public sealed class Usuario
{
    [Key] public int Id { get; set; }
    public string Nombre { get; set; } = default!;
    public string Usuario { get; set; } = default!;
    [Column("password_hash")] public string PasswordHash { get; set; } = default!;
    [SoftDelete] public bool Eliminado { get; set; }
    [Jsonb] public JObject? Preferencias { get; set; }
}
```


## JSONB con Newtonsoft.Json y Dapper

Type handlers (Utils/DapperNewtonsoftHandlers.cs):
- JTokenTypeHandler, JObjectTypeHandler, JArrayTypeHandler.
- Parse deserializa desde cadena JSON; SetValue serializa y marca NpgsqlDbType.Jsonb.

Registro recomendado:
```csharp
SqlMapper.AddTypeHandler(new MicroOrmGesg.Utils.JObjectTypeHandler());
SqlMapper.AddTypeHandler(new MicroOrmGesg.Utils.JArrayTypeHandler());
SqlMapper.AddTypeHandler(new MicroOrmGesg.Utils.JTokenTypeHandler());
```

Escritura explícita como jsonb:
- En INSERT/UPDATE, cuando la propiedad es JToken/JObject/JArray o está marcada con [Jsonb], se serializa a string y se usa un ICustomQueryParameter que crea un NpgsqlParameter con NpgsqlDbType.Jsonb, garantizando el tipo jsonb en PostgreSQL.

Ventajas:
- Lectura: Dapper + handlers devuelven directamente objetos JObject/JArray/JToken.
- Escritura: robustez independientemente de conversiones implícitas del proveedor.

Modelado de propiedades jsonb:
- Dinámico: JObject/JArray/JToken (mutables, flexibles).
- String: almacenar JSON como texto y deserializar bajo demanda.
- DTO tipados: posible con type handlers personalizados si se desea des/serialización automática.


## Paginación y conteo

Tipo Page<T> (Utils/Page.cs):
```csharp
public sealed class Page<T>
{
    public required List<T> Items { get; init; }
    public required int Total { get; init; }
    [JsonProperty("Page")] public required int PageNumber { get; init; }
    public required int Size { get; init; }
}
```

- CountAsync: SELECT count(*) con mismos filtros/soft delete.
- PageAsync: calcula offset, delega en GetAllAsync y CountAsync, y empaqueta en Page<T>.
- Filtros consistentes con StringFilterMode y forceLowerCase para case-insensitive.


## Validación de modelos

Atributos:
- [Required(Message = "...")]: rechaza null y, en string, vacío/espacios.
- [StringLength(min, max, Message = "...")]: límites para string; si min/max=0, no aplica ese límite.

Uso del validador:
```csharp
var errores = ModelValidator.Validate(dto);
if (errores.Any())
{
    // Manejar errores (PropertyName + Message)
}
```

Notas:
- [Required] y [StringLength] son simples y no dependientes de DataAnnotations.
- Decide si se valida antes de persistir (servicio/aplicación) o en capa de presentación.


## Ejecución genérica de funciones PostgreSQL (IDataFunctions)

Contrato y comportamiento:
```csharp
public interface IDataFunctions
{
    Task<TResult?> CallFunctionAsync<TResult>(IDbSession session, string functionName, object? args = null, string? schema = null, CancellationToken ct = default);
    Task<List<TResult>> CallFunctionListAsync<TResult>(IDbSession session, string functionName, object? args = null, string? schema = null, CancellationToken ct = default);
    Task CallVoidFunctionAsync(IDbSession session, string functionName, object? args = null, string? schema = null, CancellationToken ct = default);
}
```

Reglas:
- Escalares/un solo registro: select schema.func(@p1,...)
- Conjuntos (TABLE/SETOF): select * from schema.func(@p1,...)
- Normalización de parámetros: args admite objeto anónimo o IDictionary<string,object?>; los nombres se normalizan quitando '@'.

Ejemplo:
```csharp
await _db.OpenAsync(ct);
string? token = await _funcs.CallFunctionAsync<string>(
    _db,
    "generar_token_recuperacion",
    new { p_idusuario = 1, p_ip_solicitud = "1.2.3.4", p_duracion_horas = 24 },
    schema: null,
    ct);

public sealed class ValidacionDto
{
    public bool Valido { get; set; }
    public int? IdUsuario { get; set; }
    public string? Mensaje { get; set; }
    public int Intentos { get; set; }
}
var lista = await _funcs.CallFunctionListAsync<ValidacionDto>(
    _db,
    "validar_token_recuperacion",
    new { p_token = token },
    schema: null,
    ct);

await _funcs.CallVoidFunctionAsync(_db, "incrementar_intento_token", new { p_token = token }, ct: ct);
```


## Seguridad, rendimiento y buenas prácticas

Seguridad:
- SQL parametrizado en todas las rutas.
- Whitelist de columnas para ordenar/filtrar/actualizar con nombres C#, snake_case o [Column].
- Quoting de identificadores previene colisiones y ambigüedades.

Rendimiento:
- Pool de conexiones con NpgsqlDataSource como Singleton.
- Selección con alias para Dapper evitando binding costoso.
- Paginación por limit/offset con filtros indexables.
- JSONB explícito en escritura evita conversiones ambiguas.

Cancelación:
- Todas las APIs aceptan CancellationToken y lo propagan a CommandDefinition y DbSession.
- Integra HttpContext.RequestAborted para cancelación cooperativa en ASP.NET Core.

Transacciones:
- Mantener el scope lo más acotado posible.
- Usar el IsolationLevel adecuado a la operación.


## Migraciones y evolución de esquemas

- Añadir columnas: usar [Column] en el modelo si el nombre difiere; si es jsonb, añadir [Jsonb].
- Cambiar PK: actualizar [Key]/[ExplicitKey] y comprobar que EntityMap detecta correctamente KeyProperty/KeyColumn.
- Soft delete: preferible añadir una columna booleana y marcarla con [SoftDelete].
- Renombrado de tabla/esquema: actualizar [Table(Name, Schema)].


## Pruebas (ideas y patrones)

- Test de mapeo: verificar que SelectColsCsv contiene alias esperados y que GetInsertColumnsAndParams excluye columnas no escribibles.
- Test de filtros: Given-When-Then para cada StringFilterMode con forceLowerCase on/off.
- Test de jsonb: insertar/leer un JObject y comparar semánticamente.
- Test de UpdateSet: enviar patch con campos válidos/invalidos, PK/soft delete, y asegurar whitelist y errores esperados.
- Test de DbSession: commit/rollback y estados de transacción; error cuando se usa sin OpenAsync.

Ejemplo (xUnit, pseudo):
```csharp
[Fact]
public async Task UpdateSet_Should_Ignore_SoftDelete_And_PrimaryKey()
{
    // Arrange: entidad con [SoftDelete] y PK
    // Act: patch con { Eliminado = true, Id = 99, Nombre = "X" }
    // Assert: solo actualiza Nombre; ignora Eliminado e Id; SQL contiene set "nombre" = @p_Nombre
}
```


## FAQ (preguntas frecuentes)

- ¿Puedo usarlo con múltiples bases de datos? Actualmente enfocado en PostgreSQL (Npgsql). Para otras BD habría que adaptar quoting, tipos y handlers.
- ¿Cómo aplico filtros por varias columnas? Hoy el contrato admite un único par filterField/filterValue. Para múltiples condiciones, construir una query específica o extender el repositorio.
- ¿Cómo mapear enums o tipos complejos no JSONB? Crear TypeHandlers específicos de Dapper.
- ¿Qué pasa con columnas calculadas? Marcar con [Computed] para excluir de INSERT/UPDATE.
- ¿Cómo forzar escritura de PK? Usa [ExplicitKey] para PK manual.


## Troubleshooting

- Mensaje: "No hay columnas válidas para actualizar con el patch proporcionado." → El patch no contenía campos whitelisteados o todos eran PK/soft delete/Computed/Write(false). Revisa nombres (C#, snake_case o [Column]).
- Mensaje: "El modelo X no tiene definida una clave primaria." → Agrega [Key] o [ExplicitKey] a alguna propiedad pública legible.
- Filtro que no aplica: asegúrate de que filterField resuelva a una columna válida mediante [Column] o convención.
- JSON que llega null tras SELECT: registra los TypeHandlers de JObject/JArray/JToken antes de ejecutar consultas (Startup/Program).
- Deadlocks o timeouts: revisa tamaño de transacciones y aislamiento; evita locks prolongados.


---

Este anexo complementa la guía breve superior. Si necesitas ejemplos adicionales (por entidad/DTO específicos, mapeos complejos o funciones PostgreSQL particulares), añade tus casos y amplía con pruebas unitarias para asegurar regresión cero.



## Filtros múltiples avanzados

El contrato actual de GetAllAsync/CountAsync/PageAsync admite un único par filterField/filterValue. Cuando necesites combinar varias condiciones, puedes extender el patrón de construcción del WHERE usando una whitelist de columnas (igual que ResolveColumn) y parámetros Dapper.

Objetivos del patrón
- Aceptar una lista de condiciones con operadores variados.
- Resolver cada Field a una columna válida: nombre de propiedad C#, snake_case o [Column].
- Construir expresiones con seguridad (siempre parametrizadas) y uniendo con AND/OR.
- Mantener opciones para búsquedas case-insensitive con forceLowerCase.

Modelo de condición (ejemplo)
```csharp
public enum FilterOp
{
    Equals,
    Contains,
    StartsWith,
    EndsWith,
    GreaterThan,
    GreaterOrEqual,
    LessThan,
    LessOrEqual,
    Between,   // usa Value y Value2
    In,        // usa IEnumerable
    IsNull,
    IsNotNull
}

public sealed class FilterCondition
{
    public required string Field { get; init; }
    public required FilterOp Op { get; init; }
    public object? Value { get; init; }
    public object? Value2 { get; init; }        // para Between
    public bool ForceLowerCase { get; init; }   // para comparaciones string
    public bool OrWithPrevious { get; init; }   // si true, une con OR en lugar de AND
}
```

Construcción segura del WHERE (patrón dentro del repositorio)
Nota: este ejemplo usa una función ResolveColumn análoga a la del repositorio, que acepta nombre de propiedad, snake_case o [Column].
```csharp
private static (string whereSql, DynamicParameters parameters) BuildWhereAndParamsMulti(
    EntityMap map,
    IEnumerable<FilterCondition> filters)
{
    var whereParts = new List<string>();
    var parameters = new DynamicParameters();
    int pIndex = 0;

    foreach (var f in filters)
    {
        if (string.IsNullOrWhiteSpace(f.Field)) continue;
        var col = ResolveColumn(map, f.Field);
        if (col is null) continue; // fuera de whitelist
        var colSql = Naming.Quote(col);

        // operadores sin valor
        if (f.Op == FilterOp.IsNull)
        {
            whereParts.Add((f.OrWithPrevious ? " or " : " and ") + $"{colSql} is null");
            continue;
        }
        if (f.Op == FilterOp.IsNotNull)
        {
            whereParts.Add((f.OrWithPrevious ? " or " : " and ") + $"{colSql} is not null");
            continue;
        }

        // preparar nombres de parámetros únicos
        string p1 = $"p{pIndex++}";
        string? p2 = null;

        // strings con opción de lower-case
        bool isString = f.Value is string;
        string colExpr = isString && f.ForceLowerCase ? $"lower({colSql})" : colSql;
        string valExpr = isString && f.ForceLowerCase ? $"lower(@{p1})" : $"@{p1}";

        string expr;
        switch (f.Op)
        {
            case FilterOp.Equals:
                parameters.Add(p1, f.Value);
                expr = $"{colExpr} = {valExpr}";
                break;
            case FilterOp.Contains:
                parameters.Add(p1, f.Value);
                expr = $"{colExpr} ILIKE '%' || {valExpr} || '%'";
                break;
            case FilterOp.StartsWith:
                parameters.Add(p1, f.Value);
                expr = $"{colExpr} ILIKE {valExpr} || '%'";
                break;
            case FilterOp.EndsWith:
                parameters.Add(p1, f.Value);
                expr = $"{colExpr} ILIKE '%' || {valExpr}";
                break;
            case FilterOp.GreaterThan:
                parameters.Add(p1, f.Value);
                expr = $"{colSql} > @{p1}";
                break;
            case FilterOp.GreaterOrEqual:
                parameters.Add(p1, f.Value);
                expr = $"{colSql} >= @{p1}";
                break;
            case FilterOp.LessThan:
                parameters.Add(p1, f.Value);
                expr = $"{colSql} < @{p1}";
                break;
            case FilterOp.LessOrEqual:
                parameters.Add(p1, f.Value);
                expr = $"{colSql} <= @{p1}";
                break;
            case FilterOp.Between:
                p2 = $"p{pIndex++}";
                parameters.Add(p1, f.Value);
                parameters.Add(p2, f.Value2);
                expr = $"{colSql} BETWEEN @{p1} AND @{p2}";
                break;
            case FilterOp.In:
                // Dapper expande IEnumerable con el nombre del parámetro
                parameters.Add(p1, f.Value);
                expr = $"{colSql} = ANY(@{p1})"; // para arrays; o usar IN (...) si prefieres
                break;
            default:
                continue;
        }

        string glue = whereParts.Count == 0 ? "" : (f.OrWithPrevious ? " or " : " and ");
        whereParts.Add(glue + expr);
    }

    string where = whereParts.Count > 0 ? " where " + string.Concat(whereParts) : string.Empty;
    return (where, parameters);
}
```

Ejemplos de uso
- AND simple (texto + bool):
```csharp
var filters = new List<FilterCondition>
{
    new() { Field = "Nombre", Op = FilterOp.Contains, Value = "ali", ForceLowerCase = true },
    new() { Field = "Activo", Op = FilterOp.Equals, Value = true }
};
var (where, prms) = BuildWhereAndParamsMulti(map, filters);
var sql = $"select {map.SelectColsCsv} from {map.TableFullQuoted}{where} order by \"Usuario\" asc limit 20";
```

- OR entre condiciones de texto:
```csharp
var filters = new List<FilterCondition>
{
    new() { Field = "Nombre", Op = FilterOp.Contains, Value = "ali", OrWithPrevious = false },
    new() { Field = "Usuario", Op = FilterOp.Contains, Value = "ali", OrWithPrevious = true }
};
```

- Rango de fechas + IN de estados:
```csharp
var filters = new List<FilterCondition>
{
    new() { Field = "fecha_alta", Op = FilterOp.Between, Value = new DateTime(2025,1,1), Value2 = new DateTime(2025,12,31) },
    new() { Field = "estado", Op = FilterOp.In, Value = new[] { "pendiente", "activo" } }
};
```

Notas y buenas prácticas
- Mantén la whitelist de columnas (como ResolveColumn): propiedad C#, snake_case o [Column]; ignora lo que no resuelva.
- Siempre parametriza (incluido IN/ANY) para evitar inyección y favorecer caché de planes.
- Para agrupaciones complejas (paréntesis), puedes agrupar condiciones en listas y combinar resultados parciales: where = (A and B) or (C and D).
- Si deseas exponer filtros múltiples en la API pública, considera un DTO dedicado y valida server-side los campos y operadores permitidos.
