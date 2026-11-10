using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;

namespace Orders.Console.Integrations.Aws;

/// <summary>
/// The demo run number from SSM (<c>/orders-demo/demo-run</c>) and the scoreboard snapshot kept per run in the
/// metrics bucket (named in SSM <c>/orders-demo/metrics-bucket</c>), key <c>runs/&lt;N&gt;.json</c>. The bucket
/// outlives <c>just env down</c>, so runs can be compared afterwards.
/// </summary>
public sealed class AwsDemoRuns(IAmazonSimpleSystemsManagement ssm, IAmazonS3 s3, ConsoleOptions options, TimeProvider time) : IDemoRuns
{
    public const string Prefix = "runs/";

    private (DateTimeOffset Until, string? Run)? _run;
    private string? _bucket;
    private (DateTimeOffset Until, IReadOnlyList<RunSnapshotFile> Files)? _list;

    public async Task<string?> GetCurrentRunAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (_run is { } hit && hit.Until > now)
        {
            return hit.Run;
        }

        var run = (await ParameterAsync(options.DemoRunParameter, ct))?.Trim();
        _run = (now + TimeSpan.FromMinutes(1), string.IsNullOrEmpty(run) ? null : run);
        return _run.Value.Run;
    }

    public async Task<SavedSnapshot> SaveAsync(string run, string json, CancellationToken ct)
    {
        var bucket = await BucketAsync(ct);
        var key = Key(run);
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            ContentBody = json,
            ContentType = "application/json",
        }, ct);
        _list = null;
        return new SavedSnapshot($"s3://{bucket}/{key}",
            $"https://{options.AwsRegion}.console.aws.amazon.com/s3/object/{bucket}?region={options.AwsRegion}&prefix={Uri.EscapeDataString(key)}");
    }

    public async Task<IReadOnlyList<RunSnapshotFile>> ListAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (_list is { } hit && hit.Until > now)
        {
            return hit.Files;
        }

        var bucket = await BucketAsync(ct);
        var objects = new List<S3Object>();
        string? token = null;
        do
        {
            var page = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = Prefix, ContinuationToken = token }, ct);
            objects.AddRange(page.S3Objects ?? []);
            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        }
        while (token is not null);

        var files = new List<RunSnapshotFile>();
        foreach (var o in objects.Where(o => o.Key.EndsWith(".json", StringComparison.Ordinal)).OrderByDescending(o => o.LastModified).Take(12))
        {
            using var response = await s3.GetObjectAsync(bucket, o.Key, ct);
            using var reader = new StreamReader(response.ResponseStream, Encoding.UTF8);
            var run = o.Key[Prefix.Length..^".json".Length];
            var saved = o.LastModified is { } at ? new DateTimeOffset(DateTime.SpecifyKind(at.ToUniversalTime(), DateTimeKind.Utc)) : now;
            files.Add(new RunSnapshotFile(run, saved, await reader.ReadToEndAsync(ct)));
        }

        _list = (now + TimeSpan.FromMinutes(1), files);
        return files;
    }

    public static string Key(string run) => $"{Prefix}{run}.json";

    private async Task<string> BucketAsync(CancellationToken ct) =>
        _bucket ??= options.MetricsBucket
            ?? await ParameterAsync(options.MetricsBucketParameter, ct)
            ?? throw new InvalidOperationException($"No metrics bucket: set METRICS_BUCKET or the SSM parameter {options.MetricsBucketParameter}.");

    private async Task<string?> ParameterAsync(string name, CancellationToken ct)
    {
        try
        {
            return (await ssm.GetParameterAsync(new GetParameterRequest { Name = name }, ct)).Parameter?.Value;
        }
        catch (ParameterNotFoundException)
        {
            return null;
        }
    }
}
