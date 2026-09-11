using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LanTodo.Core;

internal static class AdminWeb
{
    public static async Task<WebApplication?> Start(string dataPath, Func<Packet, CancellationToken, Task<Packet>> execute, CancellationToken token)
    {
        var port = int.Parse(Environment.GetEnvironmentVariable("LANTODO_WEB_PORT") ?? "0");
        if (port == 0) return null;
        if (port is < 1024 or > 65535) throw new ArgumentException("LANTODO_WEB_PORT 必须为 1024–65535，或 0 关闭网页。");
        var tokenPath = Path.Combine(dataPath, "admin-token");
        var configured = Environment.GetEnvironmentVariable("LANTODO_ADMIN_TOKEN");
        string? password = !string.IsNullOrWhiteSpace(configured) ? configured : File.Exists(tokenPath) ? File.ReadAllText(tokenPath).Trim() : null;
        if (password is not null && password.Length < 24) throw new ArgumentException("管理令牌至少需要 24 个字符。");
        string? expected = password is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("Bearer " + password)));
        var securityGate = new SemaphoreSlim(1, 1);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 64 * 1024);
        builder.Logging.ClearProviders();
        builder.Services.AddDataProtection().SetApplicationName("LanTodo.Nas").PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataPath, "admin-keys")));
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
        {
            options.Cookie.Name = "LanTodo.Admin";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.ExpireTimeSpan = TimeSpan.FromDays(180);
            options.SlidingExpiration = true;
        });
        var web = builder.Build();
        web.UseAuthentication();
        bool Authorized(HttpContext context)
        {
            var current = expected;
            if (current is null) return true;
            if (context.User.FindFirstValue("credential") == current) return true;
            var supplied = SHA256.HashData(Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString()));
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(current), supplied);
        }
        Task SignIn(HttpContext context) => context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("credential", expected!) }, CookieAuthenticationDefaults.AuthenticationScheme)),
            new AuthenticationProperties { IsPersistent = true });
        web.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                if (context.Request.Method == "POST" && (!context.Request.HasJsonContentType() ||
                    (context.Request.Headers.Origin.Count > 0 && context.Request.Headers.Origin.ToString() != $"{context.Request.Scheme}://{context.Request.Host}")))
                { context.Response.StatusCode = 403; return; }
                var route = context.Request.Path.Value;
                if (route is not ("/api/auth" or "/api/login" or "/api/logout") && !Authorized(context))
                { context.Response.StatusCode = 401; return; }
            }
            await next(context);
        });
        foreach (var (url, name, mime) in new[]{("/", "index.html", "text/html; charset=utf-8"), ("/app.js", "app.js", "text/javascript; charset=utf-8"), ("/style.css", "style.css", "text/css; charset=utf-8")})
        {
            web.MapGet(url, () => Results.Stream(Assembly.GetExecutingAssembly().GetManifestResourceStream("LanTodo.Nas.Web." + name)!, mime));
        }
        web.MapGet("/api/auth", (HttpContext context) => Results.Json(new { configured = expected is not null, authenticated = Authorized(context), managedByEnvironment = !string.IsNullOrWhiteSpace(configured) }));
        web.MapPost("/api/login", async (HttpContext context) =>
        {
            if (expected is not null && !Authorized(context)) return Results.Unauthorized();
            if (expected is not null) await SignIn(context);
            return Results.Json(new { ok = true });
        });
        web.MapPost("/api/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Json(new { ok = true });
        });
        web.MapPost("/api/security", async (HttpContext context) =>
        {
            await securityGate.WaitAsync(context.RequestAborted);
            try
            {
                // Recheck after acquiring the setup lock: only the first anonymous setup wins.
                if (!Authorized(context)) return Results.Unauthorized();
                if (!string.IsNullOrWhiteSpace(configured)) return Results.BadRequest(new { error = "令牌由容器环境变量管理，请修改环境变量。" });
                var request = await JsonSerializer.DeserializeAsync<Packet>(context.Request.Body, Json.Options, context.RequestAborted);
                if (request?.Secret is not { Length: >= 24 and <= 256 } secret) return Results.BadRequest(new { error = "管理令牌须为 24–256 个字符。" });
                AtomicFile.Write(tokenPath, Encoding.UTF8.GetBytes(secret), true);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("Bearer " + secret)));
                await SignIn(context);
                return Results.Json(new { ok = true });
            }
            finally { securityGate.Release(); }
        });
        web.MapGet("/api/status", async (CancellationToken ct) => Results.Content((await execute(new("status"), ct)).Name!, "application/json"));
        var commands = new HashSet<string> { "invite", "cancel-invite", "delete-invite", "delete-device", "revoke", "sync", "backup", "pair", "address", "remove-address", "rename", "resolve", "upgrade-space", "leave-space", "delete-space", "new-space", "select-space", "rename-space", "public-address" };
        web.MapPost("/api/command", async (HttpContext context) =>
        {
            try
            {
                var request = await JsonSerializer.DeserializeAsync<Packet>(context.Request.Body, Json.Options, context.RequestAborted);
                if (request is null || !commands.Contains(request.Kind)) return Results.BadRequest(new { error = "未知管理操作。" });
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, token); deadline.CancelAfter(TimeSpan.FromSeconds(60));
                var reply = await execute(request, deadline.Token);
                return Results.Json(new { result = reply.Name, qr = request.Kind == "invite" && reply.Name is { } code ? Convert.ToBase64String(InviteQr.Png(code)) : null }, Json.Options);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return Results.BadRequest(new { error = ex.Message }); }
        });
        await web.StartAsync(token);
        Console.WriteLine($"Management web listening on HTTP {port}. First-time setup is available in the console; configured sessions are remembered.");
        return web;
    }
}
