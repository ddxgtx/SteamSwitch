namespace SteamSwitcher.Services;

/// <summary>Keep the existing parallel providers, while canceling losers and observing late faults.</summary>
internal static class ArtworkLookupRace
{
    internal static async Task<byte[]?> FirstMatchAsync(
        IEnumerable<Func<CancellationToken, Task<byte[]?>>> providers, CancellationToken cancellationToken)
    {
        using var lookup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = new List<Task<byte[]?>>();
        try
        {
            foreach (var provider in providers)
            {
                lookup.Token.ThrowIfCancellationRequested();
                pending.Add(InvokeAsync(provider, lookup.Token));
            }
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending).WaitAsync(lookup.Token).ConfigureAwait(false);
                pending.Remove(completed);
                var artwork = await completed.ConfigureAwait(false);
                lookup.Token.ThrowIfCancellationRequested();
                if (artwork is { Length: > 100 }) return artwork;
            }
            return null;
        }
        finally
        {
            try { lookup.Cancel(); }
            catch (AggregateException ex) { AppLogger.Warn($"Artwork cancellation callback failed: {ex.GetBaseException().Message}"); }
            foreach (var task in pending)
            {
                // A provider may ignore cancellation. Never await it during
                // shutdown, but always observe any eventual exception.
                _ = task.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    private static async Task<byte[]?> InvokeAsync(Func<CancellationToken, Task<byte[]?>> provider, CancellationToken token)
    {
        try { return await provider(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            AppLogger.Debug($"Artwork provider failed: {ex.Message}");
            return null;
        }
    }
}
