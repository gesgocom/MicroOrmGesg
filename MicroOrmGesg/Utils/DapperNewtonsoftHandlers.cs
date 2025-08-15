using System.Data;
using Dapper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Npgsql;

namespace MicroOrmGesg.Utils;

public sealed class JTokenTypeHandler : SqlMapper.TypeHandler<JToken?>
{
    public override JToken? Parse(object value)
        => value is null || value is DBNull ? null : JsonConvert.DeserializeObject<JToken>(value.ToString()!);

    public override void SetValue(IDbDataParameter parameter, JToken? value)
    {
        parameter.Value = value is null ? DBNull.Value : JsonConvert.SerializeObject(value);
        if (parameter is NpgsqlParameter npg)
            npg.NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb; // 👈 importantísimo
    }
}

public sealed class JObjectTypeHandler : SqlMapper.TypeHandler<JObject?>
{
    public override JObject? Parse(object value)
        => value is null || value is DBNull ? null : JsonConvert.DeserializeObject<JObject>(value.ToString()!);

    public override void SetValue(IDbDataParameter parameter, JObject? value)
    {
        parameter.Value = value is null ? DBNull.Value : JsonConvert.SerializeObject(value);
        if (parameter is NpgsqlParameter npg)
            npg.NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb;
    }
}

public sealed class JArrayTypeHandler : SqlMapper.TypeHandler<JArray?>
{
    public override JArray? Parse(object value)
        => value is null || value is DBNull ? null : JsonConvert.DeserializeObject<JArray>(value.ToString()!);

    public override void SetValue(IDbDataParameter parameter, JArray? value)
    {
        parameter.Value = value is null ? DBNull.Value : JsonConvert.SerializeObject(value);
        if (parameter is NpgsqlParameter npg)
            npg.NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb;
    }
}