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
- [Ejemplos prácticos](#toc-ejemplos)
- [Notas y buenas prácticas](#toc-notas)
- [Checklist rápido](#toc-checklist)
- [Historial de Cambios](#toc-historial)
- [Anexo (mejor explicado)](#anexo)

<a id="toc-introduccion"></a>
## Introducción
MicroOrmGesg es un micro ORM basado en Dapper y Npgsql que facilita operaciones CRUD, paginación, filtrado y manejo de JSONB en PostgreSQL. Incluye DbSession para gestionar conexiones/transacciones, un repositorio genérico tipado, utilidades de mapeo y ejecución genérica de funciones PostgreSQL.

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

<a id="toc-historial"></a>
## Historial de Cambios (resumen)
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
