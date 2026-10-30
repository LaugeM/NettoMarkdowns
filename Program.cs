using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using NettoMarkdowns.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddMemoryCache();

// Pack sizes already looked up, plus the day's API call count. Small and always on.
builder.Services.AddSingleton(serviceProvider =>
{
    var path = serviceProvider.GetRequiredService<IOptions<SallingOptions>>().Value.ProductDatabasePath;
    var root = serviceProvider.GetRequiredService<IHostEnvironment>().ContentRootPath;
    return new ProductStore(Path.Combine(root, path));
});
builder.Services.AddSingleton<ApiQuota>();

// Binds the "Salling" section of appsettings.json / user-secrets to SallingOptions.
builder.Services.Configure<SallingOptions>(
    builder.Configuration.GetSection(SallingOptions.SectionName));

// A "typed client": DI hands SallingClient a preconfigured HttpClient, and the
// factory underneath pools connections properly (never `new HttpClient()` per request).
var sallingClient = builder.Services.AddHttpClient<SallingClient>(static (serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<IOptions<SallingOptions>>().Value;

    client.BaseAddress = new Uri("https://api.sallinggroup.com/");
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

    if (!string.IsNullOrWhiteSpace(options.ApiKey))
    {
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", options.ApiKey.Trim());
    }
});

// Demo mode: swap the network for canned data by adding a handler to the pipeline.
if (builder.Configuration.GetValue<bool>($"{SallingOptions.SectionName}:UseSampleData"))
{
    builder.Services.AddTransient<SampleDataHandler>();
    sallingClient.AddHttpMessageHandler<SampleDataHandler>();
}

// Markdown log: polls in the background and keeps a history in SQLite for statistics.
var markdownLog = builder.Configuration.GetSection(MarkdownLogOptions.SectionName);
builder.Services.Configure<MarkdownLogOptions>(markdownLog);
if (markdownLog.GetValue<bool>(nameof(MarkdownLogOptions.Enabled)))
{
    builder.Services.AddSingleton(serviceProvider =>
    {
        var path = serviceProvider.GetRequiredService<IOptions<MarkdownLogOptions>>().Value.DatabasePath;
        var root = serviceProvider.GetRequiredService<IHostEnvironment>().ContentRootPath;
        return new MarkdownStore(Path.Combine(root, path));
    });
    builder.Services.AddTransient<ProductWeightFiller>();
    builder.Services.AddHostedService<MarkdownLogService>();
}

var app = builder.Build();

// On the server the app is reached through Tailscale Serve, which adds the Tailscale login
// of the device that sent the request and drops any copy the client sent. When logins are
// listed, every other request gets 403. Empty means no check, for running it locally.
var allowedLogins = (app.Configuration["Access:AllowedLogins"] ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .ToHashSet(StringComparer.OrdinalIgnoreCase);
if (allowedLogins.Count > 0)
{
    app.Use(async (context, next) =>
    {
        if (!allowedLogins.Contains(context.Request.Headers["Tailscale-User-Login"].ToString()))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Forbidden");
            return;
        }
        await next();
    });
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();
