using System.Reflection;
using System.Linq;
using MicroOrmGesg;        // TableAttribute, KeyAttribute, ExplicitKeyAttribute, ComputedAttribute, WriteAttribute, ColumnAttribute, SoftDeleteAttribute
using MicroOrmGesg.Utils; // Naming

namespace MicroOrmGesg.Utils
{
    internal sealed class EntityMap
    {
        private static readonly Dictionary<Type, EntityMap> Cache = new();

        public string TableFullQuoted { get; }
        public string KeyColumn { get; }
        public PropertyInfo KeyProperty { get; }
        public IReadOnlyList<PropertyInfo> Props { get; }
        public IReadOnlyList<PropertyInfo> WritableProps { get; }
        public string SelectColsCsv { get; }

        // Soft delete
        public bool HasSoftDelete { get; }
        public string SoftDeleteColumn { get; } = string.Empty;

        public EntityMap(Type t)
        {
            // Tabla: quotear schema y nombre por separado -> "schema"."table"
            var tableAttr = t.GetCustomAttribute<TableAttribute>();
            var tableName = tableAttr?.Name ?? Naming.ToSnake(t.Name);
            var schema = tableAttr?.Schema;

            var parts = (schema is null ? tableName : $"{schema}.{tableName}")
                        .Split('.', StringSplitOptions.RemoveEmptyEntries);
            TableFullQuoted = string.Join('.', parts.Select(p => Naming.Quote(p)));

            // Propiedades públicas
            Props = t.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                     .Where(p => p.GetMethod is not null)
                     .ToArray();

            // Clave primaria (por atributo)
            KeyProperty =
                Props.FirstOrDefault(p => p.GetCustomAttribute<KeyAttribute>() is not null) ??
                Props.FirstOrDefault(p => p.GetCustomAttribute<ExplicitKeyAttribute>() is not null) ??
                throw new InvalidOperationException($"El modelo {t.Name} no tiene definida una clave primaria.");

            // Nombre de columna de la PK (respeta [Column])
            KeyColumn = GetColumnName(KeyProperty);

            // SELECT: columna real (GetColumnName) AS "PropiedadCSharp"
            var selectCols = Props.Select(p =>
            {
                var realCol = Naming.Quote(GetColumnName(p));
                var alias = SafeAlias(p.Name);
                return $"{realCol} AS {alias}";
            });
            SelectColsCsv = string.Join(", ", selectCols);

            // Escritura: excluir [Computed]; excluir [Key] salvo [ExplicitKey]
            WritableProps = Props.Where(p =>
            {
                if (p.GetCustomAttribute<ComputedAttribute>() is not null) return false;
                if (p.GetCustomAttribute<WriteAttribute>() is { Include: false }) return false;

                // Excluir la columna de soft delete si existe
                if (!string.IsNullOrEmpty(SoftDeleteColumn) && 
                    string.Equals(GetColumnName(p), SoftDeleteColumn, StringComparison.OrdinalIgnoreCase))
                    return false;

                var isKey = p == KeyProperty;
                var isExplicit = p.GetCustomAttribute<ExplicitKeyAttribute>() is not null;
                return isKey ? isExplicit : true;
            }).ToArray();

            // Soft delete detection: [SoftDelete] o nombres comunes (también respeta [Column])
            var softProp = Props.FirstOrDefault(p => p.GetCustomAttribute<SoftDeleteAttribute>() is not null);
            if (softProp is null)
            {
                // Busca por nombre conocido si no hay atributo
                softProp = Props.FirstOrDefault(p =>
                    string.Equals(p.Name, "eliminado", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(p.Name, "is_deleted", StringComparison.OrdinalIgnoreCase));
            }

            if (softProp is not null && (softProp.PropertyType == typeof(bool) || softProp.PropertyType == typeof(bool?)))
            {
                HasSoftDelete = true;
                SoftDeleteColumn = GetColumnName(softProp);
            }
        }

        public static EntityMap For(Type t)
        {
            if (Cache.TryGetValue(t, out var m)) return m;
            lock (Cache)
            {
                return Cache.TryGetValue(t, out m!) ? m : (Cache[t] = new EntityMap(t));
            }
        }

        public (string colsCsv, string paramsCsv) GetInsertColumnsAndParams(object entity)
        {
            // columnas reales de BD (respetando [Column])
            var cols = WritableProps.Select(p => Naming.Quote(GetColumnName(p)));

            // parámetros con nombre de PROPIEDAD (para que Dapper los encuentre en 'entity')
            var prms = WritableProps.Select(p => "@" + p.Name); 

            return (string.Join(", ", cols), string.Join(", ", prms));
        }

        private static string SafeAlias(string propertyName)
        {
            const int limit = 63;
            var alias = propertyName.Length > limit ? propertyName[..limit] : propertyName;
            return $"\"{alias}\"";
        }

        private static string GetColumnName(PropertyInfo p)
        {
            var colAttr = p.GetCustomAttribute<ColumnAttribute>();
            if (colAttr != null && !string.IsNullOrWhiteSpace(colAttr.Name))
                return colAttr.Name; // nombre exacto del atributo

            return Naming.ToSnake(p.Name); // convención por defecto
        }
    }
}