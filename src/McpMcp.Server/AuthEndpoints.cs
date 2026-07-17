using System.Security.Claims;
using McpMcp.Abstractions;
using McpMcp.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace McpMcp.Server;

/// <summary>
/// Cookie-Login/Logout als native Form-POST-Endpoints (WP6.1). Ein Blazor-Circuit kann keine
/// Cookies setzen, daher läuft die Anmeldung über diese klassischen HTTP-Endpoints.
/// </summary>
internal static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/auth/login", async (
            HttpContext ctx, IUiUserService users, IAuditSink audit, TimeProvider time, CancellationToken ct) =>
        {
            var form = await ctx.Request.ReadFormAsync(ct);
            var username = form["username"].ToString();
            var password = form["password"].ToString();
            var returnUrl = form["returnUrl"].ToString();

            var user = await users.ValidateCredentialsAsync(username, password, ct);
            if (user is null)
            {
                audit.Record(new AuditEvent(
                    time.GetUtcNow(), null, CallOrigin.Ui, AuditEventKind.Authentication, null,
                    $"ui-login-failed:{username}", InvocationStatus.Denied, null, null, null, null));
                return Results.Redirect("/login?failed=true");
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, user.Username),
                new(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new(UiPolicies.RoleClaim, user.Role.ToString()),
            };
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

            audit.Record(new AuditEvent(
                time.GetUtcNow(), null, CallOrigin.Ui, AuditEventKind.Authentication, null,
                $"ui-login:{user.Username}", InvocationStatus.Success, null, null, null, null));

            return Results.Redirect(IsLocal(returnUrl) ? returnUrl : "/");
        }).DisableAntiforgery(); // Login besitzt kein gültiges Antiforgery-Token vor der Anmeldung; Cookie SameSite=Strict schützt.

        app.MapPost("/auth/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        });
    }

    private static bool IsLocal(string? url)
        => !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\");
}
