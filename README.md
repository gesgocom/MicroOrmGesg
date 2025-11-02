# MicroOrmGesg

Micro ORM para PostgreSQL basado en Dapper y Npgsql. Proporciona CRUD genérico, queries directas, ejecución de funciones, migraciones idempotentes y logging integrado.

## Tabla de contenidos

### 🚀 Inicio rápido
- [Instalación](#instalación)
- [Setup inicial (5 minutos)](#setup-inicial)
- [Tu primer CRUD](#tu-primer-crud)

### 📚 Casos de uso
- [¿Cuándo usar qué?](#cuándo-usar-qué) - Guía de decisión
- [CRUD simple con repositorio genérico](#caso-1-crud-simple)
- [Queries SQL personalizadas](#caso-2-queries-personalizadas)
- [Transacciones y operaciones complejas](#caso-3-transacciones)
- [Ejecutar funciones PostgreSQL](#caso-4-funciones-postgresql)
- [Trabajo con JSONB](#caso-5-jsonb)
- [Migraciones de base de datos](#caso-6-migraciones)

### 🔧 Referencia completa
- [DbSession: Conexiones y transacciones](#ref-dbsession)
- [IDataMicroOrm: Repositorio genérico](#ref-datamicroorm)
- [IDirectQuery: Queries directas con Dapper](#ref-directquery)
- [IDataFunctions: Funciones PostgreSQL](#ref-datafunctions)
- [Sistema de migraciones](#ref-migraciones)
- [Logging y diagnóstico](#ref-logging)
- [Atributos de mapeo](#ref-atributos)
- [Paginación y filtrado](#ref-paginacion)

### 📖 Recursos adicionales
- [Mejores prácticas](#mejores-prácticas)
- [Troubleshooting](#troubleshooting)
- [Ejemplos completos](#ejemplos-completos)
- [Historial de cambios](#historial)

---

## 🚀 Inicio rápido

### Instalación

```bash
dotnet add package Gesgocom.MicroOrmGesg
```

O compilar localmente:
```bash
dotnet build --configuration Release
```

### Setup inicial

**1. Instalar paquete NuGet:**
```bash
dotnet add package Gesgocom.MicroOrmGesg
dotnet add package Npgsql
dotnet add package Dapper
dotnet add package Newtonsoft.Json
```

**2. Registrar servicios en `Program.cs`:**

```csharp
using MicroOrmGesg.Interfaces;
using MicroOrmGesg.Repository;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// 1. NpgsqlDataSource (pool de conexiones)
builder.Services.AddSingleton(sp =>
{
    var connectionString = builder.Configuration.GetConnectionString("PostgreSQL")!;
    return new NpgsqlDataSourceBuilder(connectionString).Build();
});

// 2. DbSession para conexiones y transacciones
builder.Services.AddScoped<IDbSession, DbSession>();

// 3. Repositorio genérico
builder.Services.AddScoped(typeof(IDataMicroOrm<>), typeof(DataMicroOrmRepository<>));

// 4. Queries directas con Dapper
builder.Services.AddScoped<IDirectQuery, DirectQuery>();

// 5. Ejecutor de funciones PostgreSQL
builder.Services.AddScoped<IDataFunctions, DataFunctionsRepository>();

// 6. Logging (opcional pero recomendado)
builder.Logging.AddConsole();
builder.Logging.AddFilter("MicroOrmGesg", LogLevel.Information);

var app = builder.Build();
app.Run();
```

**3. Configurar connection string en `appsettings.json`:**

```json
{
  "ConnectionStrings": {
    "PostgreSQL": "Host=localhost;Database=mydb;Username=user;Password=pass"
  }
}
```

### Tu primer CRUD

**1. Define tu entidad:**

```csharp
using MicroOrmGesg.Attributes;

[Table("usuarios")]
public class Usuario
{
    [Key]
    public int Id { get; set; }

    public string Nombre { get; set; } = null!;
    public string Email { get; set; } = null!;

    [SoftDelete]
    public bool Eliminado { get; set; }
}
```

**2. Crea un servicio:**

```csharp
public class UsuarioService
{
    private readonly IDbSession _db;
    private readonly IDataMicroOrm<Usuario> _repo;

    public UsuarioService(IDbSession db, IDataMicroOrm<Usuario> repo)
    {
        _db = db;
        _repo = repo;
    }

    public async Task<Usuario?> ObtenerAsync(int id, CancellationToken ct)
    {
        await _db.OpenAsync(ct);
        return await _repo.GetByIdAsync(_db, id, ct);
    }

    public async Task<int> CrearAsync(Usuario usuario, CancellationToken ct)
    {
        await _db.OpenAsync(ct);
        await _db.BeginTransactionAsync(ct: ct);

        try
        {
            var id = await _repo.InsertAsyncReturnId(_db, usuario, ct);
            await _db.CommitAsync(ct);
            return (int)id!;
        }
        catch
        {
            await _db.RollbackAsync(ct);
            throw;
        }
    }
}
```

**3. Úsalo en un controlador:**

```csharp
[ApiController]
[Route("api/[controller]")]
public class UsuariosController : ControllerBase
{
    private readonly UsuarioService _service;

    public UsuariosController(UsuarioService service) => _service = service;

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(int id, CancellationToken ct)
    {
        var usuario = await _service.ObtenerAsync(id, ct);
        return usuario is null ? NotFound() : Ok(usuario);
    }

    [HttpPost]
    public async Task<IActionResult> Post(Usuario usuario, CancellationToken ct)
    {
        var id = await _service.CrearAsync(usuario, ct);
        return CreatedAtAction(nameof(Get), new { id }, usuario);
    }
}
```

✅ **¡Listo!** Ya tienes CRUD funcional con transacciones, soft delete y logging.

---

## 📚 Casos de uso

### ¿Cuándo usar qué?

| Necesitas... | Usa | Ejemplo |
|-------------|-----|---------|
| CRUD simple de una tabla | `IDataMicroOrm<T>` | `await _repo.GetByIdAsync(...)` |
| Query con JOINs o subconsultas | `IDirectQuery` | `await _query.QueryAsync<Dto>("SELECT...")` |
| Llamar función/stored procedure | `IDataFunctions` | `await _funcs.CallFunctionAsync(...)` |
| Operación con múltiples tablas | `IDirectQuery` + Transacción | Ver [Caso 3](#caso-3-transacciones) |
| Paginación y filtros simples | `IDataMicroOrm<T>` | `await _repo.PageAsync(...)` |
| Migrar esquema de base de datos | `IPgMigrator` | Ver [Caso 6](#caso-6-migraciones) |

### Caso 1: CRUD simple

**Problema:** Necesito operaciones básicas (crear, leer, actualizar, eliminar) en una tabla.

**Solución:** Usa `IDataMicroOrm<T>` (repositorio genérico)

```csharp
public class ProductoService
{
    private readonly IDbSession _db;
    private readonly IDataMicroOrm<Producto> _repo;

    // Leer
    public async Task<Producto?> ObtenerAsync(int id, CancellationToken ct)
    {
        await _db.OpenAsync(ct);
        return await _repo.GetByIdAsync(_db, id, ct);
    }

    // Listar con paginación
    public async Task<Page<Producto>> ListarAsync(int page, int size, CancellationToken ct)
    {
        await _db.OpenAsync(ct);
        return await _repo.PageAsync(_db, page, size, ct: ct);
    }

    // Crear
    public async Task<int> CrearAsync(Producto producto, CancellationToken ct)
    {
        await _db.OpenAsync(ct);
        var id = await _repo.InsertAsyncReturnId(_db, producto, ct);
        return (int)id!;
    }

    // Actualizar
    public async Task<bool> ActualizarAsync(Producto producto, CancellationToken ct)
    {
        await _db.OpenAsync(ct);
        return await _repo.UpdateAsync(_db, producto, ct);
    }

    // Actualización parcial
    public async Task<bool> ActualizarPrecioAsync(int id, decimal nuevoPrecio, CancellationToken ct)
    {
        await _db.OpenAsync(ct);
        return await _repo.UpdateSetAsync(_db, id, new { precio = nuevoPrecio }, ct);
    }

    // Eliminar (soft delete si la entidad lo tiene configurado)
    public async Task<bool> EliminarAsync(int id, CancellationToken ct)
    {
        await _db.OpenAsync(ct);
        return await _repo.DeleteAsync(_db, id, ct);
    }
}
```

**Ventajas:**
- No escribes SQL
- Convenciones automáticas (snake_case)
- Soft delete automático
- Type-safe

**Ver más:** [Referencia IDataMicroOrm](#ref-datamicroorm)

---

### Caso 2: Queries personalizadas

**Problema:** Necesito hacer un JOIN, una subconsulta o SQL específico.

**Solución:** Usa `IDirectQuery` para SQL directo con Dapper

```csharp
public class ReporteService
{
    private readonly IDbSession _db;
    private readonly IDirectQuery _query;

    // Query con JOIN
    public async Task<List<UsuarioConPedidosDto>> ObtenerUsuariosConPedidosAsync(CancellationToken ct)
    {
        await _db.OpenAsync(ct);

        const string sql = @"
            SELECT
                u.id,
                u.nombre,
                u.email,
                COUNT(p.id) as total_pedidos,
                SUM(p.total) as total_gastado
            FROM usuarios u
            LEFT JOIN pedidos p ON p.usuario_id = u.id
            WHERE u.eliminado = false
            GROUP BY u.id, u.nombre, u.email
            HAVING COUNT(p.id) > 0
            ORDER BY total_gastado DESC
            LIMIT 100";

        return (await _query.QueryAsync<UsuarioConPedidosDto>(_db, sql, ct: ct)).ToList();
    }

    // Query con parámetros
    public async Task<PedidoDetalleDto?> ObtenerDetallePedidoAsync(int pedidoId, CancellationToken ct)
    {
        await _db.OpenAsync(ct);

        const string sql = @"
            SELECT
                p.id,
                p.fecha,
                p.total,
                u.nombre as nombre_usuario,
                json_agg(json_build_object(
                    'producto', prod.nombre,
                    'cantidad', dp.cantidad,
                    'precio', dp.precio_unitario
                )) as items
            FROM pedidos p
            INNER JOIN usuarios u ON u.id = p.usuario_id
            LEFT JOIN detalle_pedido dp ON dp.pedido_id = p.id
            LEFT JOIN productos prod ON prod.id = dp.producto_id
            WHERE p.id = @pedidoId
            GROUP BY p.id, p.fecha, p.total, u.nombre";

        return await _query.QuerySingleOrDefaultAsync<PedidoDetalleDto>(
            _db, sql, new { pedidoId }, ct);
    }

    // Múltiples result sets
    public async Task<DashboardDto> ObtenerDashboardAsync(int usuarioId, CancellationToken ct)
    {
        await _db.OpenAsync(ct);

        const string sql = @"
            -- Result set 1: Usuario
            SELECT id, nombre, email FROM usuarios WHERE id = @usuarioId;

            -- Result set 2: Pedidos recientes
            SELECT id, fecha, total FROM pedidos
            WHERE usuario_id = @usuarioId
            ORDER BY fecha DESC LIMIT 5;

            -- Result set 3: Estadísticas
            SELECT COUNT(*) as total_pedidos, SUM(total) as total_gastado
            FROM pedidos WHERE usuario_id = @usuarioId";

        await using var multi = await _query.QueryMultipleAsync(_db, sql, new { usuarioId }, ct);

        var usuario = await multi.ReadSingleAsync<UsuarioDto>();
        var pedidos = (await multi.ReadAsync<PedidoDto>()).ToList();
        var stats = await multi.ReadSingleAsync<StatsDto>();

        return new DashboardDto(usuario, pedidos, stats);
    }
}
```

**Ventajas:**
- Flexibilidad total (cualquier SQL)
- JOINs, CTEs, window functions
- Comparte conexión/transacción con el repositorio

**Ver más:** [Referencia IDirectQuery](#ref-directquery)

---

### Caso 3: Transacciones

**Problema:** Necesito que varias operaciones se ejecuten atómicamente (todo o nada).

**Solución:** Usa `IDbSession.BeginTransactionAsync()` + Commit/Rollback

```csharp
public class PedidoService
{
    private readonly IDbSession _db;
    private readonly IDataMicroOrm<Pedido> _pedidoRepo;
    private readonly IDirectQuery _query;

    public async Task<int> CrearPedidoAsync(CrearPedidoDto dto, CancellationToken ct)
    {
        await _db.OpenAsync(ct);
        await _db.BeginTransactionAsync(ct: ct);

        try
        {
            // 1. Crear pedido
            var pedido = new Pedido
            {
                UsuarioId = dto.UsuarioId,
                Fecha = DateTime.UtcNow,
                Total = dto.Items.Sum(i => i.Precio * i.Cantidad)
            };
            var pedidoId = (int)(await _pedidoRepo.InsertAsyncReturnId(_db, pedido, ct))!;

            // 2. Insertar items del pedido
            foreach (var item in dto.Items)
            {
                const string insertItem = @"
                    INSERT INTO detalle_pedido (pedido_id, producto_id, cantidad, precio_unitario)
                    VALUES (@pedidoId, @productoId, @cantidad, @precio)";

                await _query.ExecuteAsync(_db, insertItem, new
                {
                    pedidoId,
                    productoId = item.ProductoId,
                    cantidad = item.Cantidad,
                    precio = item.Precio
                }, ct);
            }

            // 3. Actualizar stock de productos
            foreach (var item in dto.Items)
            {
                const string updateStock = @"
                    UPDATE productos
                    SET stock = stock - @cantidad
                    WHERE id = @productoId AND stock >= @cantidad";

                var rowsAffected = await _query.ExecuteAsync(_db, updateStock, new
                {
                    productoId = item.ProductoId,
                    cantidad = item.Cantidad
                }, ct);

                if (rowsAffected == 0)
                    throw new InvalidOperationException($"Stock insuficiente para producto {item.ProductoId}");
            }

            // 4. Confirmar transacción
            await _db.CommitAsync(ct);
            return pedidoId;
        }
        catch
        {
            // Rollback automático en caso de error
            await _db.RollbackAsync(ct);
            throw;
        }
    }
}
```

**Ventajas:**
- Atomicidad garantizada
- Rollback automático en excepciones
- Puedes mezclar repositorio + queries directas

**Ver más:** [Referencia DbSession](#ref-dbsession)

---

### Caso 4: Funciones PostgreSQL

**Problema:** Tengo lógica compleja en funciones/stored procedures de PostgreSQL.

**Solución:** Usa `IDataFunctions` para invocarlas

```csharp
public class AuthService
{
    private readonly IDbSession _db;
    private readonly IDataFunctions _funcs;

    // Función que devuelve un escalar
    public async Task<string?> GenerarTokenRecuperacionAsync(int usuarioId, CancellationToken ct)
    {
        await _db.OpenAsync(ct);

        return await _funcs.CallFunctionAsync<string>(
            _db,
            "generar_token_recuperacion",
            new { p_usuario_id = usuarioId, p_duracion_horas = 24 },
            schema: "auth",
            ct);
    }

    // Función que devuelve una tabla (SETOF o TABLE)
    public async Task<List<ValidacionDto>> ValidarTokenAsync(string token, CancellationToken ct)
    {
        await _db.OpenAsync(ct);

        return await _funcs.CallFunctionListAsync<ValidacionDto>(
            _db,
            "validar_token_recuperacion",
            new { p_token = token },
            schema: "auth",
            ct);
    }

    // Función void (sin retorno)
    public async Task IncrementarIntentoAsync(string token, CancellationToken ct)
    {
        await _db.OpenAsync(ct);

        await _funcs.CallVoidFunctionAsync(
            _db,
            "incrementar_intento_token",
            new { p_token = token },
            schema: "auth",
            ct);
    }
}
```

**Ejemplo de función PostgreSQL:**

```sql
CREATE OR REPLACE FUNCTION auth.generar_token_recuperacion(
    p_usuario_id int,
    p_duracion_horas int DEFAULT 24
)
RETURNS text
LANGUAGE plpgsql
AS $$
DECLARE
    v_token text;
BEGIN
    v_token := encode(gen_random_bytes(32), 'hex');

    INSERT INTO auth.tokens_recuperacion (usuario_id, token, expira_en)
    VALUES (p_usuario_id, v_token, now() + (p_duracion_horas || ' hours')::interval);

    RETURN v_token;
END;
$$;
```

**Ver más:** [Referencia IDataFunctions](#ref-datafunctions)

---

### Caso 5: JSONB

**Problema:** Necesito almacenar/consultar datos en formato JSON.

**Solución:** Usa el atributo `[Jsonb]` y JObject de Newtonsoft.Json

**1. Registrar TypeHandlers (una vez en Program.cs):**

```csharp
using Dapper;
using MicroOrmGesg.Utils;

SqlMapper.AddTypeHandler(new JObjectTypeHandler());
SqlMapper.AddTypeHandler(new JArrayTypeHandler());
SqlMapper.AddTypeHandler(new JTokenTypeHandler());
```

**2. Define tu entidad con JSONB:**

```csharp
using Newtonsoft.Json.Linq;
using MicroOrmGesg.Attributes;

[Table("usuarios")]
public class Usuario
{
    [Key]
    public int Id { get; set; }

    public string Nombre { get; set; } = null!;

    [Jsonb]
    public JObject? Preferencias { get; set; }

    [Jsonb]
    public JObject? Metadatos { get; set; }
}
```

**3. Úsalo normalmente:**

```csharp
// Crear con JSONB
var usuario = new Usuario
{
    Nombre = "Juan",
    Preferencias = JObject.FromObject(new
    {
        tema = "dark",
        idioma = "es",
        notificaciones = true
    }),
    Metadatos = JObject.FromObject(new
    {
        ip_registro = "192.168.1.1",
        navegador = "Chrome"
    })
};

await _db.OpenAsync(ct);
var id = await _repo.InsertAsyncReturnId(_db, usuario, ct);

// Leer
var usuarioLeido = await _repo.GetByIdAsync(_db, (int)id!, ct);
var tema = usuarioLeido?.Preferencias?["tema"]?.ToString(); // "dark"

// Actualizar parcialmente el JSONB
var nuevoJson = JObject.FromObject(new { tema = "light", idioma = "en" });
await _repo.UpdateSetAsync(_db, (int)id!, new { preferencias = nuevoJson }, ct);
```

**4. Consultas con operadores JSONB:**

```csharp
const string sql = @"
    SELECT * FROM usuarios
    WHERE preferencias->>'tema' = @tema
      AND preferencias->'notificaciones' = 'true'::jsonb";

var usuarios = await _query.QueryAsync<Usuario>(
    _db, sql, new { tema = "dark" }, ct);
```

**Ver más:** [Documentación JSONB](#ref-jsonb)

---

### Caso 6: Migraciones

**Problema:** Necesito gestionar cambios en el esquema de base de datos de forma controlada.

**Solución:** Usa el sistema de migraciones integrado

**1. Configura migraciones en Program.cs:**

```csharp
using MicroOrmGesg.Migrations.Extensions;
using MicroOrmGesg.Migrations.Models;

builder.Services.AddPgMigrations(options =>
{
    options.AdvisoryLockKey = "myapp:migrations";
    options.DriftPolicy = DriftPolicy.WarnAndSkip;
    options.StopOnError = true;
});
```

**2. Crea tu archivo de migraciones (`scripts/schema.sql`):**

```sql
-- @step id:001 name:create.usuarios
CREATE TABLE IF NOT EXISTS usuarios(
  id serial PRIMARY KEY,
  nombre text NOT NULL,
  email text NOT NULL UNIQUE,
  eliminado boolean NOT NULL DEFAULT false
);

-- @step id:002 name:index.usuarios.email
CREATE INDEX IF NOT EXISTS idx_usuarios_email ON usuarios(email) WHERE eliminado = false;

-- @step id:003 name:create.pedidos
CREATE TABLE IF NOT EXISTS pedidos(
  id serial PRIMARY KEY,
  usuario_id int NOT NULL REFERENCES usuarios(id),
  fecha timestamptz NOT NULL DEFAULT now(),
  total decimal(10,2) NOT NULL
);

-- @step id:004 name:alter.usuarios.add_telefono
-- @check SELECT EXISTS(
--   SELECT 1 FROM information_schema.columns
--   WHERE table_name='usuarios' AND column_name='telefono'
-- );
ALTER TABLE usuarios ADD COLUMN IF NOT EXISTS telefono text;
```

**3. Ejecuta migraciones al inicio:**

```csharp
using MicroOrmGesg.Migrations;

var app = builder.Build();

// Ejecutar migraciones antes de iniciar
using (var scope = app.Services.CreateScope())
{
    var migrator = scope.ServiceProvider.GetRequiredService<IPgMigrator>();
    var source = new FileMigrationSource("./scripts/schema.sql");

    var result = await migrator.RunAsync(source);

    if (!result.IsSuccess)
    {
        Console.WriteLine($"Migraciones fallidas: {result.StepsFailed} pasos");
        Environment.Exit(1);
    }
}

app.Run();
```

**Ventajas:**
- Idempotentes (puedes ejecutarlas múltiples veces)
- Detección de drift (cambios no autorizados)
- Advisory locks (seguro en multi-instancia)
- Transacciones por paso

**Ver más:** [Referencia completa de migraciones](#ref-migraciones)

---

## 🔧 Referencia completa

### <a id="ref-dbsession"></a>DbSession: Conexiones y transacciones

`IDbSession` gestiona una conexión y opcionalmente una transacción por scope (típicamente por request HTTP).

#### Métodos principales

```csharp
public interface IDbSession
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

#### Ciclo de vida típico

```csharp
await _db.OpenAsync(ct);                    // 1. Abrir conexión
await _db.BeginTransactionAsync(ct: ct);    // 2. Iniciar transacción (opcional)

try
{
    // ... operaciones de base de datos ...
    await _db.CommitAsync(ct);              // 3. Confirmar
}
catch
{
    await _db.RollbackAsync(ct);            // 4. Revertir en caso de error
    throw;
}
// 5. Dispose automático al finalizar el scope
```

#### Niveles de aislamiento

```csharp
// Por defecto: ReadCommitted
await _db.BeginTransactionAsync(ct: ct);

// Serializable (más estricto)
await _db.BeginTransactionAsync(IsolationLevel.Serializable, ct);

// RepeatableRead
await _db.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
```

#### Logs generados

Con `LogLevel.Debug`:
```
[DBG] Abriendo nueva conexión a la base de datos desde el pool
[DBG] Iniciando transacción con nivel de aislamiento ReadCommitted
[DBG] Confirmando transacción (COMMIT)
```

---

### <a id="ref-datamicroorm"></a>IDataMicroOrm: Repositorio genérico

Repositorio tipado que proporciona CRUD completo sin escribir SQL.

#### Métodos disponibles

```csharp
public interface IDataMicroOrm<T> where T : class
{
    // Lectura
    Task<T?> GetByIdAsync(IDbSession session, object id, CancellationToken ct = default);
    Task<List<T>> GetAllAsync(IDbSession session, bool includeSoftDeleted = false, string? orderBy = null, ...);
    Task<int> CountAsync(IDbSession session, bool includeSoftDeleted = false, ...);
    Task<Page<T>> PageAsync(IDbSession session, int page, int size, ...);

    // Escritura
    Task<int> InsertAsync(IDbSession session, T data, CancellationToken ct = default);
    Task<object?> InsertAsyncReturnId(IDbSession session, T data, CancellationToken ct = default);
    Task<bool> UpdateAsync(IDbSession session, T data, CancellationToken ct = default);
    Task<bool> UpdateSetAsync(IDbSession session, object id, object patch, CancellationToken ct = default);
    Task<bool> DeleteAsync(IDbSession session, object id, CancellationToken ct = default);
}
```

#### Convenciones automáticas

| Convención | Ejemplo C# | SQL generado |
|-----------|------------|--------------|
| Nombre de clase → tabla | `Usuario` | `usuarios` (snake_case) |
| Nombre de propiedad → columna | `NombreCompleto` | `nombre_completo` |
| `[Table("...")]` | `[Table("users")]` | `users` (literal) |
| `[Column("...")]` | `[Column("full_name")]` | `full_name` (literal) |
| `[Key]` | `public int Id` | PRIMARY KEY autoincrement |
| `[SoftDelete]` | `public bool Eliminado` | DELETE → UPDATE eliminado=true |

#### Paginación y filtros

```csharp
var page = await _repo.PageAsync(
    _db,
    page: 2,                           // Página 2
    size: 10,                          // 10 elementos por página
    includeSoftDeleted: false,         // Excluir eliminados
    orderBy: "FechaCreacion",          // Ordenar por propiedad C#
    dir: SortDirection.Desc,           // Descendente
    filterField: "Nombre",             // Filtrar por columna
    filterValue: "Juan",               // Valor a buscar
    stringMode: StringFilterMode.Contains,  // Coincidencia parcial
    forceLowerCase: true,              // Case-insensitive
    ct: ct
);

Console.WriteLine($"Total: {page.Total}");
Console.WriteLine($"Página {page.PageNumber} de {Math.Ceiling(page.Total / (double)page.Size)}");
foreach (var item in page.Items)
{
    // ...
}
```

#### UpdateSetAsync (PATCH parcial)

```csharp
// Actualizar solo el email
await _repo.UpdateSetAsync(_db, usuarioId, new { email = "nuevo@example.com" }, ct);

// Actualizar múltiples campos
await _repo.UpdateSetAsync(_db, usuarioId, new
{
    email = "nuevo@example.com",
    nombre = "Nuevo Nombre",
    telefono = "123456789"
}, ct);

// También acepta snake_case
await _repo.UpdateSetAsync(_db, usuarioId, new { password_hash = "xxx" }, ct);
```

**Importante:** `UpdateSetAsync` ignora automáticamente:
- La primary key (no se puede cambiar)
- Columnas de soft delete
- Columnas marcadas con `[Computed]` o `[Write(Include = false)]`

---

### <a id="ref-directquery"></a>IDirectQuery: Queries directas con Dapper

Ejecuta SQL personalizado compartiendo la misma conexión y transacción de `IDbSession`.

#### Métodos disponibles

```csharp
public interface IDirectQuery
{
    Task<IEnumerable<T>> QueryAsync<T>(...);
    Task<T> QuerySingleAsync<T>(...);
    Task<T?> QuerySingleOrDefaultAsync<T>(...);
    Task<T> QueryFirstAsync<T>(...);
    Task<T?> QueryFirstOrDefaultAsync<T>(...);
    Task<int> ExecuteAsync(...);
    Task<T?> ExecuteScalarAsync<T>(...);
    Task<SqlMapper.GridReader> QueryMultipleAsync(...);
}
```

#### Cuándo usar cada método

| Método | Uso | Ejemplo |
|--------|-----|---------|
| `QueryAsync<T>` | Múltiples filas | `SELECT * FROM usuarios` |
| `QuerySingleAsync<T>` | Exactamente 1 fila (error si 0 o >1) | `SELECT * FROM usuarios WHERE id = @id` |
| `QuerySingleOrDefaultAsync<T>` | 0 o 1 fila (error si >1) | Lo mismo, pero retorna null si no existe |
| `QueryFirstAsync<T>` | Al menos 1 fila (toma la primera) | `SELECT * FROM usuarios LIMIT 1` |
| `QueryFirstOrDefaultAsync<T>` | 0 o más filas (toma la primera o null) | Lo mismo, pero retorna null si vacío |
| `ExecuteAsync` | INSERT/UPDATE/DELETE | Retorna filas afectadas |
| `ExecuteScalarAsync<T>` | Un solo valor | `SELECT COUNT(*) ...` |
| `QueryMultipleAsync` | Múltiples result sets | Varios SELECT en una llamada |

#### Ejemplos rápidos

```csharp
// SELECT múltiple
var usuarios = await _query.QueryAsync<Usuario>(
    _db, "SELECT * FROM usuarios WHERE activo = @activo",
    new { activo = true }, ct);

// INSERT con RETURNING
var nuevoId = await _query.ExecuteScalarAsync<int>(
    _db, "INSERT INTO logs(mensaje) VALUES(@msg) RETURNING id",
    new { msg = "Log entry" }, ct);

// UPDATE
var rowsAffected = await _query.ExecuteAsync(
    _db, "UPDATE productos SET stock = stock - @qty WHERE id = @id",
    new { qty = 5, id = 10 }, ct);

// COUNT
var total = await _query.ExecuteScalarAsync<int>(
    _db, "SELECT COUNT(*) FROM pedidos WHERE fecha > @fecha",
    new { fecha = DateTime.Today.AddDays(-30) }, ct);
```

#### Logs generados

```
[DBG] Ejecutando QueryAsync<Usuario>: SELECT * FROM usuarios WHERE activo = @activo
[DBG] QueryAsync<Usuario> ejecutado exitosamente
[DBG] Ejecutando ExecuteAsync (comando): UPDATE productos SET stock = ...
[DBG] ExecuteAsync completado: 1 fila(s) afectada(s)
```

---

### <a id="ref-datafunctions"></a>IDataFunctions: Funciones PostgreSQL

Invoca funciones almacenadas en PostgreSQL sin escribir SQL manualmente.

#### Métodos

```csharp
public interface IDataFunctions
{
    // Función que devuelve un escalar
    Task<TResult?> CallFunctionAsync<TResult>(
        IDbSession session, string functionName, object? args = null,
        string? schema = null, CancellationToken ct = default);

    // Función que devuelve tabla (SETOF/TABLE)
    Task<List<TResult>> CallFunctionListAsync<TResult>(
        IDbSession session, string functionName, object? args = null,
        string? schema = null, CancellationToken ct = default);

    // Función void (sin retorno)
    Task CallVoidFunctionAsync(
        IDbSession session, string functionName, object? args = null,
        string? schema = null, CancellationToken ct = default);
}
```

#### Parámetros

Puedes pasar argumentos de dos formas:

**1. Objeto anónimo:**
```csharp
await _funcs.CallFunctionAsync<string>(
    _db, "generar_token",
    new { p_usuario_id = 123, p_duracion = 24 },
    ct: ct);
```

**2. Diccionario:**
```csharp
var args = new Dictionary<string, object?>
{
    ["p_usuario_id"] = 123,
    ["p_duracion"] = 24
};
await _funcs.CallFunctionAsync<string>(_db, "generar_token", args, ct: ct);
```

El prefijo `@` se elimina automáticamente si lo incluyes.

#### SQL generado

```csharp
// CallFunctionAsync (escalar)
await _funcs.CallFunctionAsync<int>(_db, "sumar", new { a = 5, b = 3 });
// SQL: SELECT sumar(@a, @b)

// CallFunctionListAsync (tabla)
await _funcs.CallFunctionListAsync<Usuario>(_db, "obtener_usuarios_activos");
// SQL: SELECT * FROM obtener_usuarios_activos()

// Con schema
await _funcs.CallFunctionAsync<string>(_db, "generar_hash", args, schema: "auth");
// SQL: SELECT auth.generar_hash(@password)
```

---

### <a id="ref-migraciones"></a>Sistema de migraciones

Sistema completo de migraciones idempotentes con checksums SHA-256, advisory locks y detección de drift.

#### Configuración

```csharp
builder.Services.AddPgMigrations(options =>
{
    options.AdvisoryLockKey = "myapp:migrations";     // Lock único por app
    options.CommandTimeoutSeconds = 120;              // Timeout de comandos
    options.DriftPolicy = DriftPolicy.WarnAndSkip;    // Fail, WarnAndSkip, Reapply
    options.StopOnError = true;                       // Detener al primer error
    options.JournalTableName = "__micro_orm_migrations";  // Tabla de historial
    options.JournalSchema = null;                     // Schema (null = public)
});
```

#### Formato de script SQL

**Directiva @step:**
```sql
-- @step id:001 name:create.usuarios
```
- `id`: Identificador único (001, 002, 003... o 001-create-users)
- `name`: Descripción (usa puntos: create.tabla, alter.tabla.columna)

**Directiva @check (opcional):**
```sql
-- @check SELECT to_regclass('public.usuarios') IS NOT NULL;
```
- SQL que devuelve boolean
- `true` = ya aplicado, `false` = necesita aplicarse
- Solo usar cuando no tienes `IF NOT EXISTS`

#### Cuándo usar @check

| SQL | ¿Necesitas @check? | Razón |
|-----|-------------------|-------|
| `CREATE TABLE IF NOT EXISTS` | ❌ NO | Ya es idempotente |
| `CREATE INDEX IF NOT EXISTS` | ❌ NO | Ya es idempotente |
| `CREATE OR REPLACE FUNCTION` | ❌ NO | Ya es idempotente |
| `ALTER TABLE ADD COLUMN IF NOT EXISTS` | ❌ NO | Ya es idempotente |
| `ALTER TABLE ALTER COLUMN TYPE` | ✅ SÍ | No tiene IF NOT EXISTS |
| `ALTER TABLE ADD CONSTRAINT` | ✅ SÍ | PostgreSQL < 16 no tiene IF NOT EXISTS |
| `INSERT INTO ...` | ✅ SÍ | Para evitar duplicados |
| Migrar a BD existente | ✅ SÍ | Para adoptar objetos pre-existentes |

#### Ejemplo completo

```sql
-- Tabla con IF NOT EXISTS (sin @check)
-- @step id:001 name:create.usuarios
CREATE TABLE IF NOT EXISTS usuarios(
  id serial PRIMARY KEY,
  email text NOT NULL UNIQUE
);

-- Índice con IF NOT EXISTS (sin @check)
-- @step id:002 name:index.usuarios.email
CREATE INDEX IF NOT EXISTS idx_usuarios_email ON usuarios(email);

-- ALTER COLUMN sin IF NOT EXISTS (CON @check)
-- @step id:003 name:alter.usuarios.email_varchar
-- @check SELECT EXISTS(
--   SELECT 1 FROM information_schema.columns
--   WHERE table_name='usuarios' AND column_name='email'
--     AND data_type='character varying' AND character_maximum_length=255
-- );
ALTER TABLE usuarios ALTER COLUMN email TYPE varchar(255);

-- Función con CREATE OR REPLACE (sin @check)
-- @step id:004 name:function.update_timestamp
CREATE OR REPLACE FUNCTION update_timestamp()
RETURNS TRIGGER AS $$
BEGIN
  NEW.updated_at = now();
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- Datos iniciales (CON @check)
-- @step id:005 name:insert.admin
-- @check SELECT EXISTS(SELECT 1 FROM usuarios WHERE email = 'admin@example.com');
INSERT INTO usuarios(email) VALUES('admin@example.com');
```

#### Ejecución

**Opción 1: Manual en Program.cs**

```csharp
var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var migrator = scope.ServiceProvider.GetRequiredService<IPgMigrator>();
    var source = new FileMigrationSource("./scripts/schema.sql");
    var result = await migrator.RunAsync(source);

    if (!result.IsSuccess)
        Environment.Exit(1);
}

app.Run();
```

**Opción 2: IHostedService**

```csharp
builder.Services.AddHostedService<MigrationHostedService>();
```

#### Políticas de drift

Cuando un paso ya aplicado tiene un checksum diferente:

| Política | Comportamiento |
|----------|----------------|
| `Fail` | Lanza excepción (fuerza corrección manual) |
| `WarnAndSkip` | Registra warning y omite (default) |
| `Reapply` | Re-ejecuta el paso (útil para funciones/vistas) |

#### Generar migraciones con LLM

Puedes usar este prompt con Claude/GPT para generar automáticamente las directivas:

```
Tengo las siguientes sentencias SQL de PostgreSQL y necesito generar
las directivas @step y @check para el sistema de migraciones de MicroOrmGesg.

REGLAS:
1. Si tiene IF NOT EXISTS, CREATE OR REPLACE, o ADD COLUMN IF NOT EXISTS → NO generar @check
2. Para CREATE TABLE sin IF NOT EXISTS → usar to_regclass('schema.tabla')
3. Para ALTER TABLE ALTER COLUMN TYPE → usar information_schema.columns
4. Para INSERT INTO → usar EXISTS(SELECT 1 FROM tabla WHERE condicion_unica)

FORMATO:
-- @step id:XXX name:descripcion.del.paso
-- @check SELECT ...;  (solo si es necesario)
[SQL original]

SENTENCIAS SQL:
[Pega aquí tu SQL]
```

---

### <a id="ref-logging"></a>Logging y diagnóstico

Todos los componentes incluyen logging con `ILogger<T>`.

#### Componentes con logging

| Componente | Qué registra |
|-----------|--------------|
| `DbSession` | Conexiones, transacciones, commits, rollbacks |
| `DirectQuery` | Queries ejecutadas, tipo de resultado, filas afectadas |
| `DataFunctionsRepository` | Funciones invocadas, esquema, resultados |
| `PgMigrator` | Ejecución completa de migraciones |

#### Niveles usados

| Nivel | Cuándo |
|-------|--------|
| `Debug` | Operaciones normales (conexión, query, commit) |
| `Information` | Eventos importantes (migraciones aplicadas) |
| `Warning` | Situaciones anómalas (rollback, drift) |
| `Error` | Excepciones y errores |

#### Configuración

```csharp
// Desarrollo: Todo en Debug
if (builder.Environment.IsDevelopment())
{
    builder.Logging.AddFilter("MicroOrmGesg", LogLevel.Debug);
}
else
{
    // Producción: Solo Information y superiores
    builder.Logging.AddFilter("MicroOrmGesg", LogLevel.Information);
}

// Filtro selectivo por componente
builder.Logging.AddFilter("MicroOrmGesg.Repository.DbSession", LogLevel.Debug);
builder.Logging.AddFilter("MicroOrmGesg.Migrations", LogLevel.Information);
```

#### Ejemplo de logs

```
[10:15:32 DBG] Abriendo nueva conexión a la base de datos desde el pool
[10:15:32 DBG] Iniciando transacción con nivel de aislamiento ReadCommitted
[10:15:32 DBG] Ejecutando QueryAsync<Usuario>: SELECT * FROM usuarios WHERE id = @id
[10:15:32 DBG] QueryAsync<Usuario> ejecutado exitosamente
[10:15:32 DBG] Confirmando transacción (COMMIT)
[10:15:32 ERR] Error ejecutando QueryAsync<Producto>: SELECT * FROM productos...
[10:17:21 WRN] Revirtiendo transacción (ROLLBACK)
```

**Importante:** Los logs incluyen SQL pero **NO los valores de los parámetros** (seguridad).

---

### <a id="ref-atributos"></a>Atributos de mapeo

| Atributo | Uso | Ejemplo |
|----------|-----|---------|
| `[Table("nombre")]` | Nombre de tabla | `[Table("users")]` |
| `[Column("nombre")]` | Nombre de columna | `[Column("full_name")]` |
| `[Key]` | Primary key autoincrement | `[Key] public int Id` |
| `[ExplicitKey]` | Primary key manual (no autoincrement) | `[ExplicitKey] public string Code` |
| `[SoftDelete]` | Columna de soft delete | `[SoftDelete] public bool Eliminado` |
| `[Computed]` | Solo lectura (no incluir en INSERT/UPDATE) | `[Computed] public DateTime CreatedAt` |
| `[Write(Include = false)]` | Excluir de escritura | `[Write(Include = false)]` |
| `[Ignore]` | Ignorar completamente | `[Ignore] public string Temp` |
| `[Jsonb]` | Columna JSONB | `[Jsonb] public JObject Data` |

#### Ejemplo completo

```csharp
[Table("usuarios", Schema = "public")]
public class Usuario
{
    [Key]
    public int Id { get; set; }

    public string Nombre { get; set; } = null!;  // → nombre (snake_case automático)

    [Column("email_address")]
    public string Email { get; set; } = null!;   // → email_address (literal)

    [Column("password_hash")]
    public string PasswordHash { get; set; } = null!;

    [Computed]
    public DateTime CreatedAt { get; set; }      // Solo lectura

    [SoftDelete]
    public bool Eliminado { get; set; }          // DELETE → UPDATE eliminado=true

    [Jsonb]
    public JObject? Preferencias { get; set; }   // Tipo JSONB en PostgreSQL

    [Ignore]
    public string TempPassword { get; set; }     // No se mapea a BD
}
```

---

### <a id="ref-paginacion"></a>Paginación y filtrado

#### PageAsync

```csharp
public async Task<Page<Usuario>> ListarAsync(int page, int size, string? busqueda, CancellationToken ct)
{
    await _db.OpenAsync(ct);

    return await _repo.PageAsync(
        _db,
        page: page,                             // Número de página (1-based)
        size: size,                             // Elementos por página
        includeSoftDeleted: false,              // Excluir eliminados
        orderBy: "FechaCreacion",               // Columna para ordenar
        dir: SortDirection.Desc,                // Ascendente/Descendente
        filterField: "Nombre",                  // Filtrar por campo
        filterValue: busqueda,                  // Valor a buscar
        stringMode: StringFilterMode.Contains,  // Equals, Contains, StartsWith, EndsWith
        forceLowerCase: true,                   // Case-insensitive
        ct: ct
    );
}
```

#### Resultado Page<T>

```csharp
public class Page<T>
{
    public List<T> Items { get; init; }        // Elementos de la página actual
    public int Total { get; init; }            // Total de registros
    public int PageNumber { get; init; }       // Página actual
    public int Size { get; init; }             // Tamaño de página
}
```

#### StringFilterMode

| Modo | SQL generado | Ejemplo |
|------|--------------|---------|
| `Equals` | `columna = @valor` | "Juan" |
| `Contains` | `columna ILIKE '%' \|\| @valor \|\| '%'` | Buscar "uan" encuentra "Juan" |
| `StartsWith` | `columna ILIKE @valor \|\| '%'` | "Ju" encuentra "Juan" |
| `EndsWith` | `columna ILIKE '%' \|\| @valor` | "an" encuentra "Juan" |

---

## 📖 Recursos adicionales

### Mejores prácticas

#### 1. Siempre propaga CancellationToken

```csharp
// ✅ BIEN
public async Task<Usuario?> ObtenerAsync(int id, CancellationToken ct)
{
    await _db.OpenAsync(ct);
    return await _repo.GetByIdAsync(_db, id, ct);
}

// ❌ MAL
public async Task<Usuario?> ObtenerAsync(int id)
{
    await _db.OpenAsync();  // Sin ct
    return await _repo.GetByIdAsync(_db, id);  // Sin ct
}
```

#### 2. Usa transacciones para operaciones múltiples

```csharp
// ✅ BIEN - Atómico (todo o nada)
await _db.BeginTransactionAsync(ct: ct);
try
{
    await _repo.InsertAsync(_db, pedido, ct);
    await _query.ExecuteAsync(_db, "UPDATE stock...", ct);
    await _db.CommitAsync(ct);
}
catch
{
    await _db.RollbackAsync(ct);
    throw;
}

// ❌ MAL - No atómico, puede quedar inconsistente
await _repo.InsertAsync(_db, pedido, ct);
await _query.ExecuteAsync(_db, "UPDATE stock...", ct);
```

#### 3. Registra TypeHandlers de JSONB una sola vez

```csharp
// En Program.cs, ANTES de builder.Build()
SqlMapper.AddTypeHandler(new JObjectTypeHandler());
SqlMapper.AddTypeHandler(new JArrayTypeHandler());
SqlMapper.AddTypeHandler(new JTokenTypeHandler());
```

#### 4. Usa logging apropiado por entorno

```csharp
if (builder.Environment.IsDevelopment())
{
    builder.Logging.AddFilter("MicroOrmGesg", LogLevel.Debug);  // Verbose
}
else
{
    builder.Logging.AddFilter("MicroOrmGesg", LogLevel.Warning);  // Solo advertencias
}
```

#### 5. Configura timeout adecuado

```csharp
// Para migraciones pesadas
services.AddPgMigrations(options =>
{
    options.CommandTimeoutSeconds = 300;  // 5 minutos
});
```

#### 6. Nunca modifiques migraciones ya aplicadas

```sql
-- ❌ MAL: Modificar paso existente
-- @step id:001 name:create.usuarios
CREATE TABLE usuarios(
  id serial PRIMARY KEY,
  email text,
  telefono text  -- ← Agregado después
);

-- ✅ BIEN: Crear nuevo paso
-- @step id:002 name:alter.usuarios.add_telefono
ALTER TABLE usuarios ADD COLUMN IF NOT EXISTS telefono text;
```

---

### Troubleshooting

#### "La sesión está cerrada. Llama a OpenAsync() primero"

**Causa:** Intentaste usar `IDbSession` sin llamar a `OpenAsync()`.

**Solución:**
```csharp
await _db.OpenAsync(ct);  // ← Agregar esta línea
await _repo.GetByIdAsync(_db, id, ct);
```

---

#### "Ya existe una transacción activa"

**Causa:** Llamaste a `BeginTransactionAsync()` dos veces sin hacer commit/rollback.

**Solución:**
```csharp
// Asegúrate de commit o rollback antes de iniciar otra
await _db.CommitAsync(ct);
// Ahora puedes iniciar otra transacción
await _db.BeginTransactionAsync(ct: ct);
```

---

#### "El modelo X no tiene definida una clave primaria"

**Causa:** Tu entidad no tiene `[Key]` o `[ExplicitKey]`.

**Solución:**
```csharp
public class Usuario
{
    [Key]  // ← Agregar esto
    public int Id { get; set; }
    // ...
}
```

---

#### "No hay columnas válidas para actualizar con el patch proporcionado"

**Causa:** En `UpdateSetAsync`, todos los campos eran PK, soft delete o no existen.

**Solución:**
```csharp
// Asegúrate de usar nombres correctos (C#, snake_case o [Column])
await _repo.UpdateSetAsync(_db, id, new { Nombre = "Juan" }, ct);  // C#
await _repo.UpdateSetAsync(_db, id, new { nombre = "Juan" }, ct);  // snake_case
```

---

#### "Drift detectado en paso XXX"

**Causa:** El SQL de un paso ya aplicado cambió (diferente checksum).

**Solución:**
1. **Revertir el cambio** en el SQL (si fue un error)
2. **Crear un nuevo paso** con el cambio deseado
3. **Cambiar política** a `DriftPolicy.Reapply` si es seguro (funciones/vistas)

---

#### "Migration file not found"

**Causa:** La ruta al archivo SQL es incorrecta o el archivo no se copia al output.

**Solución:**
```xml
<!-- En tu .csproj -->
<ItemGroup>
  <None Update="scripts\schema.sql">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
</ItemGroup>
```

---

#### Queries JSONB no funcionan

**Causa:** No registraste los TypeHandlers de JSONB.

**Solución:**
```csharp
// En Program.cs
using Dapper;
using MicroOrmGesg.Utils;

SqlMapper.AddTypeHandler(new JObjectTypeHandler());
SqlMapper.AddTypeHandler(new JArrayTypeHandler());
SqlMapper.AddTypeHandler(new JTokenTypeHandler());
```

---

### <a id="ejemplos-completos"></a>Ejemplos completos

Ver la carpeta `examples/` del repositorio:
- `examples/schema.sql` - Script de migraciones completo
- `examples/MigrationUsageExample.cs` - 3 patrones de ejecución de migraciones
- `examples/README.md` - Documentación detallada del sistema de migraciones

---

### <a id="historial"></a>Historial de cambios

- **v1.0.3**: Logging integrado con ILogger<T> en DbSession, DirectQuery y DataFunctionsRepository
- **v1.0.2**: IDirectQuery para queries directas con Dapper compartiendo IDbSession
- **v1.0.1**: Sistema de migraciones completo con checksums SHA-256, advisory locks, detección de drift
- **v1.0.0**: Versión inicial con repositorio genérico, DbSession, funciones PostgreSQL, JSONB

---

## Licencia

[Especificar tu licencia aquí]

## Contribuciones

[Especificar cómo contribuir]

## Soporte

Para reportar issues o solicitar features: [URL del repositorio]
