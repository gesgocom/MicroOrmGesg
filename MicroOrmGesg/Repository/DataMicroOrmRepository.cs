using MicroOrmGesg.Interfaces;
using System.Data;
using System.Reflection;
using Dapper;
using MicroOrmGesg.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Npgsql;
using NpgsqlTypes;

namespace MicroOrmGesg.Repository;

public class DataMicroOrmRepository<T> : IDataMicroOrm<T> where T : class
{
    public async Task<T?> GetByIdAsync(IDbSession session, object id)
    {
        IDbConnection conn = session.Connection!;
        var map = EntityMap.For(typeof(T));

        // WHERE por clave primaria
        var sql = $"select {map.SelectColsCsv} from {map.TableFullQuoted} where {Naming.Quote(map.KeyColumn)} = @id";
        return await conn.QuerySingleOrDefaultAsync<T>(sql, new { id }, session.Transaction);
    }

    public async Task<List<T>> GetAllAsync(IDbSession session, bool includeSoftDeleted = false, string? orderBy = null,
        SortDirection dir = SortDirection.Asc, int? limit = null, int? offset = null, string? filterField = null,
        object? filterValue = null, StringFilterMode stringMode = StringFilterMode.Equals)
    {
        if (session.Connection is null)
            throw new InvalidOperationException("La sesión está cerrada. Llama a OpenAsync() primero.");

        IDbConnection conn = session.Connection!;
        var map = EntityMap.For(typeof(T));

        // WHERE base (soft delete)
        var whereParts = new List<string>();
        if (!includeSoftDeleted && map.HasSoftDelete && !string.IsNullOrEmpty(map.SoftDeleteColumn))
        {
            whereParts.Add($"{Naming.Quote(map.SoftDeleteColumn)} = false");
        }

        var parameters = new DynamicParameters();

        // WHERE adicional (campo/valor)
        if (!string.IsNullOrWhiteSpace(filterField))
        {
            var resolved = ResolveColumn(map, filterField);
            if (resolved is not null)
            {
                var colSql = Naming.Quote(resolved);
                if (filterValue is string s)
                {
                    switch (stringMode)
                    {
                        case StringFilterMode.Contains:
                            parameters.Add("filter", s);
                            whereParts.Add($"{colSql} ILIKE '%' || @filter || '%'");
                            break;
                        case StringFilterMode.StartsWith:
                            parameters.Add("filter", s);
                            whereParts.Add($"{colSql} ILIKE @filter || '%'");
                            break;
                        case StringFilterMode.EndsWith:
                            parameters.Add("filter", s);
                            whereParts.Add($"{colSql} ILIKE '%' || @filter");
                            break;
                        default: // Equals
                            parameters.Add("filter", s);
                            whereParts.Add($"{colSql} = @filter");
                            break;
                    }
                }
                else
                {
                    parameters.Add("filter", filterValue);
                    whereParts.Add($"{colSql} = @filter");
                }
            }
            // si no se resuelve el campo, simplemente no añadimos filtro (whitelist contra inyección)
        }

        // Componer WHERE final
        string where = whereParts.Count > 0 ? " where " + string.Join(" and ", whereParts) : string.Empty;

        // ORDER BY
        string order = BuildOrderClause(map, orderBy, dir);

        // LIMIT/OFFSET
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

        var result = await conn.QueryAsync<T>(sql, parameters, transaction: session.Transaction);
        return result.AsList();
    }

    public async Task<int> InsertAsync(IDbSession session, T data)
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

        var affected = await conn.ExecuteAsync(sql, dp, session.Transaction);
        if (affected == 0)
            throw new InvalidOperationException("No se insertó ningún registro. Verifica los datos.");
        return affected;
    }

    public async Task<object?> InsertAsyncReturnId(IDbSession session, T data)
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

        var id = await conn.ExecuteScalarAsync<object>(sql, dp, session.Transaction);
        if (id is null)
            throw new InvalidOperationException("No se pudo obtener el ID del registro insertado.");
        return id;
    }

    public async Task<bool> UpdateAsync(IDbSession session, T data)
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

        var affected = await conn.ExecuteAsync(sql, dp, session.Transaction);
        return affected > 0;
    }

    public async Task<bool> DeleteAsync(IDbSession session, object id)
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

        var affected = await conn.ExecuteAsync(sql, parameters, session.Transaction);
        return affected > 0;
    }

    private static DynamicParameters BuildWriteParameters<TModel>(EntityMap map, TModel data,
        bool includeKeyParam = false)
    {
        var dp = new DynamicParameters();

        foreach (var p in map.WritableProps)
        {
            var paramName = p.Name;
            var value = p.GetValue(data);

            bool isJsonLike =
                p.GetCustomAttribute<JsonbAttribute>() != null ||
                value is JObject ||
                value is JArray ||
                value is JToken;

            if (isJsonLike)
            {
                // Serializar con Newtonsoft
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
}