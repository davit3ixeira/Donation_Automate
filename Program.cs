using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using DonationAutomate.Services;

var builder = WebApplication.CreateBuilder(args);

// Login, email server and other internal settings for a development machine (git-ignored). On
// the server they come from environment variables instead; see appsettings.Local.example.json.
// Environment variables and command-line arguments are added again so they still win over it.
builder.Configuration
    .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args);

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizePage("/Admin");
    options.Conventions.AuthorizePage("/Emails");
});
builder.Services.AddSingleton<SpreadsheetStore>();
builder.Services.AddSingleton<EmailSettingsStore>();
builder.Services.AddSingleton<SiteSettingsStore>();
builder.Services.AddSingleton<MailService>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<LoginCodes>();
builder.Services.AddSingleton<TrustedDevices>();
builder.Services.AddSingleton<AdminPasswords>();
builder.Services.AddSingleton<PasswordResets>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Login";
        options.AccessDeniedPath = "/Login";
        // One admin, a code on each new browser: stay logged in while the site keeps being used.
        options.ExpireTimeSpan = TimeSpan.FromDays(365);
        options.SlidingExpiration = true;
        // A changed password ends every session.
        options.Events.OnValidatePrincipal = AdminSession.ValidateAsync;
    });
builder.Services.AddAuthorization();

// Protects the login cookie. A fixed name keeps the keys valid if the project folder moves.
builder.Services.AddDataProtection().SetApplicationName("DonationAutomate");

var app = builder.Build();

// On the server, Caddy terminates HTTPS and passes requests on from the same machine: take the
// original scheme from it, so cookies are marked secure. Only loopback proxies are trusted.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();

// For testing on a development machine without the admin inbox: with Admin:DevAutoLogin true
// (in appsettings.Local.json), requests from this computer count as the admin, with no password,
// code or cookie. Never in Production, whatever the setting says.
if (app.Environment.IsDevelopment() && app.Configuration.GetValue("Admin:DevAutoLogin", false))
{
    app.Logger.LogWarning("Admin:DevAutoLogin is on: requests from this computer are logged in as the admin.");
    app.Use(async (context, next) =>
    {
        if (context.User.Identity?.IsAuthenticated != true
            && context.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip))
        {
            var email = app.Configuration["Admin:Email"] ?? "admin@localhost";
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, email)], "DevAutoLogin"));
        }
        await next();
    });
}

app.UseSiteAccess();
app.UseAuthorization();
app.MapRazorPages();

app.Run();
