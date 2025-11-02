using Dapper;

namespace MicroOrmGesg.Interfaces;

/// <summary>
/// Interfaz para ejecutar queries directas de Dapper usando IDbSession.
/// Permite combinar llamadas SQL directas con el uso del repositorio genérico,
/// compartiendo la misma conexión y transacción.
/// </summary>
public interface IDirectQuery
{
    /// <summary>
    /// Ejecuta una query y devuelve una secuencia de elementos del tipo especificado.
    /// </summary>
    Task<IEnumerable<T>> QueryAsync<T>(
        IDbSession session,
        string sql,
        object? param = null,
        CancellationToken ct = default);

    /// <summary>
    /// Ejecuta una query y devuelve un único elemento.
    /// Lanza excepción si no hay resultados o hay más de uno.
    /// </summary>
    Task<T> QuerySingleAsync<T>(
        IDbSession session,
        string sql,
        object? param = null,
        CancellationToken ct = default);

    /// <summary>
    /// Ejecuta una query y devuelve un único elemento o default(T).
    /// Lanza excepción si hay más de un resultado.
    /// </summary>
    Task<T?> QuerySingleOrDefaultAsync<T>(
        IDbSession session,
        string sql,
        object? param = null,
        CancellationToken ct = default);

    /// <summary>
    /// Ejecuta una query y devuelve el primer elemento.
    /// Lanza excepción si no hay resultados.
    /// </summary>
    Task<T> QueryFirstAsync<T>(
        IDbSession session,
        string sql,
        object? param = null,
        CancellationToken ct = default);

    /// <summary>
    /// Ejecuta una query y devuelve el primer elemento o default(T).
    /// </summary>
    Task<T?> QueryFirstOrDefaultAsync<T>(
        IDbSession session,
        string sql,
        object? param = null,
        CancellationToken ct = default);

    /// <summary>
    /// Ejecuta un comando (INSERT, UPDATE, DELETE) y devuelve el número de filas afectadas.
    /// </summary>
    Task<int> ExecuteAsync(
        IDbSession session,
        string sql,
        object? param = null,
        CancellationToken ct = default);

    /// <summary>
    /// Ejecuta una query y devuelve el primer valor de la primera fila como escalar.
    /// </summary>
    Task<T?> ExecuteScalarAsync<T>(
        IDbSession session,
        string sql,
        object? param = null,
        CancellationToken ct = default);

    /// <summary>
    /// Ejecuta una query con múltiples result sets y devuelve un GridReader de Dapper.
    /// Útil para procedimientos almacenados o queries con múltiples SELECT.
    /// </summary>
    Task<SqlMapper.GridReader> QueryMultipleAsync(
        IDbSession session,
        string sql,
        object? param = null,
        CancellationToken ct = default);
}
