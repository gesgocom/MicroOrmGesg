# Configuración de MicroOrmGesg 1.0

```
dotnet build --configuration Release
```

---

# Cambios: CancellationToken en API pública (1.2)

A partir de la versión 1.2:
- Todas las APIs públicas del repositorio (`GetByIdAsync`, `GetAllAsync`, `InsertAsync`, `InsertAsyncReturnId`, `UpdateAsync`, `DeleteAsync`) aceptan `CancellationToken ct = default`.
- Se propaga el `ct` a `DbSession.OpenAsync`, `BeginTransactionAsync`, y a las operaciones Dapper usando `CommandDefinition` (para cancelación cooperativa).
- Esto mejora la cooperación con ASP.NET Core (`HttpContext.RequestAborted`) y evita trabajo innecesario ante cancelaciones.

Ejemplo en controlador ASP.NET Core:
```csharp
[HttpGet("{id:int}")]
public async Task<IActionResult> Get(int id, CancellationToken ct)
{
    await _db.OpenAsync(ct);
    var entity = await _repo.GetByIdAsync(_db, id, ct);
    return entity is null ? NotFound() : Ok(entity);
}
```

---

# Cambios: JSONB explícito en escritura (1.3)

A partir de la versión 1.3:

3) JSONB explícito en escritura
- Para propiedades marcadas con `[Jsonb]` o de tipo `JToken`/`JObject`/`JArray`, se usa `NpgsqlParameter` con `NpgsqlDbType.Jsonb` en operaciones de escritura (INSERT/UPDATE) para no depender de conversiones implícitas de proveedor.
- Se mantienen los TypeHandlers (Newtonsoft + Dapper) para lectura y compatibilidad hacia atrás, de modo que los `SELECT` sigan deserializando automáticamente a `JObject`/`JArray`/`JToken`.

Ejemplo (resumen) del manejo de parámetros en escritura:
```csharp
var jsonString = value != null ? JsonConvert.SerializeObject(value) : null;
var np = new NpgsqlParameter(paramName, NpgsqlDbType.Jsonb)
{
    Value = (object?)jsonString ?? DBNull.Value
};
dp.Add(paramName, np);
```

Nota: Si ya registraste los `TypeHandler` de `JObject`/`JArray`/`JToken`, no necesitas cambios para lectura; esta versión únicamente hace explícito el tipo `jsonb` en escritura para mayor robustez.

---


# DbSession — Gestión de Conexiones y Transacciones en PostgreSQL

`DbSession` es una clase que encapsula la gestión de una conexión única y opcionalmente una transacción para cada petición o unidad de trabajo.  
Está diseñada para **PostgreSQL** usando `NpgsqlDataSource` (pool de conexiones de alto rendimiento) y pensada para funcionar en entornos con **Inyección de Dependencias (DI)** en ASP.NET Core.

---

## Características principales
- **Scoped por request**: una única conexión y transacción por cada petición HTTP (si lo registras como `Scoped`).
- **Compatibilidad async/sync**: expone métodos asíncronos y síncronos para mayor flexibilidad.
- **Control explícito de transacciones**: `BeginTransactionAsync`, `CommitAsync`, `RollbackAsync`.
- **Liberación segura de recursos**: implementa `IDisposable` e `IAsyncDisposable`.
- **Integración directa con `Npgsql`**: acceso completo a funcionalidades específicas de PostgreSQL.

---

## Registro en el contenedor de servicios (Scoped)

En tu `Program.cs` o `Startup.cs`, añade:

```csharp
// Registrar el DataSource como Singleton (pool de conexiones)
services.AddSingleton(sp =>
{
    var cs = Configuration.GetConnectionString("ServidorSQL") ?? string.Empty;
    var dsBuilder = new NpgsqlDataSourceBuilder(cs);
    dsBuilder.UseNodaTime(); 
    return dsBuilder.Build(); 
});

// Registrar DbSession como Scoped (nueva instancia por request)
services.AddScoped<IDbSession, DbSession>();
```

---

## Ejemplo de uso en un servicio

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
        // Abrir conexión y comenzar transacción
        await _db.OpenAsync(ct);
        await _db.BeginTransactionAsync(ct: ct);

        try
        {
            var sql = "INSERT INTO personas(nombre) VALUES (@nombre)";
            await _db.Connection!.ExecuteAsync(sql, new { nombre }, _db.Transaction);

            // Confirmar transacción
            await _db.CommitAsync(ct);
        }
        catch
        {
            // Deshacer transacción si hay error
            await _db.RollbackAsync(ct);
            throw;
        }
    }
}
```

---

## Ejemplo de uso en un controlador

```csharp
[ApiController]
[Route("api/[controller]")]
public class PersonasController : ControllerBase
{
    private readonly PersonaService _personaService;

    public PersonasController(PersonaService personaService)
    {
        _personaService = personaService;
    }

    [HttpPost]
    public async Task<IActionResult> Crear([FromBody] string nombre)
    {
        await _personaService.CrearPersonaAsync(nombre);
        return Ok(new { message = "Persona creada correctamente" });
    }
}
```

---

## Métodos disponibles en `DbSession`

| Método                       | Descripción |
|------------------------------|-------------|
| `Task<NpgsqlConnection> OpenAsync()` | Abre o reutiliza una conexión desde el pool. |
| `Task BeginTransactionAsync()` | Inicia una transacción en la conexión actual. |
| `Task CommitAsync()`         | Confirma la transacción activa. |
| `Task RollbackAsync()`       | Revierte la transacción activa. |
| `void Close()`               | Cierra manualmente la conexión. |
| `Dispose()` / `DisposeAsync()` | Libera recursos (transacción y conexión). |

---

## Notas importantes
- **Siempre** llama a `BeginTransactionAsync` antes de operaciones que deban confirmarse o revertirse como bloque.
- Si no necesitas transacciones, puedes usar `OpenAsync` y ejecutar consultas directamente.
- El contenedor DI se encargará de llamar a `Dispose()` o `DisposeAsync()` al final del `Scope`.
- No reutilices la misma instancia de `DbSession` entre diferentes peticiones HTTP.

---

# MicroOrmGesg – Repositorio Genérico (Dapper + Npgsql, Newtonsoft)

Guía práctica para usar el repositorio genérico **`DataMicroOrmRepository<T>`** con PostgreSQL, soportando **soft delete**, **paginación/ordenación**, y **campos `jsonb`** con **Newtonsoft.Json**.

---

## 📦 Instalación y registro (startup)

Asegúrate de tener Npgsql y Dapper configurados y el `IDbSession` registrado.

```csharp
// Program.cs / Startup.cs
services.AddNpgsqlDataSource(Configuration.GetConnectionString("ServidorSQL")!, b =>
{
    b.EnableRetryOnFailure();
    b.UseNodaTime();
});

services.AddScoped<IDbSession, DbSession>(); // tu implementación

// Dapper type handlers para JSON (Newtonsoft)
SqlMapper.AddTypeHandler(new MicroOrmGesg.Utils.JObjectTypeHandler());
SqlMapper.AddTypeHandler(new MicroOrmGesg.Utils.JArrayTypeHandler());
SqlMapper.AddTypeHandler(new MicroOrmGesg.Utils.JTokenTypeHandler());
```
> Si no tienes `AddNpgsqlDataSource`, crea el `NpgsqlDataSource` manualmente y regístralo como singleton.

---

## 🧩 Interfaz del repositorio

```csharp
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
```

### Comportamiento de cada método

- **`GetByIdAsync`**: `SELECT` por **clave primaria**. El `SELECT` usa alias de propiedad para que Dapper mapee limpio.
- **`GetAllAsync`**:
    - Filtra automáticamente por **soft delete** si existe (`WHERE eliminado = false`).
    - **Ordenación** por nombre de **propiedad** o por `snake_case` (whitelist; `OrdinalIgnoreCase` en `snake_case`).
    - **Paginación** con `limit` y `offset` parametrizados.
- **`InsertAsync`**: Inserta con columnas **escribibles** (excluye `[Computed]`, `[Key]` salvo `[ExplicitKey]`, y la columna de soft delete). Devuelve filas afectadas.
- **`InsertAsyncReturnId`**: Inserta y devuelve el **id** generado con `RETURNING <pk>`.
- **`UpdateAsync`**: Actualiza por **PK**. **Excluye la columna de soft delete** en `SET`. Devuelve `true` si afectó ≥ 1 fila.
- **`UpdateSetAsync` (nuevo)**: Actualiza por **PK** generando `SET` solo con las propiedades presentes en `patch` (objeto anónimo/DTO o `Dictionary<string,object?>`). Aplica una whitelist por `EntityMap` para evitar columnas inválidas y excluye siempre la columna de **soft delete** y la **PK**.
- **`DeleteAsync`**:
    - Si hay soft delete → `UPDATE ... SET eliminado = true`.
    - Si no hay → `DELETE FROM ...`.

---

## 🏷️ Atributos disponibles

Colócalos en tu proyecto (namespace `MicroOrmGesg`) y úsalos en tus modelos.

```csharp
[AttributeUsage(AttributeTargets.Class)]
public sealed class TableAttribute : Attribute
{
    public TableAttribute(string name) => Name = name;
    public string Name { get; }
    public string? Schema { get; init; }
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class KeyAttribute : Attribute {}                 // PK autoincrement (identity/serial)

[AttributeUsage(AttributeTargets.Property)]
public sealed class ExplicitKeyAttribute : Attribute {}         // PK manual (no generado por DB)

[AttributeUsage(AttributeTargets.Property)]
public sealed class ComputedAttribute : Attribute {}            // Solo lectura (excluir en INSERT/UPDATE)

[AttributeUsage(AttributeTargets.Property)]
public sealed class SoftDeleteAttribute : Attribute {}          // Marca la columna booleana de soft delete

[AttributeUsage(AttributeTargets.Property)]
public sealed class WriteAttribute : Attribute { public bool Include { get; init; } = true; } // excluir en writes

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class ColumnAttribute : Attribute
{
    public ColumnAttribute(string name) { Name = name; }
    public string Name { get; }
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class JsonbAttribute : Attribute {}               // Fuerza tratamiento como jsonb
```

### Ejemplo de modelo

```csharp
using Newtonsoft.Json.Linq;

[Table("usuarios", Schema = "public")]
public sealed class Usuarios
{
    [Key] public int Id { get; set; }

    public string Nombre { get; set; } = default!;
    public string Usuario { get; set; } = default!;

    [Column("password_hash")]
    public string PasswordHash { get; set; } = default!;

    [SoftDelete]
    public bool Eliminado { get; set; }

    // JSONB (opción 1): objetos newtonsoft
    [Jsonb] public JObject? Preferencias { get; set; }  // objeto o array
    [Jsonb] public JToken?  Roles        { get; set; }  // flexible
}
```

> Si la columna en DB no coincide con el nombre de la propiedad, usa `[Column("nombre_columna")]`.

---

## 🧠 Convenciones del `EntityMap`

- **Tabla**: `[Table(Name, Schema)]`. Si no hay, nombre de clase en `snake_case`. Quote por partes → `"schema"."tabla"`.
- **Columnas**: por defecto `snake_case` de la propiedad, pero respeta **`[Column]`**.
- **SELECT**: `columna_real AS "PropiedadCSharp"` para que Dapper mapee sin fricciones.
- **WritableProps**: excluye `[Computed]`, `[Write(Include=false)]`, **PK** salvo `[ExplicitKey]`, y **soft delete** si existe.

---

## 🧾 JSONB con Newtonsoft.Json

### Escritura (INSERT/UPDATE)
Si la propiedad es `JObject/JArray/JToken` **o** lleva `[Jsonb]`, el helper construye parámetros **`NpgsqlDbType.Jsonb`** y serializa con **`JsonConvert`**.

```csharp
private static DynamicParameters BuildWriteParameters<TModel>(EntityMap map, TModel data, bool includeKeyParam = false)
{
    var dp = new DynamicParameters();

    foreach (var p in map.WritableProps)
    {
        var paramName = p.Name;
        var value = p.GetValue(data);

        bool isJsonLike =
            p.GetCustomAttribute<JsonbAttribute>() != null ||
            value is JObject || value is JArray || value is JToken;

        if (isJsonLike)
        {
            var jsonString = value != null ? JsonConvert.SerializeObject(value) : null;
            var np = new NpgsqlParameter(paramName, NpgsqlDbType.Jsonb)
            {
                Value = (object?)jsonString ?? DBNull.Value
            };
            dp.Add(paramName, np);
        }
        else
        {
            dp.Add(paramName, value);
        }
    }

    if (includeKeyParam)
    {
        var keyParamName = map.KeyProperty.Name;
        var keyVal = map.KeyProperty.GetValue(data);
        dp.Add(keyParamName, keyVal);
    }

    return dp;
}
```

### Lectura (SELECT)
Registra **TypeHandlers** para que Dapper deserialice automáticamente a `JObject`/`JArray`/`JToken`:

```csharp
public sealed class JObjectTypeHandler : SqlMapper.TypeHandler<JObject?>
{
    public override JObject? Parse(object value)
        => value is null || value is DBNull ? null : JsonConvert.DeserializeObject<JObject>(value.ToString()!);
    public override void SetValue(IDbDataParameter parameter, JObject? value)
        => parameter.Value = value is null ? DBNull.Value : JsonConvert.SerializeObject(value);
}

// Análogo para JArray y JToken…

SqlMapper.AddTypeHandler(new MicroOrmGesg.Utils.JObjectTypeHandler());
SqlMapper.AddTypeHandler(new MicroOrmGesg.Utils.JArrayTypeHandler());
SqlMapper.AddTypeHandler(new MicroOrmGesg.Utils.JTokenTypeHandler());
```

### Modelado – opciones

**Opción A (recomendada)** – Tipos Newtonsoft directos
```csharp
[Jsonb] public JObject? Preferencias { get; set; }
[Jsonb] public JToken?  Roles        { get; set; }
```
- ✅ Lectura/escritura automática (con handlers).
- ✅ Puedes mutar el JSON como objeto.

**Opción B** – Guardar como `string`
```csharp
[Jsonb] public string? Preferencias { get; set; }
```
- ✅ Escritura correcta como `jsonb`.
- 🔶 Lectura te devuelve la cadena JSON (deserializas cuando lo necesites).

**Opción C** – Tipos CLR (DTOs) serializables
```csharp
public class PreferenciasDto { public string Tema { get; set; } = "light"; }

[Jsonb] public PreferenciasDto? Preferencias { get; set; }
```
- ✅ Con un `TypeHandler<PreferenciasDto>` puedes leer/escribir DTOs tipados.

---

## 🔎 Ejemplos de uso

### Updates parciales y columnas específicas (nuevo)

- Método: `Task<bool> UpdateSetAsync(IDbSession session, object id, object patch, CancellationToken ct = default)`
- Genera `SET` únicamente con las propiedades presentes en `patch` (objeto o `Dictionary<string,object?>`).
- Aplica whitelist por `EntityMap` (propiedad, `snake_case` o `[Column]`); ignora campos no válidos.
- Excluye siempre la PK y la columna de soft delete.
- Maneja correctamente columnas `jsonb` (usando `NpgsqlDbType.Jsonb`).

Ejemplos:

```csharp
// 1) Objeto anónimo / DTO
var ok1 = await repo.UpdateSetAsync(db, id: 10, new { Nombre = "Alice", Usuario = "alice2" }, ct);

// 2) snake_case o nombre exacto de [Column]
var ok2 = await repo.UpdateSetAsync(db, 10, new { password_hash = "xxx" }, ct);

// 3) Diccionario (por ejemplo, desde un payload dinámico)
var patch = new Dictionary<string, object?>
{
    ["Nombre"] = "Alice Doe",
    ["preferencias"] = JObject.FromObject(new { tema = "dark" }) // jsonb
};
var ok3 = await repo.UpdateSetAsync(db, 10, patch, ct);
```

Notas:
- Si ninguna columna es válida tras aplicar la whitelist, lanza `InvalidOperationException`.
- Los nombres de campo admiten: nombre de propiedad C#, su `snake_case` o el nombre exacto definido en `[Column]` (case-insensitive).

### Paginación con total (PageAsync) y conteo (CountAsync)

- Ambos métodos reutilizan la misma lógica de filtros/WHERE que `GetAllAsync` (soft delete, filtros por `filterField`/`filterValue`, `StringFilterMode`, `forceLowerCase`).
- `PageAsync` devuelve un objeto con `{ Items, Total, Page, Size }`.

```csharp
// Total con los mismos filtros
var total = await repo.CountAsync(db,
    includeSoftDeleted: false,
    filterField: "Nombre",
    filterValue: "ali",
    stringMode: StringFilterMode.Contains,
    forceLowerCase: true,
    ct);

// Página 2 de tamaño 10, ordenando por "Usuario" descendente
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

// page.Items -> List<T>
// page.Total -> int (COUNT(*))
// page.Page  -> número de página (1-based)
// page.Size  -> tamaño de página
```

```csharp
// GetById
var usuario = await repo.GetByIdAsync(db, 123, ct);

// GetAll con orden y paginación
var page = await repo.GetAllAsync(db, includeSoftDeleted: false, orderBy: "Usuario",
                                  dir: SortDirection.Desc, limit: 20, offset: 0, ct: ct);

// Insert devolviendo id
var id = await repo.InsertAsyncReturnId(db, new Usuarios {
    Nombre = "Alice",
    Usuario = "alice",
    PasswordHash = "xxx",
    Preferencias = JObject.FromObject(new { tema = "dark", notificaciones = true })
}, ct);

// Update
usuario!.Nombre = "Alice Doe";
usuario.Preferencias!["tema"] = "light";
var ok = await repo.UpdateAsync(db, usuario, ct);

// Delete (soft si existe, si no, delete físico)
var deleted = await repo.DeleteAsync(db, id!, ct);
```

---

## 🧯 Notas y gotchas

- `InsertAsyncReturnId` devuelve `object?` por si tu PK es `int`, `long`, `Guid`, etc.
- Si un nombre de propiedad supera 63 chars (raro), el alias en `SELECT` se trunca.
- `[Column("nombre_en_bd")]` fuerza el nombre exacto de la columna.
- `ORDER BY` ignora campos no válidos (whitelist).

---

## ✅ Checklist rápido

- [ ] Registré `IDbSession` y `NpgsqlDataSource`.
- [ ] Registré los **TypeHandlers** de Newtonsoft para Dapper.
- [ ] Mis modelos usan `[Table]`, `[Key]`, `[Column]` donde corresponde.
- [ ] Para `jsonb`, uso `[Jsonb]` y tipos `JObject/JArray/JToken` o `string` según convenga.
- [ ] Commit/Rollback con `db.BeginTransactionAsync()` en mis controladores/servicios.


# MicroOrmGesg 1.1 — Documentación actualizada

Esta versión amplía las capacidades de **GetAllAsync** para permitir filtros dinámicos por campo y valor, con soporte para diferentes tipos de datos y modos de búsqueda en campos string.

---

## 🚀 Cambios principales respecto a 1.0

- **Nuevo filtrado dinámico en `GetAllAsync`**:
  - Parámetros opcionales `filterField` y `filterValue`.
  - Detección de tipo del valor para construir el filtro adecuado (`=`, `ILIKE`, etc.).
  - Enum `StringFilterMode` para cadenas:
    - `Equals` (igualdad exacta, por defecto).
    - `Contains` (contiene, `ILIKE '%valor%'`).
    - `StartsWith` (comienza con, `ILIKE 'valor%'`).
    - `EndsWith` (termina con, `ILIKE '%valor'`).
  - Protección contra inyecciones con *whitelist* de columnas (solo propiedades válidas o marcadas con `[Column]`).

---

## 📦 Firma del método actualizada

```csharp
Task<List<T>> GetAllAsync(
    IDbSession session,
    bool includeSoftDeleted = false,
    string? orderBy = null,
    SortDirection dir = SortDirection.Asc,
    int? limit = null,
    int? offset = null,
    string? filterField = null,
    object? filterValue = null,
    StringFilterMode stringMode = StringFilterMode.Equals
);
```

---

## 📋 Ejemplos de uso

**1. Filtrado exacto por campo string:**
```csharp
var usuarios = await repo.GetAllAsync(db,
    filterField: "Usuario",
    filterValue: "alice");
```

**2. Filtrado por texto (contiene, case-insensitive):**
```csharp
var usuarios = await repo.GetAllAsync(db,
    filterField: "nombre",
    filterValue: "carla",
    stringMode: StringFilterMode.Contains);
```

**3. Filtrado por campo con `[Column]`:**
```csharp
var usuarios = await repo.GetAllAsync(db,
    filterField: "password_hash",
    filterValue: "xyz");
```

**4. Combinado con orden y paginación:**
```csharp
var usuarios = await repo.GetAllAsync(db,
    includeSoftDeleted: false,
    orderBy: "nombre",
    dir: SortDirection.Desc,
    limit: 20,
    offset: 0,
    filterField: "activo",
    filterValue: true);
```

---

## ⚙️ Implementación de seguridad para filtros

El método `ResolveColumn` asegura que solo se pueda filtrar por columnas conocidas en el modelo:

```csharp
private static string? ResolveColumn(EntityMap map, string field)
{
    var f = field.Trim();

    // 1) Propiedad C#
    var byProp = map.Props.FirstOrDefault(p =>
        string.Equals(p.Name, f, StringComparison.OrdinalIgnoreCase));
    if (byProp != null)
        return byProp.GetCustomAttribute<ColumnAttribute>()?.Name ?? Naming.ToSnake(byProp.Name);

    // 2) snake_case
    var bySnake = map.Props.FirstOrDefault(p =>
        string.Equals(Naming.ToSnake(p.Name), f, StringComparison.OrdinalIgnoreCase));
    if (bySnake != null)
        return bySnake.GetCustomAttribute<ColumnAttribute>()?.Name ?? Naming.ToSnake(bySnake.Name);

    // 3) Nombre exacto en [Column]
    var byColumnAttr = map.Props.FirstOrDefault(p =>
        string.Equals(p.GetCustomAttribute<ColumnAttribute>()?.Name, f, StringComparison.OrdinalIgnoreCase));
    return byColumnAttr?.GetCustomAttribute<ColumnAttribute>()?.Name;
}
```

---

## 📌 Notas y buenas prácticas

- Si el tipo de `filterValue` es `string`, el modo de comparación se controla con `stringMode`.
- Si es `int`, `bool`, `DateTime`, etc., siempre se usará `=`.
- La *whitelist* evita inyecciones SQL verificando el campo contra la definición del modelo.
- Compatible con el resto de características de `GetAllAsync`: soft delete, ordenación y paginación.

---

## ✅ Checklist rápido (añadido para 1.1)

- [ ] Usar siempre `filterField` con nombres de propiedad o `[Column]` válidos.
- [ ] Definir `[Column]` en propiedades que no coincidan con el nombre real de la columna.
- [ ] Probar filtros con todos los `StringFilterMode`.
- [ ] Combinar filtros con `orderBy`, `limit` y `offset` para optimizar consultas grandes.




---

## 🧱 Implementación completa (Repositorio + DI + Servicio/Controlador con PATCH)

A continuación, un ejemplo de punta a punta mostrando cómo implementar y usar `UpdateSetAsync` en un proyecto ASP.NET Core.

### 1) Entidad de ejemplo
```csharp
using Newtonsoft.Json.Linq;
using MicroOrmGesg;

[Table("usuarios", Schema = "public")]
public sealed class Usuarios
{
    [Key] public int Id { get; set; }

    public string Nombre { get; set; } = default!;
    public string Usuario { get; set; } = default!;

    [Column("password_hash")]
    public string PasswordHash { get; set; } = default!;

    [SoftDelete]
    public bool Eliminado { get; set; }

    [Jsonb] public JObject? Preferencias { get; set; }
}
```

### 2) Registro en DI (repositorio e infraestructura)
```csharp
// Program.cs / Startup.cs
services.AddScoped<IDbSession, DbSession>();

// Puedes registrar directamente el repositorio genérico para la entidad
services.AddScoped<MicroOrmGesg.Interfaces.IDataMicroOrm<Usuarios>, MicroOrmGesg.Repository.DataMicroOrmRepository<Usuarios>>();

// TypeHandlers de Newtonsoft para Dapper (lectura de json)
SqlMapper.AddTypeHandler(new MicroOrmGesg.Utils.JObjectTypeHandler());
SqlMapper.AddTypeHandler(new MicroOrmGesg.Utils.JArrayTypeHandler());
SqlMapper.AddTypeHandler(new MicroOrmGesg.Utils.JTokenTypeHandler());
```

Opcionalmente, si prefieres un repositorio concreto para añadir métodos específicos, crea una clase vacía que herede:
```csharp
using MicroOrmGesg.Repository;
using MicroOrmGesg.Interfaces;

public sealed class UsuariosRepository : DataMicroOrmRepository<Usuarios>, IDataMicroOrm<Usuarios>
{
    // Métodos específicos si los necesitas…
}
```
Y registra `UsuariosRepository` en lugar del genérico.

### 3) DTO para PATCH (opcional)
```csharp
public sealed class UsuarioPatchDto
{
    public string? Nombre { get; set; }
    public string? Usuario { get; set; }
    public string? PasswordHash { get; set; }
    public JObject? Preferencias { get; set; } // jsonb
}
```

### 4) Servicio de dominio (usa IDbSession + repositorio)
```csharp
using MicroOrmGesg.Interfaces;

public sealed class UsuariosService
{
    private readonly IDbSession _db;
    private readonly IDataMicroOrm<Usuarios> _repo;

    public UsuariosService(IDbSession db, IDataMicroOrm<Usuarios> repo)
    {
        _db = db;
        _repo = repo;
    }

    public async Task<bool> PatchAsync(int id, object patch, CancellationToken ct)
    {
        await _db.OpenAsync(ct);
        await _db.BeginTransactionAsync(ct: ct);
        try
        {
            var ok = await _repo.UpdateSetAsync(_db, id, patch, ct);
            await _db.CommitAsync(ct);
            return ok;
        }
        catch
        {
            await _db.RollbackAsync(ct);
            throw;
        }
    }
}
```

### 5) Controlador: endpoints con PATCH usando DTO o Dictionary
```csharp
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;

[ApiController]
[Route("api/[controller]")]
public sealed class UsuariosController : ControllerBase
{
    private readonly UsuariosService _service;
    private readonly IDbSession _db; // Para lecturas posteriores si quieres
    private readonly MicroOrmGesg.Interfaces.IDataMicroOrm<Usuarios> _repo;

    public UsuariosController(UsuariosService service, IDbSession db, MicroOrmGesg.Interfaces.IDataMicroOrm<Usuarios> repo)
    {
        _service = service;
        _db = db;
        _repo = repo;
    }

    // PATCH con DTO tipado
    [HttpPatch("{id:int}")]
    public async Task<IActionResult> PatchDto(int id, [FromBody] UsuarioPatchDto dto, CancellationToken ct)
    {
        var ok = await _service.PatchAsync(id, dto, ct);
        if (!ok) return NotFound();

        await _db.OpenAsync(ct);
        var updated = await _repo.GetByIdAsync(_db, id, ct);
        return Ok(updated);
    }

    // PATCH con Dictionary (flexible)
    [HttpPatch("{id:int}/dynamic")]
    public async Task<IActionResult> PatchDynamic(int id, [FromBody] Dictionary<string, object?> patch, CancellationToken ct)
    {
        // Ejemplo: puedes enviar keys como "Nombre", "password_hash" o "preferencias" (jsonb)
        var ok = await _service.PatchAsync(id, patch, ct);
        if (!ok) return NotFound();

        await _db.OpenAsync(ct);
        var updated = await _repo.GetByIdAsync(_db, id, ct);
        return Ok(updated);
    }
}
```

Notas importantes:
- Los nombres de campos aceptados en `patch` son el nombre de propiedad C#, su `snake_case`, o el nombre exacto definido en `[Column]` (case-insensitive).
- La PK y la columna de soft delete se excluyen automáticamente del `SET`.
- Para valores `jsonb`, puedes pasar `JObject/JArray/JToken` o cualquier objeto serializable (siempre que marques la propiedad con `[Jsonb]`).

# Configuración de MicroOrmGesg 1.0

```
dotnet build --configuration Release
```

---

# Cambios: CancellationToken en API pública (1.2)

A partir de la versión 1.2:
- Todas las APIs públicas del repositorio (`GetByIdAsync`, `GetAllAsync`, `InsertAsync`, `InsertAsyncReturnId`, `UpdateAsync`, `DeleteAsync`) aceptan `CancellationToken ct = default`.
- Se propaga el `ct` a `DbSession.OpenAsync`, `BeginTransactionAsync`, y a las operaciones Dapper usando `CommandDefinition` (para cancelación cooperativa).
- Esto mejora la cooperación con ASP.NET Core (`HttpContext.RequestAborted`) y evita trabajo innecesario ante cancelaciones.

Ejemplo en controlador ASP.NET Core:
```csharp
[HttpGet("{id:int}")]
public async Task<IActionResult> Get(int id, CancellationToken ct)
{
    await _db.OpenAsync(ct);
    var entity = await _repo.GetByIdAsync(_db, id, ct);
    return entity is null ? NotFound() : Ok(entity);
}
```

---

# Cambios: JSONB explícito en escritura (1.3)

A partir de la versión 1.3:

3) JSONB explícito en escritura
- Para propiedades marcadas con `[Jsonb]` o de tipo `JToken`/`JObject`/`JArray`, se usa `NpgsqlParameter` con `NpgsqlDbType.Jsonb` en operaciones de escritura (INSERT/UPDATE) para no depender de conversiones implícitas de proveedor.
- Se mantienen los TypeHandlers (Newtonsoft + Dapper) para lectura y compatibilidad hacia atrás, de modo que los `SELECT` sigan deserializando automáticamente a `JObject`/`JArray`/`JToken`.

Ejemplo (resumen) del manejo de parámetros en escritura:
```csharp
var jsonString = value != null ? JsonConvert.SerializeObject(value) : null;
var np = new NpgsqlParameter(paramName, NpgsqlDbType.Jsonb)
{
    Value = (object?)jsonString ?? DBNull.Value
};
dp.Add(paramName, np);
```

Nota: Si ya registraste los `TypeHandler` de `JObject`/`JArray`/`JToken`, no necesitas cambios para lectura; esta versión únicamente hace explícito el tipo `jsonb` en escritura para mayor robustez.

---


# DbSession — Gestión de Conexiones y Transacciones en PostgreSQL

`DbSession` es una clase que encapsula la gestión de una conexión única y opcionalmente una transacción para cada petición o unidad de trabajo.  
Está diseñada para **PostgreSQL** usando `NpgsqlDataSource` (pool de conexiones de alto rendimiento) y pensada para funcionar en entornos con **Inyección de Dependencias (DI)** en ASP.NET Core.

---

## Características principales
- **Scoped por request**: una única conexión y transacción por cada petición HTTP (si lo registras como `Scoped`).
- **Compatibilidad async/sync**: expone métodos asíncronos y síncronos para mayor flexibilidad.
- **Control explícito de transacciones**: `BeginTransactionAsync`, `CommitAsync`, `RollbackAsync`.
- **Liberación segura de recursos**: implementa `IDisposable` e `IAsyncDisposable`.
- **Integración directa con `Npgsql`**: acceso completo a funcionalidades específicas de PostgreSQL.

---

## Registro en el contenedor de servicios (Scoped)

En tu `Program.cs` o `Startup.cs`, añade:

```csharp
// Registrar el DataSource como Singleton (pool de conexiones)
services.AddSingleton(sp =>
{
    var cs = Configuration.GetConnectionString("ServidorSQL") ?? string.Empty;
    var dsBuilder = new NpgsqlDataSourceBuilder(cs);
    dsBuilder.UseNodaTime(); 
    return dsBuilder.Build(); 
});

// Registrar DbSession como Scoped (nueva instancia por request)
services.AddScoped<IDbSession, DbSession>();
```

---

## Ejemplo de uso en un servicio

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
        // Abrir conexión y comenzar transacción
        await _db.OpenAsync(ct);
        await _db.BeginTransactionAsync(ct: ct);

        try
        {
            var sql = "INSERT INTO personas(nombre) VALUES (@nombre)";
            await _db.Connection!.ExecuteAsync(sql, new { nombre }, _db.Transaction);

            // Confirmar transacción
            await _db.CommitAsync(ct);
        }
        catch
        {
            // Deshacer transacción si hay error
            await _db.RollbackAsync(ct);
            throw;
        }
    }
}
```

---

## Ejemplo de uso en un controlador

```csharp
[ApiController]
[Route("api/[controller]")]
public class PersonasController : ControllerBase
{
    private readonly PersonaService _personaService;

    public PersonasController(PERSONAService personaService)
    {
        _personaService = personaService;
    }

    [HttpPost]
    public async Task<IActionResult> Crear([FromBody] string nombre)
    {
        await _personaService.CrearPersonaAsync(nombre);
        return Ok(new { message = "Persona creada correctamente" });
    }
}
```

---

## Métodos disponibles en `DbSession`

| Método                       | Descripción |
|------------------------------|-------------|
| `Task<NpgsqlConnection> OpenAsync()` | Abre o reutiliza una conexión desde el pool. |
| `Task BeginTransactionAsync()` | Inicia una transacción en la conexión actual. |
| `Task CommitAsync()`         | Confirma la transacción activa. |
| `Task RollbackAsync()`       | Revierte la transacción activa. |
| `void Close()`               | Cierra manualmente la conexión. |
| `Dispose()` / `DisposeAsync()` | Libera recursos (transacción y conexión). |

---

## MicroOrmGesg – Repositorio Genérico (Dapper + Npgsql, Newtonsoft)

Guía práctica para usar el repositorio genérico **`DataMicroOrmRepository<T>`** con PostgreSQL, soportando **soft delete**, **paginación/ordenación**, y **campos `jsonb`** con **Newtonsoft.Json**.

---

## 📦 Instalación y registro (startup)

Asegúrate de tener Npgsql y Dapper configurados y el `IDbSession` registrado.

```csharp
// Program.cs / Startup.cs
services.AddNpgsqlDataSource(Configuration.GetConnectionString("ServidorSQL")!, b =>
{
    b.EnableRetryOnFailure();
    b.UseNodaTime();
});

services.AddScoped<IDbSession, DbSession>(); // tu implementación

// Dapper type handlers para JSON (Newtonsoft)
SqlMapper.AddTypeHandler(new MicroOrmGesg.Utils.JObjectTypeHandler());
SqlMapper.AddTypeHandler(new MicroOrmGesg.Utils.JArrayTypeHandler());
SqlMapper.AddTypeHandler(new MicroOrmGesg.Utils.JTokenTypeHandler());
```
> Si no tienes `AddNpgsqlDataSource`, crea el `NpgsqlDataSource` manualmente y regístralo como singleton.

---

## 🧩 Interfaz del repositorio

```csharp
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
```

### Comportamiento de cada método

- **`GetByIdAsync`**: `SELECT` por **clave primaria**. El `SELECT` usa alias de propiedad para que Dapper mapee limpio.
- **`GetAllAsync`**:
    - Filtra automáticamente por **soft delete** si existe (`WHERE eliminado = false`).
    - **Ordenación** por nombre de **propiedad** o por `snake_case` (whitelist; `OrdinalIgnoreCase` en `snake_case`).
    - **Paginación** con `limit` y `offset` parametrizados.
- **`InsertAsync`**: Inserta con columnas **escribibles** (excluye `[Computed]`, `[Key]` salvo `[ExplicitKey]`, y la columna de soft delete). Devuelve filas afectadas.
- **`InsertAsyncReturnId`**: Inserta y devuelve el **id** generado con `RETURNING <pk>`.
- **`UpdateAsync`**: Actualiza por **PK**. **Excluye la columna de soft delete** en `SET`. Devuelve `true` si afectó ≥ 1 fila.
- **`UpdateSetAsync` (nuevo)**: Actualiza por **PK** generando `SET` solo con las propiedades presentes en `patch` (objeto anónimo/DTO o `Dictionary<string,object?>`). Aplica una whitelist por `EntityMap` para evitar columnas inválidas y excluye siempre la columna de **soft delete** y la **PK**.
- **`DeleteAsync`**:
    - Si hay soft delete → `UPDATE ... SET eliminado = true`.
    - Si no hay → `DELETE FROM ...`.

---

## 🏷️ Atributos disponibles

Colócalos en tu proyecto (namespace `MicroOrmGesg`) y úsalos en tus modelos.

```csharp
[AttributeUsage(AttributeTargets.Class)]
public sealed class TableAttribute : Attribute
{
    public TableAttribute(string name) => Name = name;
    public string Name { get; }
    public string? Schema { get; init; }
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class KeyAttribute : Attribute {}                 // PK autoincrement (identity/serial)

[AttributeUsage(AttributeTargets.Property)]
public sealed class ExplicitKeyAttribute : Attribute {}         // PK manual (no generado por DB)

[AttributeUsage(AttributeTargets.Property)]
public sealed class ComputedAttribute : Attribute {}            // Solo lectura (excluir en INSERT/UPDATE)

[AttributeUsage(AttributeTargets.Property)]
public sealed class SoftDeleteAttribute : Attribute {}          // Marca la columna booleana de soft delete

[AttributeUsage(AttributeTargets.Property)]
public sealed class WriteAttribute : Attribute { public bool Include { get; init; } = true; } // excluir en writes

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class ColumnAttribute : Attribute
{
    public ColumnAttribute(string name) { Name = name; }
    public string Name { get; }
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class JsonbAttribute : Attribute {}               // Fuerza tratamiento como jsonb
```

### Ejemplo de modelo

```csharp
using Newtonsoft.Json.Linq;

[Table("usuarios", Schema = "public")]
public sealed class Usuarios
{
    [Key] public int Id { get; set; }

    public string Nombre { get; set; } = default!;
    public string Usuario { get; set; } = default!;

    [Column("password_hash")]
    public string PasswordHash { get; set; } = default!;

    [SoftDelete]
    public bool Eliminado { get; set; }

    // JSONB (opción 1): objetos newtonsoft
    [Jsonb] public JObject? Preferencias { get; set; }  // objeto o array
    [Jsonb] public JToken?  Roles        { get; set; }  // flexible
}
```

> Si la columna en DB no coincide con el nombre de la propiedad, usa `[Column("nombre_columna")]`.

---

## 🧠 Convenciones del `EntityMap`

- **Tabla**: `[Table(Name, Schema)]`. Si no hay, nombre de clase en `snake_case`. Quote por partes → `"schema"."tabla"`.
- **Columnas**: por defecto `snake_case` de la propiedad, pero respeta **`[Column]`**.
- **SELECT**: `columna_real AS "PropiedadCSharp"` para que Dapper mapee sin fricciones.
- **WritableProps**: excluye `[Computed]`, `[Write(Include=false)]`, **PK** salvo `[ExplicitKey]`, y **soft delete** si existe.

---

## 🧾 JSONB con Newtonsoft.Json

### Escritura (INSERT/UPDATE)
Si la propiedad es `JObject/JArray/JToken` **o** lleva `[Jsonb]`, el helper construye parámetros **`NpgsqlDbType.Jsonb`** y serializa con **`JsonConvert`**.

```csharp
private static DynamicParameters BuildWriteParameters<TModel>(EntityMap map, TModel data, bool includeKeyParam = false)
{
    var dp = new DynamicParameters();

    foreach (var p in map.WritableProps)
    {
        var paramName = p.Name;
        var value = p.GetValue(data);

        bool isJsonLike =
            p.GetCustomAttribute<JsonbAttribute>() != null ||
            value is JObject || value is JArray || value is JToken;

        if (isJsonLike)
        {
            var jsonString = value != null ? JsonConvert.SerializeObject(value) : null;
            var np = new NpgsqlParameter(paramName, NpgsqlDbType.Jsonb)
            {
                Value = (object?)jsonString ?? DBNull.Value
            };
            dp.Add(paramName, np);
        }
        else
        {
            dp.Add(paramName, value);
        }
    }

    if (includeKeyParam)
    {
        var keyParamName = map.KeyProperty.Name;
        var keyVal = map.KeyProperty.GetValue(data);
        dp.Add(keyParamName, keyVal);
    }

    return dp;
}
```

### Lectura (SELECT)
Registra **TypeHandlers** para que Dapper deserialice automáticamente a `JObject`/`JArray`/`JToken`:

```csharp
public sealed class JObjectTypeHandler : SqlMapper.TypeHandler<JObject?>
{
    public override JObject? Parse(object value)
        => value is null || value is DBNull ? null : JsonConvert.DeserializeObject<JObject>(value.ToString()!);
    public override void SetValue(IDbDataParameter parameter, JObject? value)
        => parameter.Value = value is null ? DBNull.Value : JsonConvert.SerializeObject(value);
}

// Análogo para JArray y JToken…

SqlMapper.AddTypeHandler(new MicroOrmGesg.Utils.JObjectTypeHandler());
SqlMapper.AddTypeHandler(new MicroOrmGesg.Utils.JArrayTypeHandler());
SqlMapper.AddTypeHandler(new MicroOrmGesg.Utils.JTokenTypeHandler());
```

### Modelado – opciones

**Opción A (recomendada)** – Tipos Newtonsoft directos
```csharp
[Jsonb] public JObject? Preferencias { get; set; }
[Jsonb] public JToken?  Roles        { get; set; }
```
- ✅ Lectura/escritura automática (con handlers).
- ✅ Puedes mutar el JSON como objeto.

**Opción B** – Guardar como `string`
```csharp
[Jsonb] public string? Preferencias { get; set; }
```
- ✅ Escritura correcta como `jsonb`.
- 🔶 Lectura te devuelve la cadena JSON (deserializas cuando lo necesites).

**Opción C** – Tipos CLR (DTOs) serializables
```csharp
public class PreferenciasDto { public string Tema { get; set; } = "light"; }

[Jsonb] public PreferenciasDto? Preferencias { get; set; }
```
- ✅ Con un `TypeHandler<PreferenciasDto>` puedes leer/escribir DTOs tipados.

---

## 🔎 Ejemplos de uso

### Updates parciales y columnas específicas (nuevo)

- Método: `Task<bool> UpdateSetAsync(IDbSession session, object id, object patch, CancellationToken ct = default)`
- Genera `SET` únicamente con las propiedades presentes en `patch` (objeto o `Dictionary<string,object?>`).
- Aplica whitelist por `EntityMap` (propiedad, `snake_case` o `[Column]`); ignora campos no válidos.
- Excluye siempre la PK y la columna de soft delete.
- Maneja correctamente columnas `jsonb` (usando `NpgsqlDbType.Jsonb`).

Ejemplos:

```csharp
// 1) Objeto anónimo / DTO
var ok1 = await repo.UpdateSetAsync(db, id: 10, new { Nombre = "Alice", Usuario = "alice2" }, ct);

// 2) snake_case o nombre exacto de [Column]
var ok2 = await repo.UpdateSetAsync(db, 10, new { password_hash = "xxx" }, ct);

// 3) Diccionario (por ejemplo, desde un payload dinámico)
var patch = new Dictionary<string, object?>
{
    ["Nombre"] = "Alice Doe",
    ["preferencias"] = JObject.FromObject(new { tema = "dark" }) // jsonb
};
var ok3 = await repo.UpdateSetAsync(db, 10, patch, ct);
```

Notas:
- Si ninguna columna es válida tras aplicar la whitelist, lanza `InvalidOperationException`.
- Los nombres de campo admiten: nombre de propiedad C#, su `snake_case` o el nombre exacto definido en `[Column]` (case-insensitive).

### Paginación con total (PageAsync) y conteo (CountAsync)

- Ambos métodos reutilizan la misma lógica de filtros/WHERE que `GetAllAsync` (soft delete, filtros por `filterField`/`filterValue`, `StringFilterMode`, `forceLowerCase`).
- `PageAsync` devuelve un objeto con `{ Items, Total, Page, Size }`.

```csharp
// Total con los mismos filtros
var total = await repo.CountAsync(db,
    includeSoftDeleted: false,
    filterField: "Nombre",
    filterValue: "ali",
    stringMode: StringFilterMode.Contains,
    forceLowerCase: true,
    ct);

// Página 2 de tamaño 10, ordenando por "Usuario" descendente
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

// page.Items -> List<T>
// page.Total -> int (COUNT(*))
// page.Page  -> número de página (1-based)
// page.Size  -> tamaño de página
```

```csharp
// GetById
var usuario = await repo.GetByIdAsync(db, 123, ct);

// GetAll con orden y paginación
var page = await repo.GetAllAsync(db, includeSoftDeleted: false, orderBy: "Usuario",
                                  dir: SortDirection.Desc, limit: 20, offset: 0, ct: ct);

// Insert devolviendo id
var id = await repo.InsertAsyncReturnId(db, new Usuarios {
    Nombre = "Alice",
    Usuario = "alice",
    PasswordHash = "xxx",
    Preferencias = JObject.FromObject(new { tema = "dark", notificaciones = true })
}, ct);

// Update
usuario!.Nombre = "Alice Doe";
usuario.Preferencias!["tema"] = "light";
var ok = await repo.UpdateAsync(db, usuario, ct);

// Delete (soft si existe, si no, delete físico)
var deleted = await repo.DeleteAsync(db, id!, ct);
```

---

## 🧯 Notas y gotchas

- `InsertAsyncReturnId` devuelve `object?` por si tu PK es `int`, `long`, `Guid`, etc.
- Si un nombre de propiedad supera 63 chars (raro), el alias en `SELECT` se trunca.
- `[Column("nombre_en_bd")]` fuerza el nombre exacto de la columna.
- `ORDER BY` ignora campos no válidos (whitelist).

---

## ✅ Checklist rápido

- [ ] Registré `IDbSession` y `NpgsqlDataSource`.
- [ ] Registré los **TypeHandlers** de Newtonsoft para Dapper.
- [ ] Mis modelos usan `[Table]`, `[Key]`, `[Column]` donde corresponde.
- [ ] Para `jsonb`, uso `[Jsonb]` y tipos `JObject/JArray/JToken` o `string` según convenga.
- [ ] Commit/Rollback con `db.BeginTransactionAsync()` en mis controladores/servicios.


# MicroOrmGesg 1.1 — Documentación actualizada

Esta versión amplía las capacidades de **GetAllAsync** para permitir filtros dinámicos por campo y valor, con soporte para diferentes tipos de datos y modos de búsqueda en campos string.

---

## 🚀 Cambios principales respecto a 1.0

- **Nuevo filtrado dinámico en `GetAllAsync`**:
  - Parámetros opcionales `filterField` y `filterValue`.
  - Detección de tipo del valor para construir el filtro adecuado (`=`, `ILIKE`, etc.).
  - Enum `StringFilterMode` para cadenas:
    - `Equals` (igualdad exacta, por defecto).
    - `Contains` (contiene, `ILIKE '%valor%'`).
    - `StartsWith` (comienza con, `ILIKE 'valor%'`).
    - `EndsWith` (termina con, `ILIKE '%valor'`).
  - Protección contra inyecciones con *whitelist* de columnas (solo propiedades válidas o marcadas con `[Column]`).

---

## 📦 Firma del método actualizada

```csharp
Task<List<T>> GetAllAsync(
    IDbSession session,
    bool includeSoftDeleted = false,
    string? orderBy = null,
    SortDirection dir = SortDirection.Asc,
    int? limit = null,
    int? offset = null,
    string? filterField = null,
    object? filterValue = null,
    StringFilterMode stringMode = StringFilterMode.Equals
);
```

---

## 📋 Ejemplos de uso

**1. Filtrado exacto por campo string:**
```csharp
var usuarios = await repo.GetAllAsync(db,
    filterField: "Usuario",
    filterValue: "alice");
```

**2. Filtrado por texto (contiene, case-insensitive):**
```csharp
var usuarios = await repo.GetAllAsync(db,
    filterField: "nombre",
    filterValue: "carla",
    stringMode: StringFilterMode.Contains);
```

**3. Filtrado por campo con `[Column]`:**
```csharp
var usuarios = await repo.GetAllAsync(db,
    filterField: "password_hash",
    filterValue: "xyz");
```

**4. Combinado con orden y paginación:**
```csharp
var usuarios = await repo.GetAllAsync(db,
    includeSoftDeleted: false,
    orderBy: "nombre",
    dir: SortDirection.Desc,
    limit: 20,
    offset: 0,
    filterField: "activo",
    filterValue: true);
```

---

## ⚙️ Implementación de seguridad para filtros

El método `ResolveColumn` asegura que solo se pueda filtrar por columnas conocidas en el modelo:

```csharp
private static string? ResolveColumn(EntityMap map, string field)
{
    var f = field.Trim();

    // 1) Propiedad C#
    var byProp = map.Props.FirstOrDefault(p =>
        string.Equals(p.Name, f, StringComparison.OrdinalIgnoreCase));
    if (byProp != null)
        return byProp.GetCustomAttribute<ColumnAttribute>()?.Name ?? Naming.ToSnake(byProp.Name);

    // 2) snake_case
    var bySnake = map.Props.FirstOrDefault(p =>
        string.Equals(Naming.ToSnake(p.Name), f, StringComparison.OrdinalIgnoreCase));
    if (bySnake != null)
        return bySnake.GetCustomAttribute<ColumnAttribute>()?.Name ?? Naming.ToSnake(bySnake.Name);

    // 3) Nombre exacto en [Column]
    var byColumnAttr = map.Props.FirstOrDefault(p =>
        string.Equals(p.GetCustomAttribute<ColumnAttribute>()?.Name, f, StringComparison.OrdinalIgnoreCase));
    return byColumnAttr?.GetCustomAttribute<ColumnAttribute>()?.Name;
}
```

---

## 📌 Notas y buenas prácticas

- Si el tipo de `filterValue` es `string`, el modo de comparación se controla con `stringMode`.
- Si es `int`, `bool`, `DateTime`, etc., siempre se usará `=`.
- La *whitelist* evita inyecciones SQL verificando el campo contra la definición del modelo.
- Compatible con el resto de características de `GetAllAsync`: soft delete, ordenación y paginación.

---

## ✅ Checklist rápido (añadido para 1.1)

- [ ] Usar siempre `filterField` con nombres de propiedad o `[Column]` válidos.
- [ ] Definir `[Column]` en propiedades que no coincidan con el nombre real de la columna.
- [ ] Probar filtros con todos los `StringFilterMode`.
- [ ] Combinar filtros con `orderBy`, `limit` y `offset` para optimizar consultas grandes.


---

## 🏷️ Validación de modelos con atributos (nuevo)

A partir de esta versión se incluyen atributos de validación simples con mensajes personalizados y un validador por reflexión.

### Atributos

- `[Required(Message = "...")]`: Campo obligatorio. Para strings, no permite `null`, vacío o solo espacios.
- `[StringLength(min, max, Message = "...")]`: Longitud mínima y/o máxima. Si `min` o `max` son `0`, ese límite se ignora.

```csharp
public sealed class UsuarioDto
{
    [Required(Message = "El nombre es obligatorio")]
    [StringLength(min: 2, max: 50, Message = "El nombre debe tener entre 2 y 50 caracteres")]
    public string? Nombre { get; set; }

    [Required]
    [StringLength(max: 20)] // solo máximo
    public string? Usuario { get; set; }
}
```

### Validación

Usa el helper `ModelValidator`:

```csharp
var dto = new UsuarioDto { Nombre = " ", Usuario = "un_usuario_muy_muy_muy_largo" };
var errores = MicroOrmGesg.Utils.ModelValidator.Validate(dto);

if (errores.Any())
{
    // Cada error tiene PropertyName y Message
    foreach (var e in errores)
        Console.WriteLine($"{e.PropertyName}: {e.Message}");
}
```

Reglas:
- `[Required]` rechaza `null` y, en strings, vacío o espacios.
- `[StringLength]` aplica solo a `string`. `min=0` o `max=0` → límite ignorado.
- Si no defines `Message`, se generan mensajes por defecto con el nombre de la propiedad y los límites.

---
