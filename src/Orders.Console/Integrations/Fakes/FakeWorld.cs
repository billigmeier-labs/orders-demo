using System.Globalization;
using Orders.Console.Domain;

namespace Orders.Console.Integrations.Fakes;

/// <summary>
/// A small simulated GitHub + AWS used when CONSOLE_FAKES=true. It starts with realistic history and
/// reacts to the console's actions the way the real system does, one "step" (CONSOLE_FAKE_STEP_SECONDS)
/// at a time: a new issue is triaged, an agent opens a PR, checks pass, the queue merges, the deploy
/// succeeds; the canned bug PR deploys, alarms and rolls back; the fault flag fires an alarm that opens
/// an incident issue which an agent fixes. Time comes from <see cref="TimeProvider"/> so tests can drive it.
/// </summary>
public sealed class FakeWorld :
    IGitHubService, IEventSource, IMonitoringService, IDeploymentService, ICostService,
    IFeatureFlagService, ILoadGenerator, IEnvironmentProbe
{
    public const string Automation = "orders-demo-automation";
    public const string CannedBugBranch = "demo/canned-bug";
    public const string ErrorAlarm = "orders-prod-5xx-rate";
    public const string FaultFlag = "searchV2";

    private static readonly string[] LatestWorkflows = ["ci", "ai-review", "deploy"];

    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly TimeSpan _step;
    private readonly string _repoUrl;
    private readonly string _agent;
    private readonly List<FakeIssue> _issues = [];
    private readonly List<FakePull> _pulls = [];
    private readonly List<WorkflowRunSummary> _runs = [];
    private readonly List<DeployRecord> _deploys = [];
    private readonly Dictionary<string, AlarmState> _alarms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (bool Enabled, string Description)> _flags = new(StringComparer.Ordinal);
    private readonly List<ConsoleEvent> _events = [];
    private readonly List<(DateTimeOffset Due, long Seq, Action Act)> _schedule = [];
    private long _seq;
    private int _next = 140;
    private long _runId = 9_100_000;
    private int _patch;
    private string _commit = "a3f9c21";
    private LoadStatus _load = new(false, null, null, null, null);
    private bool _faultFixed;

    public FakeWorld(TimeProvider time, ConsoleOptions options)
    {
        _time = time;
        _step = options.FakeStep;
        _repoUrl = options.RepoUrl;
        _agent = options.AgentLogin;
        Seed();
    }

    public string Name => "fake";

    public TimeSpan Interval => TimeSpan.FromSeconds(1);

    private DateTimeOffset Now => _time.GetUtcNow();

    private string Version => $"1.4.{_patch}";

    // ---- IEventSource ----

    public Task<IReadOnlyList<ConsoleEvent>> PollAsync(DateTimeOffset since, CancellationToken ct) =>
        Locked<IReadOnlyList<ConsoleEvent>>(() => _events.Where(e => e.At >= since).ToList());

    /// <summary>Every event so far, for tests.</summary>
    public IReadOnlyList<ConsoleEvent> Events => Locked<IReadOnlyList<ConsoleEvent>>(() => _events.ToList()).Result;

    // ---- IGitHubService ----

    public Task<IssueRef> CreateIssueAsync(NewIssue issue, CancellationToken ct) => Locked(() =>
    {
        var created = AddIssue(issue.Title, issue.Body, issue.Labels, Automation);
        return new IssueRef(created.Number, IssueUrl(created.Number));
    });

    public Task CloseIssueAsync(int number, CancellationToken ct) => Locked(() =>
    {
        var issue = _issues.FirstOrDefault(i => i.Number == number) ?? throw new InvalidOperationException($"No issue #{number}.");
        issue.Open = false;
        return true;
    });

    public Task<PullRequestRef> OpenPullRequestAsync(NewPullRequest pr, CancellationToken ct) => Locked(() =>
    {
        if (_pulls.Any(p => p.Head == pr.Head && p.State == "open"))
        {
            throw new InvalidOperationException($"A pull request already exists for {pr.Head}.");
        }

        var pull = AddPull(pr.Title, pr.Body, pr.Head, Automation, issue: null);
        pull.Bad = pr.Head == CannedBugBranch;
        After(1, () => Checks(pull));
        return new PullRequestRef(pull.Number, PullUrl(pull.Number));
    });

    public Task ClosePullRequestAsync(int number, CancellationToken ct) => Locked(() =>
    {
        var pull = _pulls.FirstOrDefault(p => p.Number == number) ?? throw new InvalidOperationException($"No pull request #{number}.");
        if (pull.State == "open")
        {
            pull.State = "closed";
            Emit(EventKinds.PreviewDown, ConsoleEvent.PullSubject(number), $"pr-{number}.demo.joshb.app removed", PullUrl(number));
        }

        return true;
    });

    // The fake AI review always passes, so an override comment changes nothing here.
    public Task CommentOnPullRequestAsync(int number, string body, CancellationToken ct) => Locked(() =>
        _pulls.Any(p => p.Number == number) ? true : throw new InvalidOperationException($"No pull request #{number}."));

    public Task<GitHubActivity> GetActivityAsync(CancellationToken ct) => Locked(() =>
    {
        var open = _issues.Where(i => i.Open).ToList();
        var byLabel = open.SelectMany(i => i.Labels).GroupBy(l => l)
            .Select(g => new LabelCount(g.Key, g.Count())).OrderByDescending(l => l.Count).ThenBy(l => l.Label, StringComparer.Ordinal).ToList();
        var agentPulls = _pulls.Where(p => p.Author == _agent).OrderByDescending(p => p.CreatedAt).Take(6).Select(Summary).ToList();
        var latest = LatestWorkflows
            .Select(w => _runs.Where(r => r.Workflow == w).MaxBy(r => r.UpdatedAt))
            .OfType<WorkflowRunSummary>().ToList();
        return new GitHubActivity(byLabel, open.Count, agentPulls, latest);
    });

    public Task<IReadOnlyList<IssueWithPulls>> ListIssuesAsync(string label, CancellationToken ct, string state = "open") => Locked<IReadOnlyList<IssueWithPulls>>(() =>
        _issues.Where(i => (state == "all" || i.Open) && i.Labels.Contains(label))
            .OrderByDescending(i => i.CreatedAt)
            .Take(state == "all" ? 6 : 50)
            .Select(i => new IssueWithPulls(IssueSummary(i), _pulls.Where(p => p.Issue == i.Number).Select(Summary).ToList()))
            .ToList());

    public Task<IReadOnlyList<PullRequestSummary>> ListAwaitingReviewAsync(CancellationToken ct) => Locked<IReadOnlyList<PullRequestSummary>>(() =>
        _pulls.Where(p => p.State == "open" && p.ReviewRequired).OrderByDescending(p => p.CreatedAt).Select(Summary).ToList());

    public Task<DeliveryStats> GetDeliveryStatsAsync(CancellationToken ct) => Locked(() =>
    {
        var ci = _runs.Where(r => r.Workflow == "ci" && r.Duration is not null).Select(r => r.Duration!.Value).ToList();
        var merged = _pulls.Where(p => p.MergedAt is not null).ToList();
        var agentMerged = merged.Where(p => p.Author == _agent).ToList();
        return new DeliveryStats(
            ci,
            merged.Select(p => p.MergedAt!.Value - p.CreatedAt).ToList(),
            agentMerged.Count,
            agentMerged.Count(p => !p.ChangesRequested),
            _runs.Where(r => r.Workflow == "deploy").ToList(),
            // Every failed fake deploy is a prod rollback (the bad-deploy scenario).
            _runs.Count(r => r.Workflow == "deploy" && r.Conclusion == "failure" && r.StartedAt >= Now.AddDays(-30)));
    });

    // ---- AWS-side fakes ----

    public Task<EnvironmentHealth> CheckAsync(string environment, CancellationToken ct) => Locked(() =>
    {
        var url = environment == "prod" ? "https://demo.joshb.app" : "https://staging.demo.joshb.app";
        var degraded = environment == "prod" && _alarms.Values.Any(a => a.State == "ALARM");
        return new EnvironmentHealth(environment, url, true, degraded ? "Ready, but an alarm is firing" : "Ready", Version, _commit, TimeSpan.FromMilliseconds(degraded ? 380 : 42));
    });

    public Task<IReadOnlyList<AlarmState>> GetAlarmsAsync(CancellationToken ct) =>
        Locked<IReadOnlyList<AlarmState>>(() => _alarms.Values.OrderBy(a => a.Name, StringComparer.Ordinal).ToList());

    public Task<IReadOnlyList<LogLine>> GetRecentErrorsAsync(CancellationToken ct) => Locked<IReadOnlyList<LogLine>>(() =>
    {
        var now = Now;
        var lines = new List<LogLine>
        {
            new(now.AddMinutes(-42), LogJson(now.AddMinutes(-42), "Warning", "Microsoft.EntityFrameworkCore.Database.Command", "Slow query on /api/search (812 ms)", null), "orders-api/prod/7d1c"),
        };
        if (_alarms.TryGetValue(ErrorAlarm, out var alarm) && alarm.State == "ALARM")
        {
            for (var i = 4; i >= 0; i--)
            {
                var at = now.AddSeconds(-i * 7);
                lines.Add(new LogLine(at, LogJson(at, "Error", "Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware", "An unhandled exception has occurred while executing the request.", SearchException), "orders-api/prod/" + (i % 2 == 0 ? "7d1c" : "93be")));
            }
        }

        return lines.OrderByDescending(l => l.At).ToList();
    });

    private const string SearchException =
        "Npgsql.PostgresException (0x80004005): 42601: syntax error in tsquery: \"alan turing:*\"\n" +
        "   at Npgsql.Internal.NpgsqlConnector.ReadMessageLong(Boolean async, DataRowLoadingMode dataRowLoadingMode, Boolean readingNotifications, Boolean isReadingPrependedMessage)\n" +
        "   at Npgsql.NpgsqlDataReader.NextResult(Boolean async, Boolean isConsuming, CancellationToken cancellationToken)\n" +
        "   at Npgsql.NpgsqlCommand.ExecuteReader(Boolean async, CommandBehavior behavior, CancellationToken cancellationToken)\n" +
        "   at Microsoft.EntityFrameworkCore.Storage.RelationalCommand.ExecuteReaderAsync(RelationalCommandParameterObject parameterObject, CancellationToken cancellationToken)\n" +
        "   at Orders.Api.Features.Search.SearchEndpoints.SearchAsync(String q, OrdersDbContext db, CancellationToken ct)\n" +
        "  Exception data:\n    Severity: ERROR\n    SqlState: 42601\n    MessageText: syntax error in tsquery: \"alan turing:*\"";

    /// <summary>A log line in the API's JSON format (Orders.Api Infrastructure/Logging.cs).</summary>
    private string LogJson(DateTimeOffset at, string level, string category, string message, string? exception) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["Timestamp"] = at.ToString("O", CultureInfo.InvariantCulture),
            ["LogLevel"] = level,
            ["Category"] = category,
            ["EventId"] = exception is null ? 0 : 1,
            ["Message"] = message,
            ["Exception"] = exception,
            ["AppVersion"] = Version,
            ["AppCommit"] = _commit,
            ["AppEnvironment"] = "prod",
            ["TraceId"] = "4bf92f3577b34da6a3ce929d0e0e4736",
            ["Scopes"] = new object[] { new Dictionary<string, string> { ["RequestId"] = "0HN8V1K2Q3R4S:00000001", ["RequestPath"] = "/api/search" } },
        });

    public Task<IReadOnlyList<DeployRecord>> GetRecentDeploysAsync(CancellationToken ct) =>
        Locked<IReadOnlyList<DeployRecord>>(() => _deploys.OrderByDescending(d => d.At).Take(8).ToList());

    public Task<CostSummary> GetCostAsync(CancellationToken ct) => Locked(() =>
    {
        var now = Now;
        var today = 1.83m;
        return new CostSummary(Math.Round((2.07m * (now.Day - 1)) + today, 2), today, "USD", now);
    });

    public Task<IReadOnlyList<FeatureFlag>> GetFlagsAsync(CancellationToken ct) => Locked<IReadOnlyList<FeatureFlag>>(() =>
        _flags.Select(f => new FeatureFlag(f.Key, f.Value.Enabled, f.Value.Description)).OrderBy(f => f.Name, StringComparer.Ordinal).ToList());

    public Task SetFlagAsync(string name, bool enabled, CancellationToken ct) => Locked(() =>
    {
        if (!_flags.TryGetValue(name, out var flag))
        {
            throw new InvalidOperationException($"No feature flag called {name}.");
        }

        _flags[name] = (enabled, flag.Description);
        if (name == FaultFlag && enabled && !flag.Enabled)
        {
            _faultFixed = false;
            After(1, () => FireAlarm(ErrorAlarm, "5xx rate 14% over 1 minute (threshold 2%)"));
            After(2, () => AddIssue(
                "Incident: GET /api/search returning 500s for multi-word terms",
                "Alarm orders-demo-api-prod-5xx fired. Multi-word searches fail with a tsquery syntax error in SearchV2 since the searchV2 flag was turned on. No deploy in the last hour.",
                ["incident"],
                "github-actions[bot]"));
        }
        else if (name == FaultFlag && !enabled)
        {
            After(1, ClearAlarms);
        }

        return true;
    });

    public Task<LoadStatus> GetStatusAsync(CancellationToken ct) => Locked(() =>
    {
        if (_load.Running && _load.EndsAt <= Now)
        {
            _load = new LoadStatus(false, null, null, null, null);
        }

        return _load;
    });

    public Task StartAsync(string scenario, int durationMinutes, CancellationToken ct) => Locked(() =>
    {
        _load = new LoadStatus(true, scenario, Now, Now.AddMinutes(durationMinutes), "fake-" + Guid.NewGuid().ToString("N")[..8]);
        return true;
    });

    public Task StopAsync(CancellationToken ct) => Locked(() =>
    {
        _load = new LoadStatus(false, null, null, null, null);
        return true;
    });

    // ---- simulation ----

    private FakeIssue AddIssue(string title, string body, IReadOnlyList<string> labels, string author)
    {
        var issue = new FakeIssue(_next++, title, body, author, Now);
        _issues.Add(issue);
        Emit(EventKinds.IssueOpened, ConsoleEvent.IssueSubject(issue.Number), $"{author}: {title}", IssueUrl(issue.Number));
        foreach (var label in labels)
        {
            Label(issue, label);
        }

        After(1, () => Triage(issue));
        return issue;
    }

    private void Triage(FakeIssue issue)
    {
        if (!issue.Open)
        {
            return;
        }

        var text = (issue.Title + " " + issue.Body).ToLowerInvariant();
        if (text.Contains(".github", StringComparison.Ordinal) || text.Contains("migration", StringComparison.Ordinal))
        {
            Label(issue, "risk:high");
            Label(issue, "difficulty:medium");
            Label(issue, "needs-human");
            if (text.Contains("migration", StringComparison.Ordinal))
            {
                // Josh assigns the agent anyway; its PR then waits for his review (CODEOWNERS on migrations).
                After(2, () =>
                {
                    var pull = AddPull("feat: index orders.customer_id", $"Closes #{issue.Number}\n\nAdds an EF Core migration. Needs review: touches migrations.", $"agent/{issue.Number}-customer-index", _agent, issue.Number);
                    pull.ReviewRequired = true;
                });
            }

            return;
        }

        Label(issue, "risk:low");
        Label(issue, "difficulty:medium");
        Label(issue, "agent");
        Emit(EventKinds.IssueAssigned, ConsoleEvent.IssueSubject(issue.Number), _agent, IssueUrl(issue.Number));
        After(1, () =>
        {
            if (!issue.Open)
            {
                return;
            }

            var title = (issue.Labels.Contains("incident") ? "fix: " : "feat: ") + LowerFirst(issue.Title.Replace("Incident: ", "", StringComparison.Ordinal));
            var pull = AddPull(title, $"Closes #{issue.Number}", $"agent/{issue.Number}-{Slug(issue.Title)}", _agent, issue.Number);
            pull.FixesIncident = issue.Labels.Contains("incident");
            After(1, () => Checks(pull));
        });
    }

    private void Checks(FakePull pull)
    {
        if (pull.State != "open")
        {
            return;
        }

        var subject = ConsoleEvent.PullSubject(pull.Number);
        Emit(EventKinds.PreviewUp, subject, $"pr-{pull.Number}.demo.joshb.app", $"https://pr-{pull.Number}.demo.joshb.app");
        AddRun("ci", "success", pull.Title, pull.Head, TimeSpan.FromSeconds(372));
        AddRun("ai-review", "success", pull.Title, pull.Head, TimeSpan.FromSeconds(95));
        Emit(EventKinds.CheckCompleted, subject, "ci-ok: success", RunUrl(_runId - 1));
        Emit(EventKinds.CheckCompleted, subject, "ai-review: success", RunUrl(_runId));
        After(1, () => Merge(pull));
    }

    private void Merge(FakePull pull)
    {
        if (pull.State != "open")
        {
            return;
        }

        pull.State = "merged";
        pull.MergedAt = Now;
        if (pull.Issue is { } n && _issues.FirstOrDefault(i => i.Number == n) is { } issue)
        {
            issue.Open = false;
        }

        AddRun("ci", "success", pull.Title, "gh-readonly-queue/main", TimeSpan.FromSeconds(401));
        Emit(EventKinds.PrMerged, ConsoleEvent.PullSubject(pull.Number), "merged by the merge queue", PullUrl(pull.Number));
        Emit(EventKinds.PreviewDown, ConsoleEvent.PullSubject(pull.Number), $"pr-{pull.Number}.demo.joshb.app removed", PullUrl(pull.Number));
        After(1, () => DeployStart(pull));
    }

    private void DeployStart(FakePull pull)
    {
        var sha = Sha(pull.Number);
        _deploys.Add(new DeployRecord("staging", $"1.4.{_patch + 1}", "succeeded", Now, null, sha));
        Emit(EventKinds.DeployStarted, "prod", $"{sha}: {pull.Title} (#{pull.Number})", RunUrl(++_runId));
        if (pull.Bad)
        {
            After(1, () => FireAlarm(ErrorAlarm, "5xx rate 22% over 1 minute (threshold 2%)"));
            After(2, () =>
            {
                AddRun("deploy", "failure", pull.Title, "main", TimeSpan.FromSeconds(540));
                _deploys.Add(new DeployRecord("prod", $"1.4.{_patch + 1}", "rolled back", Now, null, sha));
                Emit(EventKinds.DeployRolledBack, "prod", $"{sha} rolled back to {Version} ({_commit})", RunUrl(_runId));
            });
            After(3, ClearAlarms);
            return;
        }

        After(1, () =>
        {
            _patch++;
            _commit = sha;
            AddRun("deploy", "success", pull.Title, "main", TimeSpan.FromSeconds(486));
            _deploys.Add(new DeployRecord("prod", Version, "succeeded", Now, null, sha));
            Emit(EventKinds.DeploySucceeded, "prod", $"{sha}: {pull.Title} (#{pull.Number}) is live as {Version}", RunUrl(_runId));
            if (pull.FixesIncident)
            {
                _faultFixed = true;
                After(1, ClearAlarms);
            }
        });
    }

    private void FireAlarm(string name, string reason)
    {
        if (_flags.GetValueOrDefault(FaultFlag).Enabled && _faultFixed && name == ErrorAlarm)
        {
            return;
        }

        _alarms[name] = _alarms[name] with { State = "ALARM", Reason = reason, UpdatedAt = Now };
        Emit(EventKinds.AlarmAlarm, name, reason, _alarms[name].Url);
    }

    private void ClearAlarms()
    {
        if (_flags.GetValueOrDefault(FaultFlag).Enabled && !_faultFixed)
        {
            return;
        }

        foreach (var name in _alarms.Keys.ToList())
        {
            if (_alarms[name].State != "OK")
            {
                _alarms[name] = _alarms[name] with { State = "OK", Reason = "Back under threshold", UpdatedAt = Now };
                Emit(EventKinds.AlarmOk, name, "Back under threshold", _alarms[name].Url);
            }
        }
    }

    private FakePull AddPull(string title, string body, string head, string author, int? issue)
    {
        var pull = new FakePull(_next++, title, body, head, author, Now) { Issue = issue };
        _pulls.Add(pull);
        var refs = issue is null ? "" : $" (closes #{issue})";
        Emit(EventKinds.PrOpened, ConsoleEvent.PullSubject(pull.Number), $"{author}: {title}{refs}", PullUrl(pull.Number));
        return pull;
    }

    private void Label(FakeIssue issue, string label)
    {
        if (!issue.Labels.Contains(label))
        {
            issue.Labels.Add(label);
            Emit(EventKinds.IssueLabeled, ConsoleEvent.IssueSubject(issue.Number), label, IssueUrl(issue.Number));
        }
    }

    private void AddRun(string workflow, string conclusion, string title, string branch, TimeSpan duration)
    {
        var id = ++_runId;
        _runs.Add(new WorkflowRunSummary(workflow, "completed", conclusion, title, branch, RunUrl(id), Now - duration, Now));
    }

    private void Emit(string kind, string subject, string? detail, string? url) =>
        _events.Add(new ConsoleEvent(Now, kind, subject, detail, url));

    private void After(int steps, Action act) => _schedule.Add((Now + (_step * steps), ++_seq, act));

    private void Pump()
    {
        while (true)
        {
            var now = Now;
            var due = _schedule.Where(s => s.Due <= now).OrderBy(s => s.Due).ThenBy(s => s.Seq).FirstOrDefault();
            if (due.Act is null)
            {
                return;
            }

            _schedule.Remove(due);
            due.Act();
        }
    }

    private Task<T> Locked<T>(Func<T> f)
    {
        lock (_gate)
        {
            Pump();
            return Task.FromResult(f());
        }
    }

    private IssueSummary IssueSummary(FakeIssue i) => new(i.Number, i.Title, i.Labels.ToList(), i.Author, IssueUrl(i.Number), i.CreatedAt, i.Open ? "open" : "closed");

    private PullRequestSummary Summary(FakePull p) => new(p.Number, p.Title, p.Author, p.State, PullUrl(p.Number), p.CreatedAt, p.MergedAt);

    private string IssueUrl(int n) => $"{_repoUrl}/issues/{n}";

    private string PullUrl(int n) => $"{_repoUrl}/pull/{n}";

    private string RunUrl(long id) => $"{_repoUrl}/actions/runs/{id}";

    private static string Sha(int seed) => ((uint)(seed * 2654435761u)).ToString("x8", CultureInfo.InvariantCulture)[..7];

    private static string LowerFirst(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

    private static string Slug(string s) => string.Join('-', s.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(4)
        .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray())).Where(w => w.Length > 0));

    private void Seed()
    {
        var now = Now;
        foreach (var (name, description) in KnownFlags.Descriptions)
        {
            _flags[name] = (false, description);
        }

        _flags["lowStockBanner"] = (true, "Show the low-stock banner on products");

        foreach (var (name, reason) in new[]
        {
            (ErrorAlarm, "5xx rate under 2%"),
            ("orders-prod-p95-latency", "p95 latency under 500 ms"),
            ("orders-prod-unhealthy-hosts", "All targets healthy"),
            ("orders-staging-5xx-rate", "5xx rate under 2%"),
        })
        {
            _alarms[name] = new AlarmState(name, "OK", reason, now.AddHours(-5), $"https://us-east-1.console.aws.amazon.com/cloudwatch/home?region=us-east-1#alarmsV2:alarm/{name}");
        }

        void Old(int minutesAgo, string kind, string subject, string detail, string? url) =>
            _events.Add(new ConsoleEvent(now.AddMinutes(-minutesAgo), kind, subject, detail, url));

        // History: a few issues and PRs, mostly done by agents.
        var history = new (int Issue, string Title, string[] Labels, bool Open, int Pull, string PrTitle, int OpenedAgo, int? MergedAgo, bool Changes)[]
        {
            (118, "Return 404 problem details for unknown products", ["agent", "risk:low", "difficulty:low"], false, 119, "fix: problem details for unknown products", 2900, 2860, false),
            (121, "Page size above 100 should be capped", ["agent", "risk:low", "difficulty:low"], false, 122, "fix: cap page size at 100", 2100, 2040, false),
            (123, "Search orders by customer email", ["agent", "risk:low", "difficulty:medium"], false, 124, "feat: search orders by customer email", 1500, 1390, true),
            (126, "Cancelled orders should restock lines", ["agent", "risk:medium", "difficulty:medium"], false, 127, "fix: restock lines on cancel", 700, 610, false),
            (128, "Mutation: rounding in OrderTotals is not tested", ["mutation", "agent", "risk:low"], true, 129, "test: cover rounding in OrderTotals", 180, null, false),
            (131, "Mutation: restock on cancel has no assertion", ["mutation", "agent", "risk:low"], true, 0, "", 0, null, false),
            (133, "Index orders by created date", ["needs-human", "risk:high", "difficulty:medium"], true, 0, "", 0, null, false),
            (135, "Show order count per customer", ["agent", "risk:low", "difficulty:low"], true, 136, "feat: order count per customer", 25, null, false),
        };

        foreach (var h in history)
        {
            var issue = new FakeIssue(h.Issue, h.Title, h.Issue == 128 || h.Issue == 131 ? "Filed by the nightly mutation run." : "", h.Issue == 128 || h.Issue == 131 ? "github-actions[bot]" : "jbilligmeier", now.AddMinutes(-(h.OpenedAgo + 20)))
            {
                Open = h.Open,
            };
            issue.Labels.AddRange(h.Labels);
            _issues.Add(issue);
            Old(h.OpenedAgo + 20, EventKinds.IssueOpened, ConsoleEvent.IssueSubject(h.Issue), $"{issue.Author}: {h.Title}", IssueUrl(h.Issue));
            if (h.Pull == 0)
            {
                continue;
            }

            var pull = new FakePull(h.Pull, h.PrTitle, $"Closes #{h.Issue}", $"agent/{h.Issue}-{Slug(h.Title)}", _agent, now.AddMinutes(-h.OpenedAgo))
            {
                Issue = h.Issue,
                ChangesRequested = h.Changes,
                State = h.MergedAgo is null ? "open" : "merged",
                MergedAt = h.MergedAgo is { } m ? now.AddMinutes(-m) : null,
            };
            _pulls.Add(pull);
            Old(h.OpenedAgo, EventKinds.PrOpened, ConsoleEvent.PullSubject(h.Pull), $"{_agent}: {h.PrTitle} (closes #{h.Issue})", PullUrl(h.Pull));
            if (h.MergedAgo is { } merged)
            {
                Old(merged, EventKinds.PrMerged, ConsoleEvent.PullSubject(h.Pull), "merged by the merge queue", PullUrl(h.Pull));
                Old(merged - 9, EventKinds.DeploySucceeded, "prod", $"{Sha(h.Pull)}: deployed", RunUrl(_runId));
            }
        }

        _pulls.Add(new FakePull(134, "feat: add index on orders.created_at", "Closes #133\n\nAdds an EF Core migration.", "agent/133-created-at-index", _agent, now.AddMinutes(-300)) { Issue = 133, ReviewRequired = true });
        _pulls.Add(new FakePull(137, "chore: bump Npgsql to 10.0.2", "Dependabot", "dependabot/nuget/Npgsql-10.0.2", "dependabot[bot]", now.AddMinutes(-90)) { ReviewRequired = false });

        var ciMinutes = new[] { 5.2, 6.1, 4.8, 7.4, 5.9, 6.6, 9.8, 5.5, 6.0, 5.1, 8.2, 6.3 };
        for (var i = 0; i < ciMinutes.Length; i++)
        {
            var end = now.AddMinutes(-(i * 140) - 30);
            _runs.Add(new WorkflowRunSummary("ci", "completed", i == 6 ? "failure" : "success", "CI", "main", RunUrl(++_runId), end.AddMinutes(-ciMinutes[i]), end));
        }

        _runs.Add(new WorkflowRunSummary("ai-review", "completed", "success", "AI review", "agent/135-show-order-count", RunUrl(++_runId), now.AddMinutes(-22), now.AddMinutes(-20)));
        _runs.Add(new WorkflowRunSummary("deploy", "completed", "failure", "Deploy", "main", RunUrl(++_runId), now.AddDays(-3).AddMinutes(-9), now.AddDays(-3)));
        _runs.Add(new WorkflowRunSummary("deploy", "completed", "success", "Deploy", "main", RunUrl(++_runId), now.AddMinutes(-610), now.AddMinutes(-601)));

        _deploys.Add(new DeployRecord("prod", "1.3.9", "rolled back", now.AddDays(-3), null, "5be01d2"));
        _deploys.Add(new DeployRecord("staging", Version, "succeeded", now.AddMinutes(-608), null, _commit));
        _deploys.Add(new DeployRecord("prod", Version, "succeeded", now.AddMinutes(-601), null, _commit));
        Old(4320, EventKinds.DeployRolledBack, "prod", "5be01d2 rolled back to 1.3.8", null);
    }

    private sealed class FakeIssue(int number, string title, string body, string author, DateTimeOffset createdAt)
    {
        public int Number { get; } = number;

        public string Title { get; } = title;

        public string Body { get; } = body;

        public string Author { get; } = author;

        public DateTimeOffset CreatedAt { get; } = createdAt;

        public List<string> Labels { get; } = [];

        public bool Open { get; set; } = true;
    }

    private sealed class FakePull(int number, string title, string body, string head, string author, DateTimeOffset createdAt)
    {
        public int Number { get; } = number;

        public string Title { get; } = title;

        public string Body { get; } = body;

        public string Head { get; } = head;

        public string Author { get; } = author;

        public DateTimeOffset CreatedAt { get; } = createdAt;

        public int? Issue { get; init; }

        public string State { get; set; } = "open";

        public DateTimeOffset? MergedAt { get; set; }

        public bool Bad { get; set; }

        public bool FixesIncident { get; set; }

        public bool ReviewRequired { get; set; }

        public bool ChangesRequested { get; init; }
    }
}
