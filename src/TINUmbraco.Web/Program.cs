using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using TINUmbraco.Web.Migrations;
using TINUmbraco.Web.Tools;
using TINUmbraco.Web.Stripe;
using Umbraco.Cms.Core.DependencyInjection;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

string? keyVaultUri = builder.Configuration["AzureKeyVault:VaultUri"];
string keyVaultSecretName = builder.Configuration["AzureKeyVault:ConnectionStringSecretName"] ?? "umbraco-db-connection";
string mediaConnectionStringSecretName = builder.Configuration["AzureKeyVault:MediaConnectionStringSecretName"] ?? "umbraco-media-storage-connection";

bool isDevelopment = builder.Environment.IsDevelopment();
string stripeSecretKeySecretName = isDevelopment
    ? (builder.Configuration["AzureKeyVault:StripeTestSecretKeySecretName"] ?? "stripe-test-secret-key")
    : (builder.Configuration["AzureKeyVault:StripeLiveSecretKeySecretName"] ?? "stripe-live-secret-key");
string stripeWebhookSecretName = isDevelopment
    ? (builder.Configuration["AzureKeyVault:StripeTestWebhookSecretSecretName"] ?? "stripe-test-webhook-secret")
    : (builder.Configuration["AzureKeyVault:StripeLiveWebhookSecretSecretName"] ?? "stripe-live-webhook-secret");

builder.Services.AddControllersWithViews();
builder.Services.Configure<WordPressMigrationOptions>(builder.Configuration.GetSection("Migration:WordPress"));

if (!string.IsNullOrWhiteSpace(keyVaultUri))
{
    SecretClient secretClient = new(new Uri(keyVaultUri), new DefaultAzureCredential());
    KeyVaultSecret connectionStringSecret = secretClient.GetSecret(keyVaultSecretName);
    KeyVaultSecret mediaConnectionStringSecret = secretClient.GetSecret(mediaConnectionStringSecretName);

    builder.Configuration.AddInMemoryCollection(
    [
        new KeyValuePair<string, string?>("ConnectionStrings:umbracoDbDSN", connectionStringSecret.Value),
        new KeyValuePair<string, string?>("Umbraco:Storage:AzureBlob:Media:ConnectionString", mediaConnectionStringSecret.Value),
        new KeyValuePair<string, string?>("Umbraco:Storage:AzureBlob:ImageSharpCache:ConnectionString", mediaConnectionStringSecret.Value)
    ]);

    // Load Stripe secret key and webhook secret from Key Vault if they exist.
    try
    {
        KeyVaultSecret stripeSecretKeySecret = secretClient.GetSecret(stripeSecretKeySecretName);
        KeyVaultSecret stripeWebhookSecret = secretClient.GetSecret(stripeWebhookSecretName);
        builder.Configuration.AddInMemoryCollection(
        [
            new KeyValuePair<string, string?>("Stripe:SecretKey", stripeSecretKeySecret.Value),
            new KeyValuePair<string, string?>("Stripe:WebhookSecret", stripeWebhookSecret.Value)
        ]);
    }
    catch (Azure.RequestFailedException ex) when (ex.Status == 404)
    {
        // Secret not yet created in Key Vault — Stripe payments/webhooks will be unavailable until configured.
    }
}

builder.Services.Configure<StripeOptions>(builder.Configuration.GetSection("Stripe"));

builder.Services.AddScoped<MigrationContentRootLookup>();
builder.Services.AddHttpClient(nameof(WordPressMigrationMediaService));
builder.Services.AddScoped<WordPressMigrationMediaService>();
builder.Services.AddScoped<IWordPressMigrationPropertyMapper, WordPressMigrationPropertyMapper>();
builder.Services.AddScoped<WordPressMigrationContentService>();
builder.Services.AddScoped<WordPressMigrationRunner>();
builder.Services.AddScoped<IReportPricingService, UmbracoReportPricingService>();
builder.Services.AddScoped<IStripePaymentService, StripePaymentService>();
builder.Services.AddSingleton<MigrationDashboardService>();
builder.Services.AddSingleton<ToolsAccessService>();

builder.CreateUmbracoBuilder()
    .AddBackOffice()
    .AddWebsite()
    .AddAzureBlobMediaFileSystem()
    .AddAzureBlobImageSharpCache()
    .AddComposers()
    .Build();

WebApplication app = builder.Build();


await app.BootUmbracoAsync();

WordPressMigrationOptions migrationOptions = app.Services
    .GetRequiredService<Microsoft.Extensions.Options.IOptions<WordPressMigrationOptions>>()
    .Value;

if (migrationOptions.RunOnStartup && !string.IsNullOrWhiteSpace(migrationOptions.JsonPath))
{
    using IServiceScope scope = app.Services.CreateScope();
    WordPressMigrationRunner migrationRunner = scope.ServiceProvider.GetRequiredService<WordPressMigrationRunner>();
    await migrationRunner.RunFromJsonFileAsync(migrationOptions.JsonPath);
}


app.UseUmbraco()
    .WithMiddleware(u =>
    {
        u.UseBackOffice();
        u.UseWebsite();
    })
    .WithEndpoints(u =>
    {
        u.UseBackOfficeEndpoints();
        u.UseWebsiteEndpoints();
    });

app.MapControllers();

await app.RunAsync();
