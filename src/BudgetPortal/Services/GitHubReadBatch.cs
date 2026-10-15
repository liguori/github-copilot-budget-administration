namespace BudgetPortal.Services;

internal static class GitHubReadBatch
{
    internal const int MaximumConcurrency = 4;

    public static async Task<TResult[]> SelectAsync<TSource, TResult>(
        IReadOnlyList<TSource> source, Func<TSource, CancellationToken, Task<TResult>> read,
        CancellationToken cancellationToken)
    {
        var results = new TResult[source.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, source.Count), new ParallelOptions
        {
            MaxDegreeOfParallelism = MaximumConcurrency,
            CancellationToken = cancellationToken
        }, async (index, token) => results[index] = await read(source[index], token));
        return results;
    }
}
