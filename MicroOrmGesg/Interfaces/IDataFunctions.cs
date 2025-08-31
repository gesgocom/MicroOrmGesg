using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MicroOrmGesg.Interfaces;

public interface IDataFunctions
{
    // Para funciones que retornan un valor escalar o un solo registro
    Task<TResult?> CallFunctionAsync<TResult>(IDbSession session, string functionName, object? args = null, string? schema = null, CancellationToken ct = default);
    // Para funciones que retornan un conjunto de filas (TABLE o SETOF)
    Task<List<TResult>> CallFunctionListAsync<TResult>(IDbSession session, string functionName, object? args = null, string? schema = null, CancellationToken ct = default);
    // Para funciones void (sin retorno) o cuando no se necesita el resultado
    Task CallVoidFunctionAsync(IDbSession session, string functionName, object? args = null, string? schema = null, CancellationToken ct = default);
}