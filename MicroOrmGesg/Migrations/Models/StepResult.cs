namespace MicroOrmGesg.Migrations.Models;

/// <summary>
/// Resultado de la ejecución de un paso individual de migración.
/// </summary>
public sealed class StepResult
{
    /// <summary>
    /// Identificador del paso.
    /// </summary>
    public required string StepId { get; init; }

    /// <summary>
    /// Nombre del paso.
    /// </summary>
    public required string StepName { get; init; }

    /// <summary>
    /// Checksum SHA-256 del SQL ejecutado.
    /// </summary>
    public required string Checksum { get; init; }

    /// <summary>
    /// Indica si el paso se ejecutó correctamente.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Indica si el paso fue omitido (ya aplicado previamente con el mismo checksum).
    /// </summary>
    public required bool Skipped { get; init; }

    /// <summary>
    /// Indica si se detectó un drift (checksum diferente al registrado).
    /// </summary>
    public required bool DriftDetected { get; init; }

    /// <summary>
    /// Duración de la ejecución del paso en milisegundos.
    /// </summary>
    public required int DurationMs { get; init; }

    /// <summary>
    /// Mensaje de error si el paso falló, o mensaje de advertencia si hubo drift.
    /// </summary>
    public string? Message { get; init; }
}
