using System.Data;
using MicroOrmGesg.Utils;

namespace MicroOrmGesg.Interfaces;

public interface IDataMicroOrm<T> where T:class
{
    Task<T?> GetByIdAsync(IDbSession session, object id);
    Task<List<T>> GetAllAsync(
        IDbSession session,
        bool includeSoftDeleted = false,
        string? orderBy = null,
        SortDirection dir = SortDirection.Asc,
        int? limit = null,
        int? offset = null,
        string? filterField = null,
        object? filterValue = null,
        StringFilterMode stringMode = StringFilterMode.Equals);
    Task<int> InsertAsync(IDbSession session, T data);
    Task<object?> InsertAsyncReturnId(IDbSession session, T data);
    Task<bool> UpdateAsync(IDbSession session, T data);
    Task<bool> DeleteAsync(IDbSession session, object id);
}