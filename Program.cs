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

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Login";
        options.AccessDeniedPath = "/Login";
        // One admin, a code on each new browser: stay logged in while the site keeps being used.
        options.ExpireTimeSpan = TimeSpan.FromDays(365);
        options.SlidingExpiration = true;
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
app.UseSiteAccess();
app.UseAuthorization();
app.MapRazorPages();

app.Run();
