namespace Orders.IntegrationTests;

/// <summary>A clock that always reads the same instant, so rows share a CreatedAt.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
