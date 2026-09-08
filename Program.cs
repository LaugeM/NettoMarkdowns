using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using NettoMarkdowns.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddMemoryCache();
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

var app = builder.Build();

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
