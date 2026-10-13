using System.Text;
using RelayHub.Security;

namespace RelayHub.Tests;

public class SignatureVerifierTests
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"hello":"world"}""");

    [Fact]
    public void Accepts_a_correct_signature()
    {
        var header = "sha256=" + SignatureVerifier.Sign(Body, "secret");
        Assert.True(SignatureVerifier.IsValid(Body, "secret", header, "sha256="));
    }

    [Fact]
    public void Matches_GitHubs_documented_example()
    {
        // From GitHub's "Validating webhook deliveries" docs.
        var body = Encoding.UTF8.GetBytes("Hello, World!");
        const string header = "sha256=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e17";
        Assert.True(SignatureVerifier.IsValid(body, "It's a Secret to Everybody", header, "sha256="));
    }

    [Fact]
    public void Rejects_a_signature_made_with_the_wrong_secret()
    {
        var header = "sha256=" + SignatureVerifier.Sign(Body, "wrong");
        Assert.False(SignatureVerifier.IsValid(Body, "secret", header, "sha256="));
    }

    [Fact]
    public void Rejects_a_tampered_body()
    {
        var header = "sha256=" + SignatureVerifier.Sign(Body, "secret");
        var tampered = Encoding.UTF8.GetBytes("""{"hello":"there"}""");
        Assert.False(SignatureVerifier.IsValid(tampered, "secret", header, "sha256="));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("md5=abc")]
    public void Rejects_missing_or_malformed_headers(string? header)
    {
        Assert.False(SignatureVerifier.IsValid(Body, "secret", header, "sha256="));
    }

    [Fact]
    public void Rejects_everything_when_no_secret_is_configured()
    {
        var header = "sha256=" + SignatureVerifier.Sign(Body, "");
        Assert.False(SignatureVerifier.IsValid(Body, "", header, "sha256="));
    }
}
