using System.Collections.Concurrent;
using Amazon.ECS;
using Amazon.ECS.Model;
using Orders.Console.Domain;

namespace Orders.Console.Integrations.Aws;

/// <summary>ECS deployments of the prod and staging services, and rollbacks by the deployment circuit breaker.</summary>
public sealed class AwsDeployments(IAmazonECS ecs, ConsoleOptions options) : IDeploymentService, IEventSource
{
    public string Name => "ecs";

    public TimeSpan Interval => options.AwsPollInterval;

    // Task definition revisions never change, so the commit each one runs is cached for good.
    private readonly ConcurrentDictionary<string, string?> _commits = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<DeployRecord>> GetRecentDeploysAsync(CancellationToken ct)
    {
        var services = await DescribeAsync(ct);
        var deployments = services.SelectMany(s => (s.Service.Deployments ?? []).Select(d => (s.Environment, s.Service, Deployment: d))).ToList();
        foreach (var arn in deployments.Select(d => d.Deployment.TaskDefinition).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            await CommitAsync(arn, ct);
        }

        return deployments.Select(x => new DeployRecord(
                x.Environment,
                Revision(x.Deployment.TaskDefinition),
                x.Deployment.RolloutState?.Value?.ToLowerInvariant() ?? x.Deployment.Status?.ToLowerInvariant() ?? "unknown",
                Utc(x.Deployment.UpdatedAt ?? x.Deployment.CreatedAt),
                ServiceUrl(x.Service.ServiceName),
                x.Deployment.TaskDefinition is { } arn ? _commits.GetValueOrDefault(arn) : null))
            .OrderByDescending(d => d.At)
            .ToList();
    }

    /// <summary>The commit SHA a task definition runs: its container image tag (release tags are the full SHA).</summary>
    private async Task<string?> CommitAsync(string taskDefinitionArn, CancellationToken ct)
    {
        if (_commits.TryGetValue(taskDefinitionArn, out var known))
        {
            return known;
        }

        DescribeTaskDefinitionResponse response;
        try
        {
            response = await ecs.DescribeTaskDefinitionAsync(new DescribeTaskDefinitionRequest { TaskDefinition = taskDefinitionArn }, ct);
        }
        catch (AmazonECSException)
        {
            // Show the task definition instead; try again on the next read.
            return null;
        }

        var commit = (response.TaskDefinition?.ContainerDefinitions ?? [])
            .Select(c => CommitFromImage(c.Image))
            .FirstOrDefault(c => c is not null);
        _commits[taskDefinitionArn] = commit;
        return commit;
    }

    /// <summary>"…/orders-api:3f2c…" → the tag, when it is a commit SHA (7 to 40 hex characters); else null.</summary>
    internal static string? CommitFromImage(string? image)
    {
        if (string.IsNullOrEmpty(image) || image.Contains('@', StringComparison.Ordinal))
        {
            return null;
        }

        var colon = image.LastIndexOf(':');
        if (colon < 0 || colon < image.LastIndexOf('/'))
        {
            return null;
        }

        var tag = image[(colon + 1)..];
        return tag.Length is >= 7 and <= 40 && tag.All(char.IsAsciiHexDigitLower) ? tag : null;
    }

    public async Task<IReadOnlyList<ConsoleEvent>> PollAsync(DateTimeOffset since, CancellationToken ct)
    {
        var events = new List<ConsoleEvent>();
        foreach (var (environment, service) in await DescribeAsync(ct))
        {
            foreach (var d in service.Deployments ?? [])
            {
                var at = Utc(d.UpdatedAt ?? d.CreatedAt);
                if (d.RolloutState?.Value == "FAILED" && at >= since)
                {
                    events.Add(new ConsoleEvent(at, EventKinds.DeployRolledBack, environment,
                        $"{Revision(d.TaskDefinition)}: {d.RolloutStateReason}", ServiceUrl(service.ServiceName)));
                }
            }

            foreach (var e in service.Events ?? [])
            {
                var at = Utc(e.CreatedAt);
                if (at >= since && e.Message is { } m && m.Contains("rolling back", StringComparison.OrdinalIgnoreCase))
                {
                    events.Add(new ConsoleEvent(at, EventKinds.DeployRolledBack, environment, m, ServiceUrl(service.ServiceName)));
                }
            }
        }

        return events;
    }

    private async Task<List<(string Environment, Service Service)>> DescribeAsync(CancellationToken ct)
    {
        var response = await ecs.DescribeServicesAsync(new DescribeServicesRequest
        {
            Cluster = options.EcsCluster,
            Services = [options.EcsProdService, options.EcsStagingService],
        }, ct);
        return (response.Services ?? []).Select(s => (s.ServiceName == options.EcsProdService ? "prod" : "staging", s)).ToList();
    }

    private string ServiceUrl(string service) =>
        $"https://{options.AwsRegion}.console.aws.amazon.com/ecs/v2/clusters/{options.EcsCluster}/services/{service}/deployments?region={options.AwsRegion}";

    private static string Revision(string? taskDefinitionArn) =>
        taskDefinitionArn is null ? "unknown" : taskDefinitionArn[(taskDefinitionArn.LastIndexOf('/') + 1)..];

    private static DateTimeOffset Utc(DateTime? value) =>
        value is { } v ? new DateTimeOffset(DateTime.SpecifyKind(v.ToUniversalTime(), DateTimeKind.Utc)) : DateTimeOffset.MinValue;
}
