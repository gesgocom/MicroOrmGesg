using System.Text;

namespace MicroOrmGesg.Migrations.Models;

/// <summary>
/// Representa un paso de migración parseado desde el script SQL.
/// </summary>
internal sealed record MigrationStep
{
    /// <summary>
    /// Identificador único del paso (p. ej., "001", "002-create-users").
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Nombre descriptivo del paso (p. ej., "create.users", "add.index.users.email").
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// SQL opcional para verificar si el paso ya está aplicado (p. ej., "SELECT to_regclass('public.users') IS NOT NULL").
    /// Si devuelve true, el paso podría omitirse según la política de drift.
    /// </summary>
    public string? CheckSql { get; init; }

    /// <summary>
    /// SQL del paso a ejecutar (puede contener múltiples sentencias separadas por ;).
    /// </summary>
    public required string SqlToApply { get; init; }

    /// <summary>
    /// StringBuilder interno usado durante el parsing. No se usa después del parsing.
    /// </summary>
    public StringBuilder? SqlBuilder { get; init; }
}
