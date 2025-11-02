namespace MicroOrmGesg.Migrations.Models;

/// <summary>
/// Define el comportamiento cuando se detecta un drift (cambio de checksum en un paso ya aplicado).
/// </summary>
public enum DriftPolicy
{
    /// <summary>
    /// Lanza una excepción si el checksum difiere del registrado en el journal.
    /// </summary>
    Fail,

    /// <summary>
    /// Registra un warning en el log y omite la re-ejecución del paso.
    /// Esta es la opción más segura por defecto.
    /// </summary>
    WarnAndSkip,

    /// <summary>
    /// Re-ejecuta el paso aunque el @check devuelva true.
    /// Útil para funciones/vistas que pueden recrearse sin riesgo.
    /// </summary>
    Reapply
}
