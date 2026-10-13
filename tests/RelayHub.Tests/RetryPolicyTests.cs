using RelayHub.Workers;

namespace RelayHub.Tests;

public class RetryPolicyTests
{
    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 16)]
    public void Doubles_the_wait_after_each_failure(int failedAttempts, double expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), RetryPolicy.DelayAfter(failedAttempts, 2, 300));
    }

    [Fact]
    public void Never_waits_longer_than_the_cap()
    {
        Assert.Equal(TimeSpan.FromSeconds(300), RetryPolicy.DelayAfter(20, 2, 300));
    }
}
