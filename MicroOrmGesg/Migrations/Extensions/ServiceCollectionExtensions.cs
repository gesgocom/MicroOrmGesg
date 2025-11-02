using Microsoft.Extensions.DependencyInjection;

namespace MicroOrmGesg.Migrations.Extensions;

/// <summary>
/// Métodos de extensión para registrar el sistema de migraciones en el contenedor de DI.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registra el migrador de PostgreSQL (IPgMigrator) en el contenedor de servicios.
    /// Requiere que NpgsqlDataSource esté previamente registrado como Singleton.
    /// </summary>
    /// <param name="services">Colección de servicios.</param>
    /// <param name="configureOptions">Configuración opcional de PgMigratorOptions.</param>
    /// <returns>La colección de servicios para encadenamiento.</returns>
    public static IServiceCollection AddPgMigrations(
        this IServiceCollection services,
        Action<PgMigratorOptions>? configureOptions = null)
    {
        // Registrar opciones
        var options = new PgMigratorOptions();
        configureOptions?.Invoke(options);
        services.AddSingleton(options);

        // Registrar el migrador como Singleton (puede ejecutarse múltiples veces si es necesario)
        services.AddSingleton<IPgMigrator, PgMigrator>();

        return services;
    }
}
