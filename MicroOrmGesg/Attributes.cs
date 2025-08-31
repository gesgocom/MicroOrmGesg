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

// === Validación ===
/// <summary>
/// Indica que la propiedad es obligatoria. Para cadenas, no permite null ni cadenas vacías (o solo espacios).
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class RequiredAttribute : Attribute
{
    /// <summary>
    /// Mensaje de error personalizado. Si no se especifica, se generará uno por defecto.
    /// </summary>
    public string? Message { get; init; }
}

/// <summary>
/// Valida la longitud de una cadena. Si Min o Max son 0, ese límite se ignora.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class StringLengthAttribute : Attribute
{
    public StringLengthAttribute(int min = 0, int max = 0)
    {
        Min = min;
        Max = max;
    }

    /// <summary>
    /// Límite mínimo de longitud. 0 = sin mínimo.
    /// </summary>
    public int Min { get; }

    /// <summary>
    /// Límite máximo de longitud. 0 = sin máximo.
    /// </summary>
    public int Max { get; }

    /// <summary>
    /// Mensaje de error personalizado. Si no se especifica, se generará uno por defecto.
    /// </summary>
    public string? Message { get; init; }
}