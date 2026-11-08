using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;

namespace Orders.Console.Auth;

/// <summary>
/// Google sign-in (OpenID Connect, billigmeier.org accounts only) or the verification token header.
/// Everything except /health/live needs one of them, unless CONSOLE_AUTH=off.
/// </summary>
public static class AuthSetup
{
    public const string GoogleScheme = "Google";
    private const string AuthenticateScheme = "console";
    private const string ChallengeScheme = "console-challenge";

    public static void AddConsoleAuth(this IServiceCollection services, ConsoleOptions options)
    {
        if (!options.AuthEnabled)
        {
            services.AddAuthentication();
            services.AddAuthorization();
            return;
        }

        var google = options.GoogleClientId is not null && options.GoogleClientSecret is not null;
        if (!google && options.VerificationToken is null)
        {
            throw new InvalidOperationException(
                "Auth is on but neither GOOGLE_CLIENT_ID/GOOGLE_CLIENT_SECRET nor CONSOLE_VERIFICATION_TOKEN is set. Set one, or CONSOLE_AUTH=off with CONSOLE_FAKES=true.");
        }

        static bool HasToken(HttpContext c) => c.Request.Headers.ContainsKey(VerificationTokenHandler.Header);

        var auth = services.AddAuthentication(o =>
            {
                o.DefaultScheme = AuthenticateScheme;
                o.DefaultChallengeScheme = ChallengeScheme;
                o.DefaultForbidScheme = AuthenticateScheme;
                o.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            })
            .AddPolicyScheme(AuthenticateScheme, "Cookie or verification token", o =>
                o.ForwardDefaultSelector = c => HasToken(c) ? VerificationTokenHandler.SchemeName : CookieAuthenticationDefaults.AuthenticationScheme)
            .AddPolicyScheme(ChallengeScheme, "Google sign-in or 401", o =>
                o.ForwardDefaultSelector = c => HasToken(c) || !google || WantsJson(c) ? VerificationTokenHandler.SchemeName : GoogleScheme)
            .AddScheme<VerificationTokenOptions, VerificationTokenHandler>(VerificationTokenHandler.SchemeName, o => o.Token = options.VerificationToken)
            .AddCookie(o =>
            {
                o.Cookie.Name = "orders-console";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.ExpireTimeSpan = TimeSpan.FromHours(12);
                o.SlidingExpiration = true;
            });

        if (google)
        {
            auth.AddOpenIdConnect(GoogleScheme, "Google", o =>
            {
                o.Authority = "https://accounts.google.com";
                o.ClientId = options.GoogleClientId;
                o.ClientSecret = options.GoogleClientSecret;
                o.CallbackPath = "/signin-oidc";
                o.ResponseType = "code";
                o.UsePkce = true;
                o.Scope.Clear();
                o.Scope.Add("openid");
                o.Scope.Add("email");
                o.Scope.Add("profile");
                o.MapInboundClaims = false;
                o.SaveTokens = false;
                o.TokenValidationParameters.NameClaimType = "email";
                o.Events.OnRedirectToIdentityProvider = c =>
                {
                    // Pre-selects billigmeier.org accounts; the real check is in OnTokenValidated.
                    c.ProtocolMessage.SetParameter("hd", options.GoogleHostedDomain);
                    return Task.CompletedTask;
                };
                o.Events.OnTokenValidated = c =>
                {
                    if (!IsAllowedGoogleUser(c.Principal, options.GoogleHostedDomain))
                    {
                        c.Fail($"Only {options.GoogleHostedDomain} accounts can use the console.");
                    }

                    return Task.CompletedTask;
                };
                o.Events.OnRemoteFailure = c =>
                {
                    c.Response.StatusCode = StatusCodes.Status403Forbidden;
                    c.Response.ContentType = "text/plain";
                    c.HandleResponse();
                    return c.Response.WriteAsync($"Sign-in failed: {c.Failure?.Message}");
                };
            });
        }

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
    }

    /// <summary>The ID token must say the account belongs to the hosted domain and the email is verified.</summary>
    public static bool IsAllowedGoogleUser(ClaimsPrincipal? user, string hostedDomain)
    {
        var hd = user?.FindFirst("hd")?.Value;
        var email = user?.FindFirst("email")?.Value;
        var verified = user?.FindFirst("email_verified")?.Value;
        return string.Equals(hd, hostedDomain, StringComparison.OrdinalIgnoreCase)
            && email is not null
            && email.EndsWith("@" + hostedDomain, StringComparison.OrdinalIgnoreCase)
            && string.Equals(verified, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Fetches from the page's own script and API calls get a 401, not a redirect to Google.</summary>
    private static bool WantsJson(HttpContext c) =>
        c.Request.Path.StartsWithSegments("/api") || c.Request.Headers.ContainsKey("X-Console-Fetch");

    /// <summary>Puts the signed-in identity on every log line of the request.</summary>
    public static IApplicationBuilder UseIdentityLogScope(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Orders.Console.Requests");
        var name = context.User.Identity?.IsAuthenticated == true ? context.User.Identity.Name ?? "unknown" : "anonymous";
        using (logger.BeginScope(new Dictionary<string, object> { ["Identity"] = name }))
        {
            await next();
        }
    });

    public static Task SignOutAsync(HttpContext context) => context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
}
