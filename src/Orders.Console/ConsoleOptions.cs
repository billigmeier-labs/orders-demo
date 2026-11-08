namespace Orders.Console;

/// <summary>
/// Every setting the console reads, by its environment variable name. See README.md.
/// </summary>
public sealed class ConsoleOptions
{
    public bool Fakes { get; init; }
    public bool AuthEnabled { get; init; } = true;
    public string? VerificationToken { get; init; }
    public string? GoogleClientId { get; init; }
    public string? GoogleClientSecret { get; init; }
    public string GoogleHostedDomain { get; init; } = "billigmeier.org";
    public string? DataProtectionKeysPath { get; init; }

    public string? GitHubToken { get; init; }
    public string GitHubRepository { get; init; } = "billigmeier-labs/orders-demo";
    public string GitHubApiUrl { get; init; } = "https://api.github.com";
    public string AgentLogin { get; init; } = "claude[bot]";
    public IReadOnlyList<string> Workflows { get; init; } = ["ci.yml", "ai-review.yml", "deploy.yml"];
    public string DeployWorkflow { get; init; } = "deploy.yml";

    public string ProdUrl { get; init; } = "https://demo.joshb.app";
    public string StagingUrl { get; init; } = "https://staging.demo.joshb.app";

    public string AwsRegion { get; init; } = "us-east-1";
    public string EcsCluster { get; init; } = "demo";
    public string EcsProdService { get; init; } = "prod";
    public string EcsStagingService { get; init; } = "staging";
    public string AlarmPrefix { get; init; } = "orders-";
    public string ProdLogGroup { get; init; } = "/ecs/orders-api/prod";
    public string ErrorLogFilter { get; init; } = "?ERROR ?Error ?Exception";
    public string CostProjectTag { get; init; } = "orders-demo";

    public string AppConfigApplication { get; init; } = "orders-demo";
    public string AppConfigEnvironment { get; init; } = "prod";
    public string AppConfigProfile { get; init; } = "feature-flags";
    public string AppConfigDeploymentStrategy { get; init; } = "AppConfig.AllAtOnce";

    public string LoadgenTaskDefinition { get; init; } = "loadgen";
    public string LoadgenContainer { get; init; } = "loadgen";
    public IReadOnlyList<string> LoadgenSubnets { get; init; } = [];
    public IReadOnlyList<string> LoadgenSecurityGroups { get; init; } = [];
    public bool LoadgenAssignPublicIp { get; init; }

    public TimeSpan EngineTick { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan GitHubPollInterval { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan AwsPollInterval { get; init; } = TimeSpan.FromSeconds(15);
    public int StatusRefreshSeconds { get; init; } = 5;
    public TimeSpan FakeStep { get; init; } = TimeSpan.FromSeconds(3);

    public static ConsoleOptions FromConfiguration(IConfiguration c)
    {
        var d = new ConsoleOptions();
        string Str(string key, string fallback) => string.IsNullOrWhiteSpace(c[key]) ? fallback : c[key]!.Trim();
        string? Opt(string key) => string.IsNullOrWhiteSpace(c[key]) ? null : c[key]!.Trim();
        IReadOnlyList<string> List(string key, IReadOnlyList<string> fallback) =>
            string.IsNullOrWhiteSpace(c[key]) ? fallback : c[key]!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool Flag(string key, bool fallback) => c[key]?.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => fallback,
        };
        double Num(string key, double fallback) => double.TryParse(c[key], System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;

        var fakes = Flag("CONSOLE_FAKES", false);
        return new ConsoleOptions
        {
            Fakes = fakes,
            AuthEnabled = Flag("CONSOLE_AUTH", true),
            VerificationToken = Opt("CONSOLE_VERIFICATION_TOKEN"),
            GoogleClientId = Opt("GOOGLE_CLIENT_ID"),
            GoogleClientSecret = Opt("GOOGLE_CLIENT_SECRET"),
            GoogleHostedDomain = Str("GOOGLE_HOSTED_DOMAIN", d.GoogleHostedDomain),
            DataProtectionKeysPath = Opt("DATAPROTECTION_KEYS_PATH"),
            GitHubToken = Opt("GITHUB_TOKEN"),
            GitHubRepository = Str("GITHUB_REPOSITORY", d.GitHubRepository),
            GitHubApiUrl = Str("GITHUB_API_URL", d.GitHubApiUrl),
            AgentLogin = Str("AGENT_LOGIN", d.AgentLogin),
            Workflows = List("GITHUB_WORKFLOWS", d.Workflows),
            DeployWorkflow = Str("GITHUB_DEPLOY_WORKFLOW", d.DeployWorkflow),
            ProdUrl = Str("PROD_URL", d.ProdUrl).TrimEnd('/'),
            StagingUrl = Str("STAGING_URL", d.StagingUrl).TrimEnd('/'),
            AwsRegion = Str("AWS_REGION", d.AwsRegion),
            EcsCluster = Str("ECS_CLUSTER", d.EcsCluster),
            EcsProdService = Str("ECS_PROD_SERVICE", d.EcsProdService),
            EcsStagingService = Str("ECS_STAGING_SERVICE", d.EcsStagingService),
            AlarmPrefix = Str("ALARM_PREFIX", d.AlarmPrefix),
            ProdLogGroup = Str("PROD_LOG_GROUP", d.ProdLogGroup),
            ErrorLogFilter = Str("ERROR_LOG_FILTER", d.ErrorLogFilter),
            CostProjectTag = Str("COST_PROJECT_TAG", d.CostProjectTag),
            AppConfigApplication = Str("APPCONFIG_APPLICATION", d.AppConfigApplication),
            AppConfigEnvironment = Str("APPCONFIG_ENVIRONMENT", d.AppConfigEnvironment),
            AppConfigProfile = Str("APPCONFIG_PROFILE", d.AppConfigProfile),
            AppConfigDeploymentStrategy = Str("APPCONFIG_DEPLOYMENT_STRATEGY", d.AppConfigDeploymentStrategy),
            LoadgenTaskDefinition = Str("LOADGEN_TASK_DEFINITION", d.LoadgenTaskDefinition),
            LoadgenContainer = Str("LOADGEN_CONTAINER", d.LoadgenContainer),
            LoadgenSubnets = List("LOADGEN_SUBNETS", []),
            LoadgenSecurityGroups = List("LOADGEN_SECURITY_GROUPS", []),
            LoadgenAssignPublicIp = Flag("LOADGEN_ASSIGN_PUBLIC_IP", false),
            EngineTick = TimeSpan.FromSeconds(Num("ENGINE_TICK_SECONDS", fakes ? 1 : 2)),
            GitHubPollInterval = TimeSpan.FromSeconds(Num("GITHUB_POLL_SECONDS", fakes ? 1 : 10)),
            AwsPollInterval = TimeSpan.FromSeconds(Num("AWS_POLL_SECONDS", fakes ? 1 : 15)),
            StatusRefreshSeconds = (int)Num("STATUS_REFRESH_SECONDS", 5),
            FakeStep = TimeSpan.FromSeconds(Num("CONSOLE_FAKE_STEP_SECONDS", 3)),
        };
    }

    public string RepoUrl => $"https://github.com/{GitHubRepository}";
}
