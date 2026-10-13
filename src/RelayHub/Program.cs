using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RelayHub;
using RelayHub.Data;
using RelayHub.Security;
using RelayHub.Workers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<RelayOptions>(builder.Configuration.GetSection("Relay"));
builder.Services.AddDbContext<RelayDbContext>((sp, db) =>
    db.UseSqlite(sp.GetRequiredService<IConfiguration>().GetConnectionString("RelayDb")));
builder.Services.AddHttpClient(DeliveryWorker.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHostedService<DeliveryWorker>();
builder.Services.AddHealthChecks();

var app = builder.Build();

// Create the SQLite database on first run. A bigger project would use EF Core migrations.
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<RelayDbContext>().Database.EnsureCreated();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapHealthChecks("/health");

// ---------------------------------------------------------------------------
// 1. Receive: verify, store once, queue a delivery for every interested target.
// ---------------------------------------------------------------------------
app.MapPost("/webhooks/{source}", async (
    string source,
    HttpRequest request,
    RelayDbContext db,
    IOptionsSnapshot<RelayOptions> options,
    ILogger<Program> log) =>
{
    var settings = options.Value;
    source = source.ToLowerInvariant();
    if (!settings.Sources.TryGetValue(source, out var sourceConfig))
        return Results.NotFound(new { error = $"Unknown source '{source}'" });

    // Signatures are computed over the exact bytes sent, so read the raw body
    // before anything parses or reformats it.
    using var buffer = new MemoryStream();
    await request.Body.CopyToAsync(buffer);
    var body = buffer.ToArray();

    var signature = request.Headers[sourceConfig.SignatureHeader].ToString();
    if (!SignatureVerifier.IsValid(body, sourceConfig.Secret, signature, sourceConfig.SignaturePrefix))
    {
        log.LogWarning("Rejected webhook from {Source}: bad or missing signature", source);
        return Results.Unauthorized();
    }

    // Use the sender's delivery ID if it gives one; otherwise fingerprint the body,
    // so an identical resend is still recognised as a duplicate.
    var deliveryId = request.Headers[sourceConfig.DeliveryIdHeader].ToString();
    if (string.IsNullOrWhiteSpace(deliveryId))
        deliveryId = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(body));

    var existing = await db.Messages.FirstOrDefaultAsync(m => m.Source == source && m.DeliveryId == deliveryId);
    if (existing is not null)
        return Results.Ok(new { duplicate = true, messageId = existing.Id });

    var eventType = request.Headers[sourceConfig.EventHeader].ToString();
    var message = new WebhookMessage
    {
        Source = source,
        DeliveryId = deliveryId,
        EventType = string.IsNullOrWhiteSpace(eventType) ? null : eventType,
        Payload = System.Text.Encoding.UTF8.GetString(body),
    };
    foreach (var target in settings.Targets.Where(t => t.Wants(source)))
        message.Deliveries.Add(new Delivery { TargetName = target.Name, TargetUrl = target.Url });

    db.Messages.Add(message);
    try
    {
        await db.SaveChangesAsync();
    }
    catch (DbUpdateException)
    {
        // Two copies arrived at the same moment and the other one won the unique index.
        return Results.Ok(new { duplicate = true });
    }

    log.LogInformation("Accepted {DeliveryId} from {Source} for {Count} target(s)",
        deliveryId, source, message.Deliveries.Count);

    // 202 Accepted = "got it, delivering in the background". Senders like GitHub expect
    // a fast reply, so we never make them wait on our targets.
    return Results.Accepted(value: new { messageId = message.Id, targets = message.Deliveries.Count });
});

// ---------------------------------------------------------------------------
// Dashboard API
// ---------------------------------------------------------------------------
app.MapGet("/api/messages", async (RelayDbContext db, string? status, int? take) =>
{
    var query = db.Messages.Include(m => m.Deliveries).AsQueryable();
    if (Enum.TryParse<DeliveryStatus>(status, ignoreCase: true, out var s))
        query = query.Where(m => m.Deliveries.Any(d => d.Status == s));

    var messages = await query
        .OrderByDescending(m => m.ReceivedAtUtc)
        .Take(Math.Clamp(take ?? 100, 1, 500))
        .ToListAsync();

    return messages.Select(m => new
    {
        m.Id,
        m.Source,
        m.DeliveryId,
        m.EventType,
        m.ReceivedAtUtc,
        m.Payload,
        Deliveries = m.Deliveries.OrderBy(d => d.TargetName).Select(d => new
        {
            d.Id,
            d.TargetName,
            Status = d.Status.ToString(),
            d.Attempts,
            d.NextAttemptAtUtc,
            d.DeliveredAtUtc,
            d.LastError,
        }),
    });
});

app.MapGet("/api/stats", async (RelayDbContext db) =>
{
    var counts = await db.Deliveries
        .GroupBy(d => d.Status)
        .Select(g => new { Status = g.Key, Count = g.Count() })
        .ToListAsync();
    return counts.ToDictionary(c => c.Status.ToString(), c => c.Count);
});

// Replay: put any delivery back in the queue, e.g. after fixing a broken target.
app.MapPost("/api/deliveries/{id:guid}/replay", async (Guid id, RelayDbContext db) =>
{
    var delivery = await db.Deliveries.FindAsync(id);
    if (delivery is null)
        return Results.NotFound();

    delivery.Status = DeliveryStatus.Pending;
    delivery.Attempts = 0;
    delivery.NextAttemptAtUtc = DateTime.UtcNow;
    delivery.LastError = null;
    await db.SaveChangesAsync();
    return Results.Ok(new { replayed = id });
});

// ---------------------------------------------------------------------------
// Demo targets, so you can watch deliveries succeed and fail without any other app.
// ---------------------------------------------------------------------------
app.MapPost("/demo/echo", (HttpRequest request, ILogger<Program> log) =>
{
    log.LogInformation("Demo echo received message {MessageId}", request.Headers["X-Relay-Message-Id"].ToString());
    return Results.Ok();
});

app.MapPost("/demo/flaky", () =>
    Random.Shared.NextDouble() < 0.6
        ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
        : Results.Ok());

app.MapPost("/demo/broken", () => Results.StatusCode(StatusCodes.Status500InternalServerError));

app.Run();

// Lets the test project start the app in memory.
public partial class Program;
