using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RelayHub.Data;

namespace RelayHub.Workers;

/// <summary>
/// Runs in the background, picks up deliveries that are due, and forwards them to their targets.
/// Failures are retried with backoff; after MaxAttempts they become dead letters for a human to look at.
/// </summary>
public class DeliveryWorker(
    IServiceScopeFactory scopes,
    IHttpClientFactory httpClients,
    IOptionsMonitor<RelayOptions> options,
    ILogger<DeliveryWorker> log) : BackgroundService
{
    public const string HttpClientName = "targets";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.CurrentValue.WorkerEnabled)
        {
            log.LogInformation("Delivery worker disabled by configuration");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DeliverDueAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad pass must never kill the worker for good.
                log.LogError(ex, "Delivery pass failed");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    public async Task DeliverDueAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
        var now = DateTime.UtcNow;

        var due = await db.Deliveries
            .Include(d => d.Message)
            .Where(d => d.Status == DeliveryStatus.Pending && d.NextAttemptAtUtc <= now)
            .OrderBy(d => d.NextAttemptAtUtc)
            .Take(20)
            .ToListAsync(ct);

        foreach (var delivery in due)
            await AttemptAsync(delivery, ct);

        if (due.Count > 0)
            await db.SaveChangesAsync(ct);
    }

    private async Task AttemptAsync(Data.Delivery delivery, CancellationToken ct)
    {
        var settings = options.CurrentValue;
        var message = delivery.Message!;
        delivery.Attempts++;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, delivery.TargetUrl)
            {
                Content = new StringContent(message.Payload, System.Text.Encoding.UTF8, "application/json"),
            };
            // Pass along IDs so the target can do its own duplicate checks and tracing.
            request.Headers.Add("X-Relay-Message-Id", message.Id.ToString());
            request.Headers.Add("X-Relay-Source", message.Source);
            request.Headers.Add("X-Relay-Delivery-Id", message.DeliveryId);
            if (message.EventType is not null)
                request.Headers.Add("X-Relay-Event", message.EventType);

            using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, ct);

            if (response.IsSuccessStatusCode)
            {
                delivery.Status = DeliveryStatus.Delivered;
                delivery.DeliveredAtUtc = DateTime.UtcNow;
                delivery.LastError = null;
                log.LogInformation("Delivered {MessageId} to {Target} on attempt {Attempt}",
                    message.Id, delivery.TargetName, delivery.Attempts);
                return;
            }

            delivery.LastError = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            delivery.LastError = ex.Message;
        }

        if (delivery.Attempts >= settings.MaxAttempts)
        {
            delivery.Status = DeliveryStatus.DeadLettered;
            log.LogWarning("Dead-lettered {MessageId} for {Target} after {Attempts} attempts: {Error}",
                message.Id, delivery.TargetName, delivery.Attempts, delivery.LastError);
        }
        else
        {
            var wait = RetryPolicy.DelayAfter(delivery.Attempts, settings.BaseDelaySeconds, settings.MaxDelaySeconds);
            delivery.NextAttemptAtUtc = DateTime.UtcNow + wait;
            log.LogWarning("Delivery of {MessageId} to {Target} failed ({Error}); retrying in {Wait}",
                message.Id, delivery.TargetName, delivery.LastError, wait);
        }
    }
}
