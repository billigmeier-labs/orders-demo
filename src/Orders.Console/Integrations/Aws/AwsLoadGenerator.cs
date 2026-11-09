using System.Globalization;
using Amazon.ECS;
using Amazon.ECS.Model;
using EcsKeyValue = Amazon.ECS.Model.KeyValuePair;
using EcsTask = Amazon.ECS.Model.Task;
using Task = System.Threading.Tasks.Task;

namespace Orders.Console.Integrations.Aws;

/// <summary>Starts and stops the <c>loadgen</c> ECS task (Fargate) with SCENARIO and DURATION_MINUTES.</summary>
public sealed class AwsLoadGenerator(IAmazonECS ecs, ConsoleOptions options) : ILoadGenerator
{
    public const string StartedBy = "orders-console";
    public const string ProjectTagKey = "project";
    public const string ProjectTagValue = "orders-demo";

    /// <summary>The console's task role may stop only tasks with this tag (infra/terraform/env/iam.tf), so it can never stop a service task.</summary>
    public const string ComponentTagKey = "component";
    public const string ComponentTagValue = "loadgen";

    public async Task<LoadStatus> GetStatusAsync(CancellationToken ct)
    {
        var tasks = await RunningAsync(ct);
        var task = tasks.OrderByDescending(t => t.CreatedAt).FirstOrDefault();
        if (task is null)
        {
            return new LoadStatus(false, null, null, null, null);
        }

        var env = task.Overrides?.ContainerOverrides?.SelectMany(c => c.Environment ?? []).ToDictionary(e => e.Name, e => e.Value) ?? [];
        var started = task.StartedAt ?? task.CreatedAt;
        DateTimeOffset? startedAt = started is { } s ? new DateTimeOffset(DateTime.SpecifyKind(s.ToUniversalTime(), DateTimeKind.Utc)) : null;
        DateTimeOffset? endsAt = startedAt is not null && env.TryGetValue("DURATION_MINUTES", out var m) && int.TryParse(m, CultureInfo.InvariantCulture, out var minutes)
            ? startedAt.Value.AddMinutes(minutes)
            : null;
        return new LoadStatus(true, env.GetValueOrDefault("SCENARIO"), startedAt, endsAt, task.TaskArn?[(task.TaskArn.LastIndexOf('/') + 1)..]);
    }

    public async Task StartAsync(string scenario, int durationMinutes, CancellationToken ct)
    {
        if (options.LoadgenSubnets.Count == 0)
        {
            throw new InvalidOperationException("LOADGEN_SUBNETS is not set.");
        }

        await StopAsync(ct);
        var response = await ecs.RunTaskAsync(RunTaskRequestFor(scenario, durationMinutes), ct);

        if (response.Failures is { Count: > 0 } failures)
        {
            throw new InvalidOperationException($"RunTask failed: {failures[0].Reason} {failures[0].Detail}");
        }
    }

    /// <summary>The loadgen RunTask call. Tasks don't inherit tags by default, so the task gets <c>project=orders-demo</c> itself
    /// and Cost Explorer counts it with the rest of the demo, plus <c>component=loadgen</c>, which is what lets the console
    /// stop it. (Setting the tags needs ecs:TagResource.)</summary>
    internal RunTaskRequest RunTaskRequestFor(string scenario, int durationMinutes) =>
        new()
        {
            Cluster = options.EcsCluster,
            TaskDefinition = options.LoadgenTaskDefinition,
            LaunchType = LaunchType.FARGATE,
            Count = 1,
            StartedBy = StartedBy,
            Tags =
            [
                new Tag { Key = ProjectTagKey, Value = ProjectTagValue },
                new Tag { Key = ComponentTagKey, Value = ComponentTagValue },
            ],
            NetworkConfiguration = new NetworkConfiguration
            {
                AwsvpcConfiguration = new AwsVpcConfiguration
                {
                    Subnets = options.LoadgenSubnets.ToList(),
                    SecurityGroups = options.LoadgenSecurityGroups.ToList(),
                    AssignPublicIp = options.LoadgenAssignPublicIp ? AssignPublicIp.ENABLED : AssignPublicIp.DISABLED,
                },
            },
            Overrides = new TaskOverride
            {
                ContainerOverrides =
                [
                    new ContainerOverride
                    {
                        Name = options.LoadgenContainer,
                        Environment =
                        [
                            new EcsKeyValue { Name = "SCENARIO", Value = scenario },
                            new EcsKeyValue { Name = "DURATION_MINUTES", Value = durationMinutes.ToString(CultureInfo.InvariantCulture) },
                        ],
                    },
                ],
            },
        };

    public async Task StopAsync(CancellationToken ct)
    {
        foreach (var task in await RunningAsync(ct))
        {
            await ecs.StopTaskAsync(new StopTaskRequest { Cluster = options.EcsCluster, Task = task.TaskArn, Reason = "Stopped from the demo console" }, ct);
        }
    }

    private async Task<List<EcsTask>> RunningAsync(CancellationToken ct)
    {
        var list = await ecs.ListTasksAsync(new ListTasksRequest { Cluster = options.EcsCluster, StartedBy = StartedBy, DesiredStatus = DesiredStatus.RUNNING }, ct);
        if (list.TaskArns is not { Count: > 0 } arns)
        {
            return [];
        }

        var described = await ecs.DescribeTasksAsync(new DescribeTasksRequest { Cluster = options.EcsCluster, Tasks = arns }, ct);
        return described.Tasks ?? [];
    }
}
