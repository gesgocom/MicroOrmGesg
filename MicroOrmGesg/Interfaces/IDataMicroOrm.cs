using System.Data;
using System.Threading;
using MicroOrmGesg.Utils;

namespace MicroOrmGesg.Interfaces;

public interface IDataMicroOrm<T> where T:class
{
    Task<T?> GetByIdAsync(IDbSession session, object id, CancellationToken ct = default);
    Task<List<T>> GetAllAsync(
        IDbSession session,
        bool includeSoftDeleted = false,
        string? orderBy = null,
        SortDirection dir = SortDirection.Asc,
        int? limit = null,
        int? offset = null,
        string? filterField = null,
        object? filterValue = null,
        StringFilterMode stringMode = StringFilterMode.Equals,
        bool forceLowerCase = false,
        CancellationToken ct = default);

    Task<int> CountAsync(
        IDbSession session,
        bool includeSoftDeleted = false,
        string? filterField = null,
        object? filterValue = null,
        StringFilterMode stringMode = StringFilterMode.Equals,
        bool forceLowerCase = false,
        CancellationToken ct = default);

    Task<Page<T>> PageAsync(
        IDbSession session,
        int page,
        int size,
        bool includeSoftDeleted = false,
        string? orderBy = null,
        SortDirection dir = SortDirection.Asc,
        string? filterField = null,
        object? filterValue = null,
        StringFilterMode stringMode = StringFilterMode.Equals,
        bool forceLowerCase = false,
        CancellationToken ct = default);

    Task<int> InsertAsync(IDbSession session, T data, CancellationToken ct = default);
    Task<object?> InsertAsyncReturnId(IDbSession session, T data, CancellationToken ct = default);
    Task<bool> UpdateAsync(IDbSession session, T data, CancellationToken ct = default);
    
    // 7) Updates parciales y columnas específicas
    // Genera SET solo con propiedades presentes en 'patch' (objeto o Dictionary<string,object?>)
    // Whitelist según EntityMap y excluye soft delete.
    Task<bool> UpdateSetAsync(IDbSession session, object id, object patch, CancellationToken ct = default);
    
    Task<bool> DeleteAsync(IDbSession session, object id, CancellationToken ct = default);
}