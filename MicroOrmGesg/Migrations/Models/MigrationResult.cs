namespace MicroOrmGesg.Migrations.Models;

/// <summary>
/// Resultado completo de la ejecución de migraciones.
/// </summary>
public sealed class MigrationResult
{
    /// <summary>
    /// Número de pasos aplicados exitosamente.
    /// </summary>
    public required int StepsApplied { get; init; }

    /// <summary>
    /// Número de pasos omitidos (ya aplicados previamente).
    /// </summary>
    public required int StepsSkipped { get; init; }

    /// <summary>
    /// Número de pasos que fallaron durante la ejecución.
    /// </summary>
    public required int StepsFailed { get; init; }

    /// <summary>
    /// Número de pasos con drift detectado (checksum diferente).
    /// </summary>
    public required int StepsWithDrift { get; init; }

    /// <summary>
    /// Duración total de la ejecución de todas las migraciones.
    /// </summary>
    public required TimeSpan TotalDuration { get; init; }

    /// <summary>
    /// Resultados detallados de cada paso ejecutado.
    /// </summary>
    public required List<StepResult> Steps { get; init; }

    /// <summary>
    /// Indica si todas las migraciones se completaron sin errores.
    /// </summary>
    public bool IsSuccess => StepsFailed == 0;
}
