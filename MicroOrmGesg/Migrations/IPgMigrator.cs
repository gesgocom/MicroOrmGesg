using MicroOrmGesg.Migrations.Models;

namespace MicroOrmGesg.Migrations;

/// <summary>
/// Migrador de esquemas PostgreSQL con soporte para idempotencia, checksums y detección de drift.
/// </summary>
public interface IPgMigrator
{
    /// <summary>
    /// Ejecuta las migraciones desde la fuente especificada.
    /// Los pasos se aplican secuencialmente en una transacción por paso.
    /// Usa advisory locks para evitar ejecuciones concurrentes.
    /// </summary>
    /// <param name="source">Fuente de scripts de migración (archivo, recurso embebido, etc.).</param>
    /// <param name="ct">Token de cancelación.</param>
    /// <returns>Resultado de la ejecución con estadísticas y detalles por paso.</returns>
    Task<MigrationResult> RunAsync(IMigrationSource source, CancellationToken ct = default);
}
