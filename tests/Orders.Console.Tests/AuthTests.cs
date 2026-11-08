using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Orders.Console.Auth;

namespace Orders.Console.Tests;

public sealed class ConsoleFactory(IReadOnlyDictionary<string, string> settings) : WebApplicationFactory<Program>
{
    public const string Token = "test-token-123";

    public static ConsoleFactory WithTokenAuth() => new(new Dictionary<string, string>
    {
        ["CONSOLE_FAKES"] = "true",
        ["CONSOLE_VERIFICATION_TOKEN"] = Token,
        ["CONSOLE_FAKE_STEP_SECONDS"] = "1",
    });

    public static ConsoleFactory WithAuthOff() => new(new Dictionary<string, string>
    {
        ["CONSOLE_FAKES"] = "true",
        ["CONSOLE_AUTH"] = "off",
        ["CONSOLE_FAKE_STEP_SECONDS"] = "1",
    });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }
    }
}

public sealed class AuthTests : IClassFixture<AuthTests.Fixture>
{
    private readonly ConsoleFactory _factory;

    public AuthTests(Fixture fixture) => _factory = fixture.Factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("secret", "secret", true)]
    [InlineData("secret", "Secret", false)]
    [InlineData("secret", "secret ", false)]
    [InlineData("", "secret", false)]
    [InlineData(null, "secret", false)]
    [InlineData("anything", null, false)]
    [InlineData("", "", false)]
    public void Token_comparison(string? presented, string? expected, bool matches) =>
        Assert.Equal(matches, VerificationTokenHandler.Matches(presented, expected));

    [Fact]
    public async Task Health_live_is_anonymous()
    {
        var response = await _factory.CreateClient().GetAsync("/health/live", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/present")]
    [InlineData("/architecture")]
    [InlineData("/api/run")]
    public async Task Everything_else_needs_auth(string path)
    {
        var response = await _factory.CreateClient().GetAsync(path, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_right_token_is_accepted_as_automation()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(VerificationTokenHandler.Header, ConsoleFactory.Token);

        var whoami = await client.GetStringAsync("/api/whoami", Ct);
        Assert.Contains("\"name\":\"automation\"", whoami, StringComparison.Ordinal);
        Assert.Contains("verification-token", whoami, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/present", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_wrong_token_is_rejected()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(VerificationTokenHandler.Header, "wrong");

        var response = await client.GetAsync("/", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/whoami", Ct)).StatusCode);
    }

    [Fact]
    public async Task Posts_need_the_action_header()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(VerificationTokenHandler.Header, ConsoleFactory.Token);

        var response = await client.PostAsync("/api/load/stop", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/load/stop") { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Console-Action", "1");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request, Ct)).StatusCode);
    }

    [Theory]
    [InlineData("billigmeier.org", "josh@billigmeier.org", "true", true)]
    [InlineData("billigmeier.org", "josh@billigmeier.org", "false", false)]
    [InlineData(null, "josh@gmail.com", "true", false)]
    [InlineData("example.com", "josh@example.com", "true", false)]
    [InlineData("billigmeier.org", "josh@evil.com", "true", false)]
    public void Google_sign_in_is_limited_to_the_hosted_domain(string? hd, string email, string verified, bool allowed)
    {
        var claims = new List<Claim> { new("email", email), new("email_verified", verified) };
        if (hd is not null)
        {
            claims.Add(new Claim("hd", hd));
        }

        Assert.Equal(allowed, AuthSetup.IsAllowedGoogleUser(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")), "billigmeier.org"));
    }

    public sealed class Fixture : IDisposable
    {
        public ConsoleFactory Factory { get; } = ConsoleFactory.WithTokenAuth();

        public void Dispose() => Factory.Dispose();
    }
}

public sealed class AuthOffTests : IClassFixture<AuthOffTests.Fixture>
{
    private readonly ConsoleFactory _factory;

    public AuthOffTests(Fixture fixture) => _factory = fixture.Factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/", "data-panel=\"health\"")]
    [InlineData("/status", "data-panel=\"flags\"")]
    [InlineData("/present", "data-step=\"issue\"")]
    [InlineData("/present?scenario=bad-deploy", "data-step=\"rollback\"")]
    [InlineData("/architecture", "data-testid=\"architecture-diagram\"")]
    [InlineData("/?handler=Live", "data-panel=\"alarms\"")]
    [InlineData("/present?handler=Live&scenario=tour", "data-testid=\"step-action\"")]
    public async Task Pages_render_in_fake_mode_without_auth(string path, string marker)
    {
        var html = await _factory.CreateClient().GetStringAsync(path, Ct);
        Assert.Contains(marker, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_scenario_is_404()
    {
        var response = await _factory.CreateClient().GetAsync("/present?scenario=nope", Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    public sealed class Fixture : IDisposable
    {
        public ConsoleFactory Factory { get; } = ConsoleFactory.WithAuthOff();

        public void Dispose() => Factory.Dispose();
    }
}
