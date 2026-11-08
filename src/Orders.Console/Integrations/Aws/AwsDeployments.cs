using Amazon.ECS;
using Amazon.ECS.Model;
using Orders.Console.Domain;

namespace Orders.Console.Integrations.Aws;

/// <summary>ECS deployments of the prod and staging services, and rollbacks by the deployment circuit breaker.</summary>
public sealed class AwsDeployments(IAmazonECS ecs, ConsoleOptions options) : IDeploymentService, IEventSource
{
    public string Name => "ecs";

    public TimeSpan Interval => options.AwsPollInterval;

    public async Task<IReadOnlyList<DeployRecord>> GetRecentDeploysAsync(CancellationToken ct)
    {
        var services = await DescribeAsync(ct);
        return services.SelectMany(s => (s.Service.Deployments ?? []).Select(d => new DeployRecord(
                s.Environment,
                Revision(d.TaskDefinition),
                d.RolloutState?.Value?.ToLowerInvariant() ?? d.Status?.ToLowerInvariant() ?? "unknown",
                Utc(d.UpdatedAt ?? d.CreatedAt),
                ServiceUrl(s.Service.ServiceName))))
            .OrderByDescending(d => d.At)
            .ToList();
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
