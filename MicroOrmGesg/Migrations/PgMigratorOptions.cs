using MicroOrmGesg.Migrations.Models;

namespace MicroOrmGesg.Migrations;

/// <summary>
/// Opciones de configuración para el migrador de PostgreSQL.
/// </summary>
public sealed class PgMigratorOptions
{
    /// <summary>
    /// Clave única para el advisory lock de PostgreSQL.
    /// Previene ejecuciones concurrentes de migraciones.
    /// Por defecto: "micro-orm-gesg:migrations:default"
    /// </summary>
    public string AdvisoryLockKey { get; set; } = "micro-orm-gesg:migrations:default";

    /// <summary>
    /// Timeout en segundos para comandos SQL individuales.
    /// Por defecto: 120 segundos (2 minutos).
    /// </summary>
    public int CommandTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Política de manejo de drift (cuando el checksum de un paso cambia).
    /// Por defecto: WarnAndSkip (registra advertencia y omite re-ejecución).
    /// </summary>
    public DriftPolicy DriftPolicy { get; set; } = DriftPolicy.WarnAndSkip;

    /// <summary>
    /// Si es true, detiene la ejecución de migraciones al primer error.
    /// Si es false, continúa con los siguientes pasos tras un fallo.
    /// Por defecto: true.
    /// </summary>
    public bool StopOnError { get; set; } = true;

    /// <summary>
    /// Nombre de la tabla que almacena el journal de migraciones.
    /// Por defecto: "__micro_orm_migrations"
    /// </summary>
    public string JournalTableName { get; set; } = "__micro_orm_migrations";

    /// <summary>
    /// Schema de la tabla de journal. Si es null, usa el schema por defecto (public).
    /// Por defecto: null.
    /// </summary>
    public string? JournalSchema { get; set; } = null;
}
