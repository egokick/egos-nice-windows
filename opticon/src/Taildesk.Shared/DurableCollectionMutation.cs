namespace Taildesk.Shared;

public static class DurableCollectionMutation
{
    public static async Task AddAsync<T>(
        ICollection<T> collection,
        T item,
        SemaphoreSlim gate,
        Func<CancellationToken, Task> persist,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(persist);

        await gate.WaitAsync(cancellationToken);
        var added = false;
        try
        {
            collection.Add(item);
            added = true;
            await persist(cancellationToken);
        }
        catch
        {
            if (added) collection.Remove(item);
            throw;
        }
        finally
        {
            gate.Release();
        }
    }
}
