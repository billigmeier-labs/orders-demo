using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Orders.LoadGenerator;

/// <summary>Counts and latencies per endpoint, for the summary at the end.</summary>
internal sealed class LoadStats
{
    private readonly ConcurrentDictionary<string, Endpoint> _endpoints = new(StringComparer.Ordinal);

    public CallResult Record(string endpoint, int status, TimeSpan elapsed, string? error = null)
    {
        var result = new CallResult(status, elapsed, error);
        _endpoints.GetOrAdd(endpoint, _ => new Endpoint()).Add(result);
        return result;
    }

    public long Total => _endpoints.Values.Sum(e => e.Count);

    public long Failed => _endpoints.Values.Sum(e => e.Failed);

    public string Summary(LoadSettings settings, TimeSpan elapsed)
    {
        var text = new StringBuilder();
        var total = Total;
        var failed = Failed;
        text.AppendLine(CultureInfo.InvariantCulture, $"== Load summary: {settings.Scenario.ToString().ToLowerInvariant()} against {settings.TargetBaseUrl} for {elapsed.TotalMinutes:0.0} min ==");
        text.AppendLine(CultureInfo.InvariantCulture, $"requests {total}, {total / Math.Max(elapsed.TotalSeconds, 1):0.0}/s, failed (5xx or no response) {failed} = {Rate(failed, total):0.00}%");
        text.AppendLine(CultureInfo.InvariantCulture, $"{"endpoint",-22} {"count",7} {"2xx",7} {"4xx",6} {"5xx",6} {"none",5} {"p50 ms",7} {"p95 ms",7} {"p99 ms",7}");
        foreach (var (name, e) in _endpoints.Where(e => e.Key != "setup").OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            var (p50, p95, p99) = e.Percentiles();
            text.AppendLine(CultureInfo.InvariantCulture,
                $"{name,-22} {e.Count,7} {e.Status2xx,7} {e.Status4xx,6} {e.Status5xx,6} {e.NoResponse,5} {p50,7:0} {p95,7:0} {p99,7:0}");
        }

        return text.ToString();
    }

    private static double Rate(long part, long total) => total == 0 ? 0 : 100.0 * part / total;

    private sealed class Endpoint
    {
        private readonly Lock _gate = new();
        private readonly List<double> _millis = [];

        public long Count { get; private set; }
        public long Status2xx { get; private set; }
        public long Status4xx { get; private set; }
        public long Status5xx { get; private set; }
        public long NoResponse { get; private set; }
        public long Failed => Status5xx + NoResponse;

        public void Add(CallResult result)
        {
            lock (_gate)
            {
                Count++;
                switch (result.Status)
                {
                    case 0:
                        NoResponse++;
                        break;
                    case >= 500:
                        Status5xx++;
                        break;
                    case >= 400:
                        Status4xx++;
                        break;
                    default:
                        Status2xx++;
                        break;
                }

                _millis.Add(result.Elapsed.TotalMilliseconds);
            }
        }

        public (double P50, double P95, double P99) Percentiles()
        {
            lock (_gate)
            {
                if (_millis.Count == 0)
                {
                    return (0, 0, 0);
                }

                var sorted = _millis.Order().ToArray();
                double At(double p) => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(p * sorted.Length) - 1)];
                return (At(0.50), At(0.95), At(0.99));
            }
        }
    }
}
