namespace RelayHub.Data;

/// <summary>One webhook we received, stored exactly once.</summary>
public class WebhookMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Source { get; set; } = "";

    /// <summary>The sender's ID for this delivery. Together with Source it is unique.</summary>
    public string DeliveryId { get; set; } = "";

    public string? EventType { get; set; }
    public string Payload { get; set; } = "";
    public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;

    public List<Delivery> Deliveries { get; set; } = [];
}

public enum DeliveryStatus
{
    Pending,
    Delivered,
    DeadLettered,
}

/// <summary>The job of getting one message to one target.</summary>
public class Delivery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MessageId { get; set; }
    public WebhookMessage? Message { get; set; }

    public string TargetName { get; set; } = "";
    public string TargetUrl { get; set; } = "";

    public DeliveryStatus Status { get; set; } = DeliveryStatus.Pending;
    public int Attempts { get; set; }
    public DateTime NextAttemptAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? DeliveredAtUtc { get; set; }
    public string? LastError { get; set; }
}
