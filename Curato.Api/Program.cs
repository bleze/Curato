using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

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
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
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

builder.Services.AddAuthorization();

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

app.UseHttpsRedirection();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// Minimal health check — deliberately unauthenticated (PLAN.md §15: login + a
// health check are the only routes that don't require a session).
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Serves Curato.Web's built static output once it's produced (one deployable
// artifact per PLAN.md §1 — no separate frontend server/CORS setup).
app.UseDefaultFiles();
app.UseStaticFiles();

app.Run();
