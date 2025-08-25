using System.Threading;
using System.Threading.Tasks;

namespace MicroOrmGesg.Interfaces;

/// <summary>
/// Servicio para comprobar la disponibilidad de la base de datos.
/// Permite opcionalmente validar con un usuario/contraseña concretos.
/// Devuelve un resultado controlado con un booleano y un mensaje explicativo.
/// </summary>
public interface IDbHealthCheck
{
    /// <summary>
    /// Verifica si la base de datos está disponible abriendo una conexión y ejecutando un SELECT 1.
    /// Si se proporcionan credenciales, se usan para la verificación; de lo contrario, se usa la configuración
    /// del origen de datos (pool) registrado.
    /// </summary>
    /// <param name="username">Usuario a probar (opcional).</param>
    /// <param name="password">Contraseña a probar (opcional).</param>
    /// <param name="ct">Token de cancelación.</param>
    /// <returns>Tupla (Ok, Message) con el resultado de la comprobación.</returns>
    Task<(bool Ok, string Message)> CheckAsync(string? username = null, string? password = null, CancellationToken ct = default);
}