using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Fy1Switch.Relay;

public static class RelayWebUi
{
    private const string AdminName = "admin";

    public static void AddRelayWebUi(this IServiceCollection services, RelaySettings settings)
    {
        if (settings.Management is not { } management) return;
        var origin = new Uri(management.PublicUrl);
        services.AddLogging(logging => logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning));
        Directory.CreateDirectory(management.DataDirectory);
        services.AddDataProtection().SetApplicationName("Fy1Switch.Relay")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(management.DataDirectory, "keys")));
        services.AddDbContext<RelayIdentityDbContext>(options => options.UseSqlite(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(management.DataDirectory, "identity.db"), Pooling = false
        }.ToString()));
        services.AddIdentity<IdentityUser, IdentityRole>(options =>
        {
            options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
            options.Password.RequiredLength = 12;
            options.Password.RequireDigit = true;
            options.Password.RequireLowercase = true;
            options.Password.RequireUppercase = true;
            options.Password.RequireNonAlphanumeric = true;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
        }).AddEntityFrameworkStores<RelayIdentityDbContext>().AddDefaultTokenProviders();
        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = "__Host-Fy1SwitchRelay";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.LoginPath = "/login";
            options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
            options.SlidingExpiration = false;
            options.Events.OnRedirectToLogin = context => RedirectOrStatus(context, 401, management.PublicUrl);
            options.Events.OnRedirectToAccessDenied = context => RedirectOrStatus(context, 403, management.PublicUrl);
        });
        services.Configure<CookieAuthenticationOptions>(IdentityConstants.TwoFactorUserIdScheme, options =>
        {
            options.Cookie.Name = "__Host-Fy1SwitchRelay.Passkey";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
        });
        services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero);
        services.Configure<IdentityPasskeyOptions>(options =>
        {
            options.ServerDomain = origin.Host;
            options.UserVerificationRequirement = "required";
            options.AuthenticatorTimeout = TimeSpan.FromMinutes(3);
            options.ValidateOrigin = context => ValueTask.FromResult(
                !context.CrossOrigin && string.Equals(context.Origin, management.PublicUrl, StringComparison.Ordinal));
        });
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "__RequestVerificationToken";
            options.Cookie.Name = "__Host-Fy1SwitchRelay.Csrf";
            options.Cookie.HttpOnly = true;
            // TLS terminates at the configured reverse proxy. CookiePolicy below always
            // emits Secure, without inferring TLS from untrusted forwarding headers.
            options.Cookie.SecurePolicy = CookieSecurePolicy.None;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });
        services.AddSingleton<RelayPasskeyCeremonies>();
        services.AddAuthorization();
        services.AddRazorPages(options => options.Conventions.AuthorizePage("/Index"))
            .AddApplicationPart(typeof(RelayWebUi).Assembly);
        services.AddRateLimiter(options => options.AddPolicy("relay-account", context =>
            RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ =>
                new FixedWindowRateLimiterOptions { PermitLimit = 12, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
    }

    public static async Task InitializeRelayWebUiAsync(this WebApplication app)
    {
        if (app.Services.GetRequiredService<RelaySettings>().Management is not { } management) return;
        using var scope = app.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<RelayIdentityDbContext>();
        await database.Database.EnsureCreatedAsync();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        if (await users.FindByNameAsync(AdminName) is not null) return;
        if (management.AdminPasswordFile is not { } passwordPath)
            throw new InvalidOperationException("The first WebUI start requires management.adminPasswordFile.");
        string password;
        try
        {
            var info = new FileInfo(passwordPath);
            if (!info.Exists || info.Length is < 12 or > 4096) throw new IOException();
            password = (await File.ReadAllTextAsync(passwordPath)).TrimEnd('\r', '\n');
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Cannot read the initial administrator password file.");
        }
        if (password.Length > 1024) throw new InvalidOperationException("The initial administrator password is too long.");
        var result = await users.CreateAsync(new IdentityUser(AdminName) { LockoutEnabled = true }, password);
        if (!result.Succeeded)
            throw new InvalidOperationException("The initial administrator password must have at least 12 characters including uppercase, lowercase, a digit and a symbol.");
    }

    public static void UseRelayWebUi(this WebApplication app)
    {
        if (app.Services.GetRequiredService<RelaySettings>().Management is not { } management) return;
        var origin = new Uri(management.PublicUrl);
        app.Use(async (context, next) =>
        {
            if (IsBrowserPath(context.Request.Path))
            {
                if (!string.Equals(context.Request.Host.Host, origin.Host, StringComparison.OrdinalIgnoreCase) ||
                    (context.Request.Host.Port ?? 443) != origin.Port ||
                    (context.Request.Headers.Origin.Count > 0 && context.Request.Headers.Origin.ToString() != management.PublicUrl))
                {
                    context.Response.StatusCode = 400;
                    return;
                }
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
                context.Response.Headers.XContentTypeOptions = "nosniff";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Headers.StrictTransportSecurity = "max-age=31536000";
            }
            await next(context);
        });
        app.UseStaticFiles();
        app.UseCookiePolicy(new CookiePolicyOptions
        {
            Secure = CookieSecurePolicy.Always,
            HttpOnly = Microsoft.AspNetCore.CookiePolicy.HttpOnlyPolicy.Always,
            MinimumSameSitePolicy = SameSiteMode.Strict
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapRazorPages();

        var account = app.MapGroup("/api/account").RequireRateLimiting("relay-account")
            .AddEndpointFilter<RelayAntiforgeryFilter>();
        account.MapPost("/login", async (LoginRequest request, SignInManager<IdentityUser> signIn) =>
        {
            if (request.Password is not { Length: >= 1 and <= 1024 }) return Error(401, "invalid_credentials");
            var result = await signIn.PasswordSignInAsync(AdminName, request.Password, isPersistent: false, lockoutOnFailure: true);
            return result.Succeeded ? Results.Ok(new { success = true }) : Error(401, "invalid_credentials");
        });
        account.MapPost("/logout", async (SignInManager<IdentityUser> signIn) =>
        {
            await signIn.SignOutAsync();
            return Results.Ok(new { success = true });
        }).RequireAuthorization();
        account.MapPost("/password", async (PasswordRequest request, HttpContext context, UserManager<IdentityUser> users, SignInManager<IdentityUser> signIn) =>
        {
            var user = await users.GetUserAsync(context.User);
            if (user is null) return Results.Unauthorized();
            if (request.CurrentPassword is not { Length: >= 1 and <= 1024 } || request.NewPassword is not { Length: >= 12 and <= 1024 }) return Error(400, "password_invalid");
            var result = await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
            if (!result.Succeeded) return Error(400, "password_invalid");
            await signIn.RefreshSignInAsync(user);
            return Results.Ok(new { success = true });
        }).RequireAuthorization();
        account.MapGet("/passkeys", async (HttpContext context, UserManager<IdentityUser> users) =>
        {
            var user = await users.GetUserAsync(context.User);
            if (user is null) return Results.Unauthorized();
            var keys = await users.GetPasskeysAsync(user);
            return Results.Ok(keys.Select(key => new { id = WebEncoders.Base64UrlEncode(key.CredentialId), name = key.Name ?? "Passkey", createdAt = key.CreatedAt }));
        }).RequireAuthorization();
        account.MapPost("/passkeys/options", async (HttpContext context, UserManager<IdentityUser> users, SignInManager<IdentityUser> signIn, RelayPasskeyCeremonies ceremonies) =>
        {
            var user = await users.GetUserAsync(context.User);
            if (user is null) return Results.Unauthorized();
            if ((await users.GetPasskeysAsync(user)).Count >= 8) return Error(409, "passkey_limit");
            if (!ceremonies.Begin(context, "register", user.Id)) return Error(429, "too_many_ceremonies");
            var options = await signIn.MakePasskeyCreationOptionsAsync(new() { Id = user.Id, Name = AdminName, DisplayName = "Fy1Switch Relay" });
            return Results.Content(options, "application/json");
        }).RequireAuthorization();
        account.MapPost("/passkeys", async (PasskeyRegistrationRequest request, HttpContext context, UserManager<IdentityUser> users, SignInManager<IdentityUser> signIn, RelayPasskeyCeremonies ceremonies) =>
        {
            var user = await users.GetUserAsync(context.User);
            if (user is null) return Results.Unauthorized();
            if (!ceremonies.Consume(context, "register", user.Id) || request.CredentialJson is not { Length: >= 2 and <= 60000 } ||
                string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 80 || request.Name.Any(char.IsControl)) return Error(400, "passkey_invalid");
            if ((await users.GetPasskeysAsync(user)).Count >= 8) return Error(409, "passkey_limit");
            var attestation = await signIn.PerformPasskeyAttestationAsync(request.CredentialJson);
            if (!attestation.Succeeded || attestation.UserEntity.Id != user.Id) return Error(400, "passkey_invalid");
            attestation.Passkey.Name = request.Name.Trim();
            var result = await users.AddOrUpdatePasskeyAsync(user, attestation.Passkey);
            return result.Succeeded ? Results.Ok(new { success = true }) : Error(400, "passkey_invalid");
        }).RequireAuthorization();
        account.MapDelete("/passkeys/{id}", async (string id, HttpContext context, UserManager<IdentityUser> users, SignInManager<IdentityUser> signIn) =>
        {
            var user = await users.GetUserAsync(context.User);
            if (user is null) return Results.Unauthorized();
            byte[] credentialId;
            try { credentialId = WebEncoders.Base64UrlDecode(id); }
            catch (FormatException) { return Error(400, "passkey_invalid"); }
            if (credentialId.Length is < 1 or > 1024) return Error(400, "passkey_invalid");
            if (!(await users.HasPasswordAsync(user)) && (await users.GetPasskeysAsync(user)).Count <= 1) return Error(409, "last_sign_in_method");
            var result = await users.RemovePasskeyAsync(user, credentialId);
            if (!result.Succeeded) return Error(400, "passkey_invalid");
            await users.UpdateSecurityStampAsync(user);
            await signIn.RefreshSignInAsync(user);
            return Results.Ok(new { success = true });
        }).RequireAuthorization();
        account.MapPost("/passkey-login/options", async (HttpContext context, SignInManager<IdentityUser> signIn, RelayPasskeyCeremonies ceremonies) =>
        {
            if (!ceremonies.Begin(context, "login", null)) return Error(429, "too_many_ceremonies");
            return Results.Content(await signIn.MakePasskeyRequestOptionsAsync(null), "application/json");
        });
        account.MapPost("/passkey-login", async (PasskeyLoginRequest request, HttpContext context, SignInManager<IdentityUser> signIn, RelayPasskeyCeremonies ceremonies) =>
        {
            if (!ceremonies.Consume(context, "login", null) || request.CredentialJson is not { Length: >= 2 and <= 60000 }) return Error(401, "invalid_credentials");
            var result = await signIn.PasskeySignInAsync(request.CredentialJson);
            return result.Succeeded ? Results.Ok(new { success = true }) : Error(401, "invalid_credentials");
        });
    }

    // Includes Razor Pages aliases such as /Index and /login/, static assets and 404s.
    private static bool IsBrowserPath(PathString path) => !path.StartsWithSegments("/v1");
    private static IResult Error(int status, string errorCode) => Results.Json(new { errorCode }, statusCode: status);
    private static Task RedirectOrStatus(RedirectContext<CookieAuthenticationOptions> context, int status, string publicUrl)
    {
        if (context.Request.Path.StartsWithSegments("/api")) context.Response.StatusCode = status;
        else context.Response.Redirect(publicUrl + new Uri(context.RedirectUri, UriKind.Absolute).PathAndQuery);
        return Task.CompletedTask;
    }

    public sealed record LoginRequest(string Password);
    public sealed record PasswordRequest(string CurrentPassword, string NewPassword);
    public sealed record PasskeyRegistrationRequest(string CredentialJson, string Name);
    public sealed record PasskeyLoginRequest(string CredentialJson);
}

public sealed class RelayAntiforgeryFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!HttpMethods.IsGet(context.HttpContext.Request.Method))
        {
            try { await context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context.HttpContext); }
            catch (AntiforgeryValidationException) { return Results.Json(new { errorCode = "invalid_csrf" }, statusCode: 400); }
        }
        return await next(context);
    }
}

// In addition to Identity's protected ceremony cookie, enforce server-side single use.
public sealed class RelayPasskeyCeremonies
{
    private const string CookieName = "__Host-Fy1SwitchRelay.Ceremony";
    private readonly object gate = new();
    private readonly Dictionary<string, Ceremony> pending = new(StringComparer.Ordinal);

    public bool Begin(HttpContext context, string purpose, string? userId)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var expired in pending.Where(item => item.Value.ExpiresAt <= now).Select(item => item.Key).ToArray()) pending.Remove(expired);
            if (context.Request.Cookies.TryGetValue(CookieName, out var previous)) pending.Remove(previous);
            if (pending.Count >= 64) return false;
            var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            pending.Add(token, new(purpose, userId, now.AddMinutes(5)));
            context.Response.Cookies.Append(CookieName, token, new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = TimeSpan.FromMinutes(5) });
            return true;
        }
    }

    public bool Consume(HttpContext context, string purpose, string? userId)
    {
        context.Response.Cookies.Delete(CookieName, new CookieOptions { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/" });
        if (!context.Request.Cookies.TryGetValue(CookieName, out var token)) return false;
        lock (gate)
            return pending.Remove(token, out var ceremony) && ceremony.ExpiresAt > DateTimeOffset.UtcNow && ceremony.Purpose == purpose && ceremony.UserId == userId;
    }
    private sealed record Ceremony(string Purpose, string? UserId, DateTimeOffset ExpiresAt);
}
