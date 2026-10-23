using Microsoft.Extensions.Options;

namespace NettoMarkdowns.Services;

/// <summary>
/// Polls the Food Waste API on a fixed interval and hands each result to
/// <see cref="MarkdownStore"/>. Runs inside the web app, so the server runs one
/// process for both the dashboard and the logger.
/// </summary>
public sealed class MarkdownLogService(
    IServiceScopeFactory scopes,
    MarkdownStore store,
    IOptions<MarkdownLogOptions> logOptions,
    IOptions<SallingOptions> sallingOptions,
    ILogger<MarkdownLogService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = logOptions.Value;
        var salling = sallingOptions.Value;
        var zips = options.Zips.Length > 0 ? options.Zips : salling.DefaultZips;

        if (zips.Length == 0)
        {
            logger.LogWarning("Markdown log is enabled but has no postcodes. Set MarkdownLog:Zips or Salling:DefaultZips.");
            return;
        }

        logger.LogInformation("Markdown log writing to {Path}, postcodes {Zips}, every {Interval}",
            Path.GetFullPath(store.DatabasePath), string.Join(", ", zips), options.Interval);

        using var timer = new PeriodicTimer(options.Interval);
        do
        {
            await PollAsync(zips, salling.Brands, stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PollAsync(string[] zips, string[] brands, CancellationToken ct)
    {
        var at = DateTimeOffset.UtcNow;
        try
        {
            // SallingClient is a typed HttpClient, so take a fresh one per poll
            // rather than holding one for the life of the app.
            using var scope = scopes.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<SallingClient>();

            var stores = (await client.GetByZipsAsync(zips, ct))
                .Where(s => brands.Length == 0 ||
                            brands.Contains(s.Store.Brand, StringComparer.OrdinalIgnoreCase))
                .ToList();

            var result = store.RecordPoll(at, stores);
            logger.LogInformation("Logged {Stores} stores, {Offers} offers: {New} new, {Gone} gone",
                result.Stores, result.Offers, result.New, result.Gone);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            // Never mark offers gone on a failed poll; just note the gap and try again next tick.
            logger.LogError(ex, "Markdown poll failed");
            try
            {
                store.RecordFailedPoll(at, ex.Message);
            }
            catch (Exception dbEx)
            {
                logger.LogError(dbEx, "Could not record the failed poll");
            }
        }
    }
}
