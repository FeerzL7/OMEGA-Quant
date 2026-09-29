using Npgsql;
using Omega.Core.Persistence;

namespace Omega.Infrastructure.Persistence;

internal static class NpgsqlErrors
{
    /// <summary>
    /// Runs a database operation and translates driver errors into
    /// <see cref="PersistenceException"/>. Cancellation is never translated.
    /// </summary>
    public static async Task<T> TranslateAsync<T>(string operation, Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (NpgsqlException ex)
        {
            throw new PersistenceException($"{operation} failed: {ex.Message}", ex.IsTransient, ex);
        }
        catch (TimeoutException ex)
        {
            throw new PersistenceException($"{operation} timed out: {ex.Message}", isTransient: true, ex);
        }
    }
}
