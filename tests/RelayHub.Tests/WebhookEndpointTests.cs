using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using RelayHub.Data;
using RelayHub.Security;
using RelayHub.Workers;

namespace RelayHub.Tests;

/// <summary>
/// Starts the whole app in memory with its own throwaway database and sends it real HTTP requests.
/// </summary>
public class WebhookEndpointTests : IDisposable
{
    private const string Secret = "test-secret";
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"relayhub-test-{Guid.NewGuid()}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public WebhookEndpointTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseSetting("ConnectionStrings:RelayDb", $"Data Source={_dbPath};Pooling=False");
            host.UseSetting("Relay:WorkerEnabled", "false");
            host.UseSetting("Relay:Sources:demo:Secret", Secret);
            // Send the worker's outgoing calls into this same in-memory app, so the
            // demo targets (/demo/echo, /demo/broken) answer them.
            host.ConfigureServices(services => services
                .AddHttpClient(DeliveryWorker.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => _factory!.Server.CreateHandler()));
        });
        _client = _factory.CreateClient();
    }

    private static HttpRequestMessage SignedRequest(string body, string deliveryId, string secret = Secret)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/demo")
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Headers.Add("X-Signature-256", "sha256=" + SignatureVerifier.Sign(bytes, secret));
        request.Headers.Add("X-Delivery-Id", deliveryId);
        request.Headers.Add("X-Event-Type", "order.created");
        return request;
    }

    [Fact]
    public async Task Accepts_a_signed_webhook_and_queues_deliveries()
    {
        var response = await _client.SendAsync(SignedRequest("""{"id":1}""", "d-1"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var messages = await _client.GetFromJsonAsync<JsonElement>("/api/messages");
        var message = Assert.Single(messages.EnumerateArray());
        Assert.Equal("order.created", message.GetProperty("eventType").GetString());
        // appsettings.json subscribes echo, flaky and broken to the demo source.
        Assert.Equal(3, message.GetProperty("deliveries").GetArrayLength());
    }

    [Fact]
    public async Task Rejects_a_webhook_with_a_bad_signature()
    {
        var response = await _client.SendAsync(SignedRequest("""{"id":1}""", "d-1", secret: "wrong"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var messages = await _client.GetFromJsonAsync<JsonElement>("/api/messages");
        Assert.Equal(0, messages.GetArrayLength());
    }

    [Fact]
    public async Task Stores_a_duplicate_delivery_only_once()
    {
        var first = await _client.SendAsync(SignedRequest("""{"id":1}""", "same-id"));
        var second = await _client.SendAsync(SignedRequest("""{"id":1}""", "same-id"));

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Contains("\"duplicate\":true", await second.Content.ReadAsStringAsync());
        var messages = await _client.GetFromJsonAsync<JsonElement>("/api/messages");
        Assert.Equal(1, messages.GetArrayLength());
    }

    [Fact]
    public async Task Returns_404_for_an_unknown_source()
    {
        var response = await _client.PostAsync("/webhooks/nobody", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private DeliveryWorker Worker => _factory.Services
        .GetServices<Microsoft.Extensions.Hosting.IHostedService>()
        .OfType<DeliveryWorker>().Single();

    private async Task PointAllDeliveriesAt(string path)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
        foreach (var d in db.Deliveries)
            d.TargetUrl = new Uri(_client.BaseAddress!, path).ToString();
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Worker_delivers_to_a_healthy_target()
    {
        await _client.SendAsync(SignedRequest("""{"id":1}""", "d-ok"));
        await PointAllDeliveriesAt("/demo/echo");

        await Worker.DeliverDueAsync(CancellationToken.None);

        using var check = _factory.Services.CreateScope();
        var deliveries = check.ServiceProvider.GetRequiredService<RelayDbContext>().Deliveries.ToList();
        Assert.All(deliveries, d =>
        {
            Assert.Equal(DeliveryStatus.Delivered, d.Status);
            Assert.Equal(1, d.Attempts);
        });
    }

    [Fact]
    public async Task Worker_retries_failures_and_dead_letters_after_max_attempts()
    {
        await _client.SendAsync(SignedRequest("""{"id":1}""", "d-worker"));
        await PointAllDeliveriesAt("/demo/broken");
        var worker = Worker;

        // /demo/broken always answers 500. Make each retry due immediately instead of
        // waiting out the backoff: 5 failures should mean dead letter.
        for (var i = 0; i < 5; i++)
        {
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
                foreach (var d in db.Deliveries)
                    d.NextAttemptAtUtc = DateTime.UtcNow.AddSeconds(-1);
                await db.SaveChangesAsync();
            }
            await worker.DeliverDueAsync(CancellationToken.None);
        }

        using var check = _factory.Services.CreateScope();
        var deliveries = check.ServiceProvider.GetRequiredService<RelayDbContext>().Deliveries.ToList();
        Assert.All(deliveries, d =>
        {
            Assert.Equal(DeliveryStatus.DeadLettered, d.Status);
            Assert.Equal(5, d.Attempts);
            Assert.NotNull(d.LastError);
        });
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }
}
