using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using DonationAutomate.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizePage("/Admin");
    options.Conventions.AuthorizePage("/Emails");
});
builder.Services.AddSingleton<SpreadsheetStore>();
builder.Services.AddSingleton<EmailSettingsStore>();
builder.Services.AddSingleton<SiteSettingsStore>();
builder.Services.AddSingleton<MailService>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Login";
        options.AccessDeniedPath = "/Login";
        options.ExpireTimeSpan = TimeSpan.FromDays(14);
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();

// Encrypts the saved SMTP password. A fixed name keeps the keys valid if the project folder moves.
builder.Services.AddDataProtection().SetApplicationName("DonationAutomate");

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

app.Run();
