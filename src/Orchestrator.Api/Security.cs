using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Orchestrator.Application;
using Orchestrator.Application.Abstractions;
using Orchestrator.Infrastructure.Persistence;

namespace Orchestrator.Api;

public sealed class AuthOptions
{
    /// <summary>When false the API runs open (actor from X-Operator-Id), as in spec 007.</summary>
    public bool Enabled { get; set; }
    /// <summary>OIDC authority (Keycloak realm URL) used to discover signing keys.</summary>
    public string? Authority { get; set; }
    public string? ValidIssuer { get; set; }
    public string? Audience { get; set; }
    public bool RequireHttpsMetadata { get; set; }
    /// <summary>Symmetric key for locally issued tokens (tests and offline development only).</summary>
    public string? SigningKey { get; set; }
}

public static class Roles
{
    public const string Viewer = "viewer", Operator = "operator", Client = "client", Admin = "admin";
    public static readonly string[] All = [Viewer, Operator, Client, Admin];
}

/// <summary>Role matrix of the API (PRD section 37): which roles may call which route.</summary>
public static class Rbac
{
    private static readonly string[] Everyone = Roles.All;
    private static readonly string[] Operate = [Roles.Operator, Roles.Admin, Roles.Client];
    private static readonly string[] Create = [Roles.Client, Roles.Admin];

    public static string[] Required(string method, string path)
    {
        path = path.TrimEnd('/');
        var read = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
        // Provider metadata is audited and therefore an operator action, even though it is a GET.
        if (path.StartsWith("/v1/dev/", StringComparison.OrdinalIgnoreCase)) return [Roles.Admin];
        if (HttpMethods.IsPost(method) && path.Equals("/v1/document-uploads", StringComparison.OrdinalIgnoreCase)) return Create;
        if (read) return path.EndsWith("/provider", StringComparison.OrdinalIgnoreCase) ? Operate : Everyone;
        if (HttpMethods.IsPost(method) && path.Equals("/v1/signature-processes", StringComparison.OrdinalIgnoreCase)) return Create;
        if (path.StartsWith("/v1/callbacks", StringComparison.OrdinalIgnoreCase)) return Create;
        if (path.StartsWith("/v1/proofing-sessions", StringComparison.OrdinalIgnoreCase))
        {
            var manual = path.EndsWith("/retry", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/evidence", StringComparison.OrdinalIgnoreCase);
            return manual ? Operate : Create;
        }
        return Operate;
    }
}

public static class SecurityExtensions
{
    public static void AddOrchestratorSecurity(this IServiceCollection services, IConfiguration cfg)
    {
        var auth = cfg.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();
        services.Configure<AuthOptions>(cfg.GetSection("Auth"));
        services.AddScoped<CallerContext>();

        var perMinute = cfg.GetValue("RateLimit:CreatePerMinute", 600);
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy("create", ctx =>
            {
                var key = ctx.User.FindFirstValue("azp") ?? ctx.User.FindFirstValue("preferred_username") ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
                });
            });
        });

        if (!auth.Enabled) return;
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
        {
            o.MapInboundClaims = false;
            o.RequireHttpsMetadata = auth.RequireHttpsMetadata;
            var p = new TokenValidationParameters
            {
                ValidateIssuer = !string.IsNullOrEmpty(auth.ValidIssuer) || !string.IsNullOrEmpty(auth.Authority),
                ValidIssuer = auth.ValidIssuer ?? auth.Authority,
                ValidateAudience = !string.IsNullOrEmpty(auth.Audience),
                ValidAudience = auth.Audience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = "preferred_username",
                RoleClaimType = ClaimTypes.Role
            };
            if (!string.IsNullOrEmpty(auth.SigningKey))
            {
                p.ValidateIssuerSigningKey = true;
                p.IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(auth.SigningKey));
            }
            else o.Authority = auth.Authority;
            o.TokenValidationParameters = p;
            o.Events = new JwtBearerEvents
            {
                OnTokenValidated = ctx =>
                {
                    MapRealmRoles(ctx.Principal);
                    return Task.CompletedTask;
                }
            };
        });
    }

    /// <summary>Copies Keycloak realm_access.roles into role claims.</summary>
    public static void MapRealmRoles(ClaimsPrincipal? principal)
    {
        if (principal?.Identity is not ClaimsIdentity id) return;
        var raw = principal.FindFirst("realm_access")?.Value;
        if (string.IsNullOrEmpty(raw)) return;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array)
                foreach (var r in roles.EnumerateArray())
                    if (r.GetString() is { Length: > 0 } name && Roles.All.Contains(name)) id.AddClaim(new Claim(ClaimTypes.Role, name));
        }
        catch (JsonException) { /* malformed claim: no roles */ }
    }
}

/// <summary>
/// Enforces authentication, the role matrix and client segregation; fills CallerContext and the audit actor
/// from the validated token. Only registered when Auth:Enabled is true.
/// </summary>
public sealed partial class AccessMiddleware(RequestDelegate next, ILogger<AccessMiddleware> log)
{
    [System.Text.RegularExpressions.GeneratedRegex(@"^/v1/(signature-processes|proofing-sessions|callbacks)/([^/]+)")]
    private static partial System.Text.RegularExpressions.Regex OwnedRoute();

    private static bool Public(PathString path) =>
        path.StartsWithSegments("/health") || path.StartsWithSegments("/swagger") || path.StartsWithSegments("/v1/downloads")
        || path.StartsWithSegments("/v1/webhooks"); // authenticated by the provider signature, not by a token

    public async Task InvokeAsync(HttpContext ctx, CallerContext caller, ActorContext actor, OrchestratorDbContext db)
    {
        if (Public(ctx.Request.Path) || !ctx.Request.Path.StartsWithSegments("/v1")) { await next(ctx); return; }

        if (ctx.User.Identity?.IsAuthenticated != true)
        {
            ctx.Response.Headers.WWWAuthenticate = "Bearer";
            await Deny(ctx, 401, "Authentication required", "A valid Bearer token is required", "unauthenticated");
            return;
        }

        var roles = ctx.User.FindAll(ClaimTypes.Role).Select(c => c.Value).Distinct().ToArray();
        var required = Rbac.Required(ctx.Request.Method, ctx.Request.Path.Value ?? "");
        var azp = ctx.User.FindFirstValue("azp");
        var user = ctx.User.FindFirstValue("preferred_username") ?? azp ?? ctx.User.FindFirstValue("sub") ?? "unknown";
        if (!roles.Intersect(required).Any())
        {
            log.LogWarning("Access denied for {User} ({Roles}) to {Method} {Path}", user, string.Join(",", roles), ctx.Request.Method, ctx.Request.Path);
            await Deny(ctx, 403, "Forbidden", "Your role does not allow this operation", "forbidden");
            return;
        }

        var machineClient = roles.Contains(Roles.Client) && !roles.Contains(Roles.Operator) && !roles.Contains(Roles.Admin);
        caller.Authenticated = true;
        caller.UserId = user;
        caller.Roles = roles;
        caller.ClientId = machineClient ? azp ?? user : null;
        actor.Type = machineClient ? "CONSUMER" : "OPERATOR";
        actor.Id = user;

        if (caller.ClientId is { } clientId && OwnedRoute().Match(ctx.Request.Path.Value ?? "") is { Success: true } m)
        {
            var id = m.Groups[2].Value;
            var owns = m.Groups[1].Value switch
            {
                "signature-processes" => await db.Processes.AsNoTracking().AnyAsync(p => p.Id == id && p.ClientId == clientId, ctx.RequestAborted),
                "proofing-sessions" => await db.ProofingSessions.AsNoTracking().AnyAsync(s => s.Id == id && s.ClientId == clientId, ctx.RequestAborted),
                "callbacks" => await db.CallbackRegistrations.AsNoTracking().AnyAsync(c => c.CallbackId == id && c.ClientId == clientId, ctx.RequestAborted),
                _ => false
            };
            if (!owns)
            {
                await Deny(ctx, 404, "Not found", "Resource not found", null);
                return;
            }
        }
        await next(ctx);
    }

    private static async Task Deny(HttpContext ctx, int status, string title, string detail, string? reason)
    {
        if (reason is not null) Telemetry.SecurityDenied.Add(1, new KeyValuePair<string, object?>("reason", reason));
        var pd = new ProblemDetails { Status = status, Title = title, Detail = detail, Type = $"https://httpstatuses.io/{status}" };
        pd.Extensions["correlationId"] = ctx.CorrelationId();
        ctx.Response.StatusCode = status;
        await Results.Json(pd, contentType: "application/problem+json").ExecuteAsync(ctx);
    }
}
