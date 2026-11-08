using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Orders.Console.Auth;

public sealed class VerificationTokenOptions : AuthenticationSchemeOptions
{
    public string? Token { get; set; }
}

/// <summary>
/// Lets automation (the Playwright suite, smoke checks) in with header X-Verification-Token equal to
/// CONSOLE_VERIFICATION_TOKEN. The identity is "automation".
/// </summary>
public sealed partial class VerificationTokenHandler(
    IOptionsMonitor<VerificationTokenOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder)
    : AuthenticationHandler<VerificationTokenOptions>(options, loggerFactory, encoder)
{
    public const string SchemeName = "VerificationToken";
    public const string Header = "X-Verification-Token";
    public const string Identity = "automation";

    private readonly ILogger _log = loggerFactory.CreateLogger<VerificationTokenHandler>();

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(Header, out var values))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!Matches(values.ToString(), Options.Token))
        {
            LogRejected(Request.Path);
            return Task.FromResult(AuthenticateResult.Fail("Invalid verification token."));
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, Identity), new Claim(ClaimTypes.NameIdentifier, Identity), new Claim("auth_method", "verification-token")],
            SchemeName);
        LogAccepted(Identity, Request.Method, Request.Path);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = SchemeName;
        return Task.CompletedTask;
    }

    /// <summary>Constant-time comparison; an unset expected token never matches.</summary>
    public static bool Matches(string? presented, string? expected)
    {
        if (string.IsNullOrEmpty(expected) || presented is null)
        {
            return false;
        }

        // Hash both so the comparison is fixed-length and does not leak the token length.
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Authenticated {Identity} with the verification token: {Method} {Path}")]
    private partial void LogAccepted(string identity, string method, PathString path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected a wrong verification token for {Path}")]
    private partial void LogRejected(PathString path);
}
