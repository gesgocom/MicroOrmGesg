namespace MicroOrmGesg;

[AttributeUsage(AttributeTargets.Class)]
public sealed class TableAttribute : Attribute
{
    public TableAttribute(string name) => Name = name;
    public string Name { get; }
    public string? Schema { get; init; }
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class KeyAttribute : Attribute {}     // PK autoincrement (serial/identity)
[AttributeUsage(AttributeTargets.Property)]
public sealed class ExplicitKeyAttribute : Attribute {} // PK manual (no generado)
[AttributeUsage(AttributeTargets.Property)]
public sealed class ComputedAttribute : Attribute {}    // no se escribe (solo lectura)
[AttributeUsage(AttributeTargets.Property)]
public sealed class SoftDeleteAttribute : Attribute {}
[AttributeUsage(AttributeTargets.Property)]
public sealed class WriteAttribute : Attribute          // para excluir columnas en writes
{
    public bool Include { get; init; } = true;
}
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class ColumnAttribute : Attribute
{
    public ColumnAttribute(string name)
    {
        Name = name;
    }

    public string Name { get; }
}
[AttributeUsage(AttributeTargets.Property)]
public sealed class JsonbAttribute : Attribute { }