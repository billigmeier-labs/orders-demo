using System.Reflection;
using System.Text;
using Amazon.AppConfigData;
using Amazon.AppConfigData.Model;
using Microsoft.Extensions.Logging;
using Orders.Api.Infrastructure;

namespace Orders.UnitTests;

/// <summary>A hand-written AppConfigData client: records requests and answers from a script.</summary>
public class FakeAppConfigData : DispatchProxy
{
    public List<StartConfigurationSessionRequest> Sessions { get; } = [];

    public List<GetLatestConfigurationRequest> Polls { get; } = [];

    /// <summary>Answers each poll in order; a null entry throws.</summary>
    public Queue<GetLatestConfigurationResponse?> Responses { get; } = new();

    /// <summary>Called with the poll count after each poll is answered.</summary>
    public Action<int>? OnPoll { get; set; }

    public static (IAmazonAppConfigData Client, FakeAppConfigData Fake) Create()
    {
        var client = Create<IAmazonAppConfigData, FakeAppConfigData>();
        return (client, (FakeAppConfigData)(object)client);
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        switch (targetMethod?.Name)
        {
            case nameof(IAmazonAppConfigData.StartConfigurationSessionAsync):
                Sessions.Add((StartConfigurationSessionRequest)args![0]!);
                return Task.FromResult(new StartConfigurationSessionResponse { InitialConfigurationToken = $"session-{Sessions.Count}" });
            case nameof(IAmazonAppConfigData.GetLatestConfigurationAsync):
                Polls.Add((GetLatestConfigurationRequest)args![0]!);
                var response = Responses.Dequeue();
                OnPoll?.Invoke(Polls.Count);
                return response is null
                    ? Task.FromException<GetLatestConfigurationResponse>(new InvalidOperationException("AppConfig is down"))
                    : Task.FromResult(response);
            default:
                return null;
        }
    }
}

internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));
}

public class AppConfigPollerTests
{
    internal static readonly AppConfigSettings Settings = new("orders-demo", "staging", "flags-profile");

    internal static GetLatestConfigurationResponse Reply(string? json, string next = "next") => new()
    {
        Configuration = json is null ? new MemoryStream() : new MemoryStream(Encoding.UTF8.GetBytes(json)),
        NextPollConfigurationToken = next,
        NextPollIntervalInSeconds = 0,
    };

    /// <summary>Starts the poller, waits until <paramref name="stopAfterPolls"/> polls were answered, then stops it.</summary>
    internal static async Task<CapturingLogger<AppConfigPoller>> RunAsync(
        FakeAppConfigData fake, IAmazonAppConfigData client, AppConfigFeatureFlags flags, int stopAfterPolls, Func<bool>? settled = null)
    {
        var logger = new CapturingLogger<AppConfigPoller>();
        var done = new TaskCompletionSource();
        fake.OnPoll = count =>
        {
            if (count >= stopAfterPolls)
            {
                done.TrySetResult();
            }
        };

        using var poller = new AppConfigPoller(client, Settings, flags, logger);
        await poller.StartAsync(TestContext.Current.CancellationToken);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        while (settled is not null && !settled())
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        await poller.StopAsync(TestContext.Current.CancellationToken);
        Assert.True(poller.ExecuteTask!.IsCompletedSuccessfully);
        return logger;
    }

    [Fact]
    public async Task FirstPoll_StartsASessionWithTheSettings_UpdatesFlagsAndLogsSortedSummary()
    {
        var (client, fake) = FakeAppConfigData.Create();
        fake.Responses.Enqueue(Reply("""{ "zeta": { "enabled": true }, "alpha": { "enabled": false }, "mid": { "enabled": true } }"""));
        var flags = new AppConfigFeatureFlags();

        var logger = await RunAsync(fake, client, flags, stopAfterPolls: 1, () => flags.IsEnabled("zeta"));

        var session = Assert.Single(fake.Sessions);
        Assert.Equal("orders-demo", session.ApplicationIdentifier);
        Assert.Equal("staging", session.EnvironmentIdentifier);
        Assert.Equal("flags-profile", session.ConfigurationProfileIdentifier);
        Assert.Equal(15, session.RequiredMinimumPollIntervalInSeconds);
        Assert.Equal("session-1", Assert.Single(fake.Polls).ConfigurationToken);
        Assert.True(flags.IsEnabled("zeta"));
        Assert.True(flags.IsEnabled("mid"));
        Assert.False(flags.IsEnabled("alpha"));
        Assert.Contains((LogLevel.Information, "Feature flags updated from AppConfig: alpha=off, mid=on, zeta=on"), logger.Entries);
    }

    [Fact]
    public async Task EmptyResponse_KeepsTheCachedFlags()
    {
        var (client, fake) = FakeAppConfigData.Create();
        var flags = new AppConfigFeatureFlags();
        flags.Update(AppConfigFeatureFlags.Parse("""{ "searchV2": { "enabled": true } }"""));
        fake.Responses.Enqueue(Reply(null));

        var logger = await RunAsync(fake, client, flags, stopAfterPolls: 1);

        Assert.True(flags.IsEnabled("searchV2"));
        Assert.Empty(logger.Entries);
    }

}

// Separate classes run in parallel, so the two 15 s poll intervals overlap.
public class AppConfigPollerReuseTests
{
    [Fact]
    public async Task SecondPoll_ReusesTheSessionAndTheNextToken()
    {
        var (client, fake) = FakeAppConfigData.Create();
        fake.Responses.Enqueue(AppConfigPollerTests.Reply("""{ "a": { "enabled": true } }""", next: "token-2"));
        fake.Responses.Enqueue(AppConfigPollerTests.Reply("""{ "a": { "enabled": false } }""", next: "token-3"));
        var flags = new AppConfigFeatureFlags();

        await AppConfigPollerTests.RunAsync(fake, client, flags, stopAfterPolls: 2, () => !flags.IsEnabled("a"));

        Assert.Single(fake.Sessions);
        Assert.Equal(["session-1", "token-2"], fake.Polls.Select(p => p.ConfigurationToken));
        Assert.False(flags.IsEnabled("a"));
    }
}

public class AppConfigPollerFailureTests
{
    [Fact]
    public async Task FailedPoll_KeepsOldFlagsLogsAWarningAndStartsAFreshSession()
    {
        var (client, fake) = FakeAppConfigData.Create();
        fake.Responses.Enqueue(null);
        fake.Responses.Enqueue(AppConfigPollerTests.Reply("""{ "a": { "enabled": true } }"""));
        var flags = new AppConfigFeatureFlags();
        flags.Update(AppConfigFeatureFlags.Parse("""{ "old": { "enabled": true } }"""));

        var logger = await AppConfigPollerTests.RunAsync(fake, client, flags, stopAfterPolls: 2, () => flags.IsEnabled("a"));

        Assert.Equal(2, fake.Sessions.Count);
        Assert.Equal(["session-1", "session-2"], fake.Polls.Select(p => p.ConfigurationToken));
        Assert.True(flags.IsEnabled("a"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("orders-demo/staging/flags-profile", StringComparison.Ordinal));
    }
}
