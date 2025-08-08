# Configuración de MicroOrmGesg

```
dotnet build --configuration Release
```


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
Task<T?> GetByIdAsync(IDbSession session, object id);

Task<List<T>?> GetAllAsync(
    IDbSession session,
    bool includeSoftDeleted = false,
    string? orderBy = null,
    SortDirection dir = SortDirection.Asc,
    int? limit = null,
    int? offset = null);

Task<int> InsertAsync(IDbSession session, T data);
Task<object?> InsertAsyncReturnId(IDbSession session, T data);
Task<bool> UpdateAsync(IDbSession session, T data);
Task<bool> DeleteAsync(IDbSession session, object id);
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

```csharp
// GetById
var usuario = await repo.GetByIdAsync(db, 123);

// GetAll con orden y paginación
var page = await repo.GetAllAsync(db, includeSoftDeleted: false, orderBy: "Usuario",
                                  dir: SortDirection.Desc, limit: 20, offset: 0);

// Insert devolviendo id
var id = await repo.InsertAsyncReturnId(db, new Usuarios {
    Nombre = "Alice",
    Usuario = "alice",
    PasswordHash = "xxx",
    Preferencias = JObject.FromObject(new { tema = "dark", notificaciones = true })
});

// Update
usuario!.Nombre = "Alice Doe";
usuario.Preferencias!["tema"] = "light";
var ok = await repo.UpdateAsync(db, usuario);

// Delete (soft si existe, si no, delete físico)
var deleted = await repo.DeleteAsync(db, id!);
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
