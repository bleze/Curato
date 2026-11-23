using System.Threading.RateLimiting;
using Curato.Api;
using Curato.Data;
using Curato.iCloud;
using Curato.Organizer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Server-wide config — currently just the single admin account (§15) — lives
// in its own database, separate from any library's `.curato/library.db`
// (§2), in the server's persistent data directory (the Docker volume at
// `/data`, or a `data` folder next to the app for local dev). Resolved
// against ContentRootPath, not the process's current working directory —
// "./data" would otherwise land somewhere different depending on where
// `dotnet run`/`dotnet Curato.Api.dll` happens to be invoked from.
var dataDirectory = builder.Configuration["Curato:DataDirectory"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "data");
EnsureWritable(dataDirectory, "settings folder");
var serverDatabase = new ServerDatabase(dataDirectory);
serverDatabase.EnsureSchema();
builder.Services.AddSingleton(serverDatabase);

// In Docker the library is whatever the compose file mounts at /photos (the
// image sets Curato__LibraryFolder), so there's nothing for the user to
// choose: store it as the Library Folder on every start, and Settings shows
// it read-only. Unset (running outside Docker), the Settings field decides.
var fixedLibraryFolder = builder.Configuration["Curato:LibraryFolder"];
if (!string.IsNullOrWhiteSpace(fixedLibraryFolder))
{
    fixedLibraryFolder = Path.GetFullPath(fixedLibraryFolder);
    EnsureWritable(fixedLibraryFolder, "photo library", mustExist: true);
    new LibraryFolderRepository(serverDatabase).Set(fixedLibraryFolder);
}
builder.Services.AddSingleton(new FixedLibraryFolder(string.IsNullOrWhiteSpace(fixedLibraryFolder) ? null : fixedLibraryFolder));

// Serializes ScanService/OrganizeService calls against the same library
// root (PLAN.md §4) — needed now that the iCloud background sync below can
// genuinely run concurrently with a user-triggered scan/upload/organize,
// which nothing defended against before. Shared singleton state, unlike the
// per-request repositories controllers otherwise construct inline.
builder.Services.AddSingleton<LibraryOperationGate>();

// A live "files found so far" counter a scan updates as it walks, read by a
// separate polling request while the scan's own single blocking request is
// still in flight — see ScanProgressTracker's own doc comment.
builder.Services.AddSingleton<ScanProgressTracker>();

// Direct iCloud integration (PLAN.md §4) — shells out to the MIT-licensed
// icloudpd/pyicloud tools (Python) rather than porting their auth/sync
// logic into C#. See Curato.iCloud for why, and tools/icloud-sync for the
// Python side.
builder.Services.AddSingleton<iCloudProcessOptions>();
builder.Services.AddSingleton<iCloudAuthClient>();
builder.Services.AddSingleton<iCloudSyncRunner>();
builder.Services.AddHostedService<iCloudSyncBackgroundService>();

// Resolves place names for GPS coordinates out-of-band from any page load —
// see GeocodeWarmupBackgroundService's own doc comment for why the library
// view itself (LibraryController.GetLibrary) only ever reads the cache this
// fills, never calls Nominatim directly.
builder.Services.AddHostedService<GeocodeWarmupBackgroundService>();

// Reads X-Forwarded-Proto/X-Forwarded-For from the reverse proxy PLAN.md
// §15 expects in front for any internet-facing deployment (Synology DSM's
// built-in proxy + its Let's Encrypt integration, primarily), so
// Request.IsHttps below reflects the client's real HTTPS connection even
// though the proxy-to-container hop itself is plain HTTP — without this,
// the auth cookie's SameAsRequest Secure policy would always evaluate
// false behind a TLS-terminating proxy, silently sending the session
// cookie over an unencrypted hop it doesn't actually need to travel over.
// KnownNetworks/KnownProxies are cleared (trust the header from whatever
// sent it) rather than pinned to a specific proxy IP, since that varies by
// setup — safe only because Curato.Api's own port is never meant to be
// reachable except through that trusted reverse proxy (never publish it
// directly to the internet, or a client could forge these headers itself).
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// Session-based auth for the web UI and the Android TV client (PLAN.md §15).
// Cookie settings are hardened up front rather than bolted on later.
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        // SameAsRequest, not Always: behind the reverse proxy PLAN.md §15
        // expects in production, TLS is terminated there — Curato.Api itself
        // only ever receives plain HTTP from that proxy hop. Request.IsHttps
        // still correctly reflects the client's real HTTPS connection once
        // the Forwarded Headers middleware above rewrites it from
        // X-Forwarded-Proto, so this correctly marks the cookie Secure in
        // that case while also still working for local http-only dev (no
        // proxy in front, no forwarded header, Request.IsHttps genuinely
        // false).
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        options.LoginPath = "/api/auth/login";
        options.Events.OnRedirectToLogin = context =>
        {
            // This is an API, not a page app: an unauthenticated request gets 401,
            // never a redirect to an HTML login page.
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
    });

// Secure by default (§15): every endpoint requires a session unless it opts
// out with [AllowAnonymous] (AuthController's setup/login/status only) —
// so a new controller added later is protected automatically, not by
// remembering to annotate it.
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

// Login brute-force protection (PLAN.md §15) — built into ASP.NET Core, no extra
// dependency. Applied to the login endpoint specifically once it exists (Phase 1);
// the policy is registered here so it's available from day one.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("login", limiterOptions =>
    {
        limiterOptions.Window = TimeSpan.FromMinutes(1);
        limiterOptions.PermitLimit = 5;
        limiterOptions.QueueLimit = 0;
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// No UseHttpsRedirection(): per PLAN.md §15, TLS termination belongs to a
// reverse proxy in front of Curato.Api, not the app itself — the app only
// ever expects to receive plain HTTP (from the proxy in production, or
// directly in local dev). Redirecting to HTTPS here was actively wrong, not
// just inconvenient: it broke the Vite dev proxy, which forwards plain HTTP
// and has no reason to expect (or trust) a redirect to a self-signed
// dev-cert HTTPS endpoint.

// Must run before anything that reads Request.Scheme/IsHttps or the remote
// IP (the cookie's SameAsRequest Secure policy below, any future IP-based
// rate limiting/audit logging per §15) — otherwise those see the plain-HTTP
// proxy-to-container hop instead of the client's real connection.
app.UseForwardedHeaders();

// A folder Curato may not read or write (on a NAS: a subfolder whose
// permissions don't include the container's user) aborts a scan by design —
// skipping it would make every file inside look deleted to ScanService. Say
// which folder and what to do, instead of a bare "500 Internal Server Error";
// the web UI shows a failed request's text as its error.
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var error = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    context.Response.ContentType = "text/plain; charset=utf-8";
    await context.Response.WriteAsync(error is UnauthorizedAccessException
        ? $"Curato has no access: {error.Message} Nothing was changed. Give the user Curato runs as "
          + "Read/Write on that folder (on a Synology: File Station -> right-click it -> Properties -> "
          + "Permission, applied to sub-folders and files), then try again."
        : "Unexpected server error; the container log has the details.");
}));

// Serves Curato.Web's built static output (one deployable artifact per
// PLAN.md §1 — no separate frontend server/CORS setup). Before
// authentication on purpose: the fallback policy below requires a session
// for everything that runs after it, which would 401 the page that shows
// the login form. The UI files are public; every /api route stays protected.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// Minimal health check — deliberately unauthenticated (PLAN.md §15: login + a
// health check are the only routes that don't require a session).
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();


app.Run();

// Fails startup with a message that says what to fix, instead of SQLite's
// "Error 14: unable to open database file" stack trace. On a NAS this is
// almost always a mapped folder owned by root (Docker creates a missing
// bind-mount folder as root) while the container runs as the compose
// file's `user:`.
static void EnsureWritable(string directory, string description, bool mustExist = false)
{
    // The library must be mounted, not created: an empty folder inside the
    // container would look like an empty library and vanish on recreate.
    if (mustExist && !Directory.Exists(directory))
    {
        Console.Error.WriteLine($"""

            Curato's {description} ({directory}) does not exist: nothing is mounted there.
            In Docker, map your photo folder to it under `volumes:` in the compose file, e.g.
                - /volume1/photo:{directory}
            then restart.

            """);
        Environment.Exit(1);
    }

    try
    {
        Directory.CreateDirectory(directory);
        var probe = Path.Combine(directory, $".curato-write-test-{Environment.ProcessId}");
        File.WriteAllText(probe, "");
        File.Delete(probe);
    }
    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
    {
        var user = OperatingSystem.IsWindows() ? Environment.UserName : $"uid {GetUnixId("-u")}, gid {GetUnixId("-g")}";
        Console.Error.WriteLine($"""

            Curato cannot write to its {description}: {directory}
            It runs as {user}, which has no write access there ({ex.Message}).
            In Docker, give that user write access to the NAS folder mapped to {directory} in the
            compose file, then restart.
            On Synology, shared folders use ACLs, so chown is often not enough, and the container
            does not get the user's other groups (e.g. administrators): in File Station, right-click
            the folder -> Properties -> Permission -> Create -> your user, Read + Write, applied to
            this folder, sub-folders and files.
            Elsewhere, over SSH: sudo chown -R {GetUnixId("-u")}:{GetUnixId("-g")} <that NAS folder>

            """);
        Environment.Exit(1);
    }
}

static string GetUnixId(string flag)
{
    try
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("id", flag) { RedirectStandardOutput = true });
        return process!.StandardOutput.ReadToEnd().Trim();
    }
    catch
    {
        return "?";
    }
}
