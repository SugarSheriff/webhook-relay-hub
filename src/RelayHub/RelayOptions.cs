namespace RelayHub;

/// <summary>
/// Settings bound from the "Relay" section of appsettings.json.
/// </summary>
public class RelayOptions
{
    /// <summary>Apps allowed to send us webhooks, keyed by the {source} in /webhooks/{source}.</summary>
    public Dictionary<string, SourceOptions> Sources { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Apps we forward messages to.</summary>
    public List<TargetOptions> Targets { get; set; } = [];

    /// <summary>How many delivery attempts before a message is parked as a dead letter.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Wait before the first retry; doubles after each failure.</summary>
    public double BaseDelaySeconds { get; set; } = 2;

    /// <summary>Longest we will ever wait between retries.</summary>
    public double MaxDelaySeconds { get; set; } = 300;

    /// <summary>Turn the background delivery worker off (useful in tests).</summary>
    public bool WorkerEnabled { get; set; } = true;
}

public class SourceOptions
{
    /// <summary>Shared secret used to sign webhooks. Never commit real secrets.</summary>
    public string Secret { get; set; } = "";

    /// <summary>Header carrying the HMAC signature, e.g. X-Hub-Signature-256 for GitHub.</summary>
    public string SignatureHeader { get; set; } = "X-Signature-256";

    /// <summary>Text before the hex signature, e.g. "sha256=" for GitHub.</summary>
    public string SignaturePrefix { get; set; } = "sha256=";

    /// <summary>Header with the sender's unique ID for this delivery, used to spot duplicates.</summary>
    public string DeliveryIdHeader { get; set; } = "X-Delivery-Id";

    /// <summary>Header naming the kind of event, e.g. X-GitHub-Event.</summary>
    public string EventHeader { get; set; } = "X-Event-Type";
}

public class TargetOptions
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";

    /// <summary>Which sources this target wants. Empty means all of them.</summary>
    public List<string> Sources { get; set; } = [];

    public bool Wants(string source) =>
        Sources.Count == 0 || Sources.Contains(source, StringComparer.OrdinalIgnoreCase);
}
