namespace MicroOrmGesg.Migrations;

/// <summary>
/// Define una fuente de scripts de migración SQL.
/// </summary>
public interface IMigrationSource
{
    /// <summary>
    /// Lee las líneas del script de migración de forma asíncrona (streaming).
    /// Cada línea puede contener SQL, comentarios o directivas @step/@check.
    /// </summary>
    /// <param name="ct">Token de cancelación.</param>
    /// <returns>Secuencia asíncrona de líneas del script.</returns>
    IAsyncEnumerable<string> ReadLinesAsync(CancellationToken ct = default);
}
