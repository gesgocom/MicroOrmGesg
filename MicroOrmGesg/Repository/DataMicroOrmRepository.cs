using MicroOrmGesg.Interfaces;
using System.Data;
using System.Reflection;
using System.Threading;
using Dapper;
using MicroOrmGesg.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Npgsql;

namespace MicroOrmGesg.Repository;

public class DataMicroOrmRepository<T> : IDataMicroOrm<T> where T : class
{
    public async Task<T?> GetByIdAsync(IDbSession session, object id, CancellationToken ct = default)
    {
        IDbConnection conn = session.Connection!;
        var map = EntityMap.For(typeof(T));

        // WHERE por clave primaria
        var sql = $"select {map.SelectColsCsv} from {map.TableFullQuoted} where {Naming.Quote(map.KeyColumn)} = @id";
        var cmd = new CommandDefinition(sql, new { id }, session.Transaction, cancellationToken: ct);
        return await conn.QuerySingleOrDefaultAsync<T>(cmd);
    }

    public async Task<List<T>> GetAllAsync(IDbSession session, bool includeSoftDeleted = false, string? orderBy = null,
        SortDirection dir = SortDirection.Asc, int? limit = null, int? offset = null, string? filterField = null,
        object? filterValue = null, StringFilterMode stringMode = StringFilterMode.Equals,
        bool forceLowerCase = false, CancellationToken ct = default)
    {
        if (session.Connection is null)
            throw new InvalidOperationException("La sesión está cerrada. Llama a OpenAsync() primero.");

        IDbConnection conn = session.Connection!;
        var map = EntityMap.For(typeof(T));

        // WHERE y parámetros compartidos
        var (where, parameters) = BuildWhereAndParams(map, includeSoftDeleted, filterField, filterValue, stringMode, forceLowerCase);

        // ORDER BY
        string order = BuildOrderClause(map, orderBy, dir);

        // LIMIT / OFFSET
        string paging = string.Empty;
        if (limit is { } l && l > 0)
        {
            paging += " limit @limit";
            parameters.Add("limit", l);
        }

        if (offset is { } o && o >= 0)
        {
            paging += " offset @offset";
            parameters.Add("offset", o);
        }

        // SQL final
        string sql = $"select {map.SelectColsCsv} from {map.TableFullQuoted}{where}{order}{paging}";

        var cmd = new CommandDefinition(sql, parameters, session.Transaction, cancellationToken: ct);
        var result = await conn.QueryAsync<T>(cmd);
        return result.AsList();
    }

    public async Task<int> InsertAsync(IDbSession session, T data, CancellationToken ct = default)
    {
        if (session.Connection is null)
            throw new InvalidOperationException("La sesión está cerrada. Llama a OpenAsync() primero.");

        IDbConnection conn = session.Connection;
        var map = EntityMap.For(typeof(T));

        // columnas y parámetros para INSERT
        var (colsCsv, paramsCsv) = map.GetInsertColumnsAndParams(data);

        // INSERT simple (sin RETURNING) -> devuelve filas afectadas
        var sql = $"insert into {map.TableFullQuoted} ({colsCsv}) values ({paramsCsv})";

        // ⬇️ parámetros con jsonb cuando aplique
        var dp = BuildWriteParameters(map, data);

        var cmd = new CommandDefinition(sql, dp, session.Transaction, cancellationToken: ct);
        var affected = await conn.ExecuteAsync(cmd);
        if (affected == 0)
            throw new InvalidOperationException("No se insertó ningún registro. Verifica los datos.");
        return affected;
    }

    public async Task<object?> InsertAsyncReturnId(IDbSession session, T data, CancellationToken ct = default)
    {
        if (session.Connection is null)
            throw new InvalidOperationException("La sesión está cerrada. Llama a OpenAsync() primero.");

        IDbConnection conn = session.Connection!;
        var map = EntityMap.For(typeof(T));

        var (colsCsv, paramsCsv) = map.GetInsertColumnsAndParams(data);

        var sql =
            $"insert into {map.TableFullQuoted} ({colsCsv}) values ({paramsCsv}) returning {Naming.Quote(map.KeyColumn)}";

        // ⬇️ parámetros con jsonb cuando aplique
        var dp = BuildWriteParameters(map, data);

        var cmd = new CommandDefinition(sql, dp, session.Transaction, cancellationToken: ct);
        var id = await conn.ExecuteScalarAsync<object>(cmd);
        if (id is null)
            throw new InvalidOperationException("No se pudo obtener el ID del registro insertado.");
        return id;
    }

    public async Task<bool> UpdateAsync(IDbSession session, T data, CancellationToken ct = default)
    {
        if (session.Connection is null)
            throw new InvalidOperationException("La sesión está cerrada. Llama a OpenAsync() primero.");

        IDbConnection conn = session.Connection!;
        var map = EntityMap.For(typeof(T));

        // Construimos el SET: "columna_real" = @NombrePropiedad
        if (map.WritableProps.Count == 0)
            throw new InvalidOperationException($"No hay columnas escribibles para {typeof(T).Name}.");

        // Resolver nombre de columna respetando [Column("...")]
        static string GetCol(PropertyInfo p)
            => p.GetCustomAttribute<ColumnAttribute>()?.Name ?? Naming.ToSnake(p.Name);

        var setClause = string.Join(", ",
            map.WritableProps.Select(p => $"{Naming.Quote(GetCol(p))} = @{p.Name}"));

        // WHERE por PK: "col_pk" = @NombrePropiedadClave
        var keyParam = map.KeyProperty.Name; // parámetro por nombre de propiedad (para Dapper)
        var keyCol = map.KeyColumn; // nombre real de la columna en BD

        var sql = $"update {map.TableFullQuoted} set {setClause} where {Naming.Quote(keyCol)} = @{keyParam}";

        var dp = BuildWriteParameters(map, data, includeKeyParam: true);

        var cmd = new CommandDefinition(sql, dp, session.Transaction, cancellationToken: ct);
        var affected = await conn.ExecuteAsync(cmd);
        return affected > 0;
    }

    // 7) Updates parciales y columnas específicas
    // Genera SET solo con propiedades presentes en 'patch' (objeto o Dictionary<string,object?>)
    // Whitelist por EntityMap y excluye soft delete y clave primaria.
    public async Task<bool> UpdateSetAsync(IDbSession session, object id, object patch, CancellationToken ct = default)
    {
        if (session.Connection is null)
            throw new InvalidOperationException("La sesión está cerrada. Llama a OpenAsync() primero.");

        IDbConnection conn = session.Connection!;
        var map = EntityMap.For(typeof(T));

        // 1) Extraer pares (campo -> valor) desde patch
        IEnumerable<KeyValuePair<string, object?>> entries;
        if (patch is IDictionary<string, object?> dict)
        {
            entries = dict;
        }
        else
        {
            var props = patch.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.GetMethod is not null);
            entries = props.Select(p => new KeyValuePair<string, object?>(p.Name, p.GetValue(patch)));
        }

        // 2) Mapa PropiedadEntidad -> nombre de columna real
        var propByColumn = map.Props.ToDictionary(
            p => p,
            p => p.GetCustomAttribute<ColumnAttribute>()?.Name ?? Naming.ToSnake(p.Name),
            EqualityComparer<PropertyInfo>.Default);

        // 3) Construir SET y parámetros aplicando whitelist
        var setParts = new List<string>();
        var parameters = new DynamicParameters();

        // @id en WHERE
        parameters.Add("id", id);

        foreach (var kv in entries)
        {
            var requestedField = kv.Key?.Trim();
            if (string.IsNullOrWhiteSpace(requestedField)) continue;

            var resolvedCol = ResolveColumn(map, requestedField);
            if (resolvedCol is null) continue; // fuera de whitelist

            // Excluir clave primaria y soft delete
            if (string.Equals(resolvedCol, map.KeyColumn, StringComparison.OrdinalIgnoreCase)) continue;
            if (map.HasSoftDelete && !string.IsNullOrEmpty(map.SoftDeleteColumn) &&
                string.Equals(resolvedCol, map.SoftDeleteColumn, StringComparison.OrdinalIgnoreCase)) continue;

            // Encontrar la propiedad de entidad asociada a esa columna (para detectar Jsonb/Computed/Write)
            var prop = propByColumn.FirstOrDefault(x => string.Equals(x.Value, resolvedCol, StringComparison.OrdinalIgnoreCase)).Key;
            if (prop is null) continue;

            // Excluir [Computed] o [Write(Include=false)]
            if (prop.GetCustomAttribute<ComputedAttribute>() is not null) continue;
            if (prop.GetCustomAttribute<WriteAttribute>() is { Include: false }) continue;

            // Excluir clave primaria (si no lo hicimos por columna ya)
            if (prop == map.KeyProperty && prop.GetCustomAttribute<ExplicitKeyAttribute>() is null) continue;

            var paramName = $"p_{prop.Name}"; // nombre de parámetro estable
            var colSql = Naming.Quote(resolvedCol);

            // Manejo especial JSONB igual que en BuildWriteParameters
            var value = kv.Value;
            bool hasJsonbAttr = prop.GetCustomAttribute<JsonbAttribute>() != null;
            var t = prop.PropertyType;
            bool isJsonType = typeof(JToken).IsAssignableFrom(t) || typeof(JObject).IsAssignableFrom(t) || typeof(JArray).IsAssignableFrom(t);
            bool isJsonInstance = value is JToken || value is JObject || value is JArray;
            bool isJsonLike = hasJsonbAttr || isJsonType || isJsonInstance;

            if (isJsonLike)
            {
                var jsonString = value != null ? JsonConvert.SerializeObject(value) : null;
                parameters.Add(paramName, new NpgsqlJsonbParameter(jsonString));
            }
            else
            {
                parameters.Add(paramName, value);
            }

            setParts.Add($"{colSql} = @{paramName}");
        }

        if (setParts.Count == 0)
            throw new InvalidOperationException("No hay columnas válidas para actualizar con el patch proporcionado.");

        var setClause = string.Join(", ", setParts);
        var where = $" where {Naming.Quote(map.KeyColumn)} = @id";
        var sql = $"update {map.TableFullQuoted} set {setClause}{where}";

        var cmd = new CommandDefinition(sql, parameters, session.Transaction, cancellationToken: ct);
        var affected = await conn.ExecuteAsync(cmd);
        return affected > 0;
    }

    public async Task<bool> DeleteAsync(IDbSession session, object id, CancellationToken ct = default)
    {
        if (session.Connection is null)
            throw new InvalidOperationException("La sesión está cerrada. Llama a OpenAsync() primero.");

        IDbConnection conn = session.Connection!;
        var map = EntityMap.For(typeof(T));

        string sql;
        object parameters;

        if (map.HasSoftDelete && !string.IsNullOrEmpty(map.SoftDeleteColumn))
        {
            // UPDATE para soft delete
            sql = $"update {map.TableFullQuoted} " +
                  $"set {Naming.Quote(map.SoftDeleteColumn)} = true " +
                  $"where {Naming.Quote(map.KeyColumn)} = @id";
            parameters = new { id };
        }
        else
        {
            // DELETE físico
            sql = $"delete from {map.TableFullQuoted} " +
                  $"where {Naming.Quote(map.KeyColumn)} = @id";
            parameters = new { id };
        }

        var cmd = new CommandDefinition(sql, parameters, session.Transaction, cancellationToken: ct);
        var affected = await conn.ExecuteAsync(cmd);
        return affected > 0;
    }

    private static DynamicParameters BuildWriteParameters<TModel>(
        EntityMap map, TModel data, bool includeKeyParam = false)
    {
        var dp = new DynamicParameters();

        foreach (var p in map.WritableProps)
        {
            var paramName = p.Name;
            var value = p.GetValue(data);

            bool hasJsonbAttr = p.GetCustomAttribute<JsonbAttribute>() != null;
            // Detectar por tipo de propiedad (aunque el valor sea null)
            var t = p.PropertyType;
            bool isJsonType = typeof(JToken).IsAssignableFrom(t) || typeof(JObject).IsAssignableFrom(t) || typeof(JArray).IsAssignableFrom(t);
            // Detección adicional por instancia (por si llega un tipo dinámico similar a JToken)
            bool isJsonInstance = value is JToken || value is JObject || value is JArray;
            bool isJsonLike = hasJsonbAttr || isJsonType || isJsonInstance;

            if (isJsonLike)
            {
                var jsonString = value != null ? JsonConvert.SerializeObject(value) : null;
                // Usar ICustomQueryParameter para que Dapper añada un NpgsqlParameter con Jsonb explícito
                dp.Add(paramName, new NpgsqlJsonbParameter(jsonString));
            }
            else
            {
                // Tipo normal (string/int/bool/etc.)
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

    /// <summary>
    /// Resuelve un nombre de campo proporcionado por el consumidor a un nombre de columna real:
    /// - Nombre de propiedad C# (case-insensitive)
    /// - Nombre snake_case de la propiedad (case-insensitive)
    /// - Nombre definido en [Column("...")] (case-insensitive)
    /// Devuelve null si no hay coincidencia (whitelist).
    /// </summary>
    private static string? ResolveColumn(EntityMap map, string field)
    {
        var f = field.Trim();

        // 1) Propiedad C#
        var byProp = map.Props.FirstOrDefault(p =>
            string.Equals(p.Name, f, StringComparison.OrdinalIgnoreCase));
        if (byProp is not null)
        {
            var col = byProp.GetCustomAttribute<ColumnAttribute>()?.Name
                      ?? Naming.ToSnake(byProp.Name);
            return col;
        }

        // 2) snake_case de la propiedad
        var bySnake = map.Props.FirstOrDefault(p =>
            string.Equals(Naming.ToSnake(p.Name), f, StringComparison.OrdinalIgnoreCase));
        if (bySnake is not null)
        {
            var col = bySnake.GetCustomAttribute<ColumnAttribute>()?.Name
                      ?? Naming.ToSnake(bySnake.Name);
            return col;
        }

        // 3) nombre exacto definido en [Column]
        var byColumnAttr = map.Props.FirstOrDefault(p =>
            string.Equals(p.GetCustomAttribute<ColumnAttribute>()?.Name, f, StringComparison.OrdinalIgnoreCase));
        if (byColumnAttr is not null)
        {
            var col = byColumnAttr.GetCustomAttribute<ColumnAttribute>()!.Name;
            return col;
        }

        return null;
    }

    // Tu BuildOrderClause existente (ya con OrdinalIgnoreCase en snake_case)
    private static string BuildOrderClause(EntityMap map, string? orderBy, SortDirection dir)
    {
        if (string.IsNullOrWhiteSpace(orderBy))
            return string.Empty;

        var requested = orderBy.Trim();

        // 1) Ordenar por alias (nombre de propiedad C#)
        var prop = map.Props.FirstOrDefault(p =>
            string.Equals(p.Name, requested, StringComparison.OrdinalIgnoreCase));
        if (prop != null)
        {
            var alias = $"\"{prop.Name}\"";
            return $" order by {alias}{(dir == SortDirection.Desc ? " desc" : " asc")}";
        }

        // 2) Ordenar por snake_case real
        var prop2 = map.Props.FirstOrDefault(p =>
            string.Equals(Naming.ToSnake(p.Name), requested, StringComparison.OrdinalIgnoreCase));
        if (prop2 != null)
        {
            var col = Naming.Quote(Naming.ToSnake(prop2.Name));
            return $" order by {col}{(dir == SortDirection.Desc ? " desc" : " asc")}";
        }

        return string.Empty;
    }

    private static (string whereSql, DynamicParameters parameters) BuildWhereAndParams(
        EntityMap map,
        bool includeSoftDeleted,
        string? filterField,
        object? filterValue,
        StringFilterMode stringMode,
        bool forceLowerCase)
    {
        var whereParts = new List<string>();
        if (!includeSoftDeleted && map.HasSoftDelete && !string.IsNullOrEmpty(map.SoftDeleteColumn))
        {
            whereParts.Add($"{Naming.Quote(map.SoftDeleteColumn)} = false");
        }

        var parameters = new DynamicParameters();

        if (!string.IsNullOrWhiteSpace(filterField))
        {
            var resolved = ResolveColumn(map, filterField);
            if (resolved is not null)
            {
                var colSql = Naming.Quote(resolved);

                if (filterValue is string)
                {
                    parameters.Add("filter", filterValue);

                    var colExpr = forceLowerCase ? $"lower({colSql})" : colSql;
                    var valExpr = forceLowerCase ? "lower(@filter)" : "@filter";

                    string likeKeyword = forceLowerCase ? "LIKE" : "ILIKE";

                    switch (stringMode)
                    {
                        case StringFilterMode.Contains:
                            whereParts.Add($"{colExpr} {likeKeyword} '%' || {valExpr} || '%'");
                            break;
                        case StringFilterMode.StartsWith:
                            whereParts.Add($"{colExpr} {likeKeyword} {valExpr} || '%'");
                            break;
                        case StringFilterMode.EndsWith:
                            whereParts.Add($"{colExpr} {likeKeyword} '%' || {valExpr}");
                            break;
                        default:
                            whereParts.Add($"{colExpr} = {valExpr}");
                            break;
                    }
                }
                else
                {
                    parameters.Add("filter", filterValue);
                    whereParts.Add($"{colSql} = @filter");
                }
            }
        }

        string where = whereParts.Count > 0 ? " where " + string.Join(" and ", whereParts) : string.Empty;
        return (where, parameters);
    }

    public async Task<int> CountAsync(
        IDbSession session,
        bool includeSoftDeleted = false,
        string? filterField = null,
        object? filterValue = null,
        StringFilterMode stringMode = StringFilterMode.Equals,
        bool forceLowerCase = false,
        CancellationToken ct = default)
    {
        if (session.Connection is null)
            throw new InvalidOperationException("La sesión está cerrada. Llama a OpenAsync() primero.");

        IDbConnection conn = session.Connection!;
        var map = EntityMap.For(typeof(T));

        var (where, parameters) = BuildWhereAndParams(map, includeSoftDeleted, filterField, filterValue, stringMode, forceLowerCase);

        string sql = $"select count(*) from {map.TableFullQuoted}{where}";
        var cmd = new CommandDefinition(sql, parameters, session.Transaction, cancellationToken: ct);
        var total = await conn.ExecuteScalarAsync<long>(cmd);
        return (int)total;
    }

    public async Task<Page<T>> PageAsync(
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
        CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (size < 1) size = 1;

        int offset = (page - 1) * size;

        var items = await GetAllAsync(session, includeSoftDeleted, orderBy, dir, size, offset,
            filterField, filterValue, stringMode, forceLowerCase, ct);
        var total = await CountAsync(session, includeSoftDeleted, filterField, filterValue, stringMode, forceLowerCase, ct);

        return new Page<T>
        {
            Items = items,
            Total = total,
            PageNumber = page,
            Size = size
        };
    }


    // Parameter wrapper to force jsonb explicitly when writing (INSERT/UPDATE) with Dapper
    private sealed class NpgsqlJsonbParameter : SqlMapper.ICustomQueryParameter
    {
        private readonly object? _value;
        public NpgsqlJsonbParameter(object? value) => _value = value ?? DBNull.Value;
        public void AddParameter(IDbCommand command, string name)
        {
            var p = new NpgsqlParameter(name, NpgsqlTypes.NpgsqlDbType.Jsonb)
            {
                Value = _value ?? DBNull.Value
            };
            command.Parameters.Add(p);
        }
    }
}