using Microsoft.AspNetCore.DataProtection;
using DonationAutomate.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddSingleton<SpreadsheetStore>();
builder.Services.AddSingleton<EmailSettingsStore>();
builder.Services.AddSingleton<MailService>();

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
app.MapRazorPages();

app.Run();
