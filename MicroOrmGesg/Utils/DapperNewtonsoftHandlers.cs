using System.Data;
using Dapper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MicroOrmGesg.Utils;

public sealed class JObjectTypeHandler : SqlMapper.TypeHandler<JObject?>
{
    public override JObject? Parse(object value)
        => value is null || value is DBNull ? null : JsonConvert.DeserializeObject<JObject>(value.ToString()!);

    public override void SetValue(IDbDataParameter parameter, JObject? value)
        => parameter.Value = value is null ? DBNull.Value : JsonConvert.SerializeObject(value);
}

public sealed class JArrayTypeHandler : SqlMapper.TypeHandler<JArray?>
{
    public override JArray? Parse(object value)
        => value is null || value is DBNull ? null : JsonConvert.DeserializeObject<JArray>(value.ToString()!);

    public override void SetValue(IDbDataParameter parameter, JArray? value)
        => parameter.Value = value is null ? DBNull.Value : JsonConvert.SerializeObject(value);
}

public sealed class JTokenTypeHandler : SqlMapper.TypeHandler<JToken?>
{
    public override JToken? Parse(object value)
        => value is null || value is DBNull ? null : JsonConvert.DeserializeObject<JToken>(value.ToString()!);

    public override void SetValue(IDbDataParameter parameter, JToken? value)
        => parameter.Value = value is null ? DBNull.Value : JsonConvert.SerializeObject(value);
}