using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using TINUmbraco.Web.Migrations;
using Umbraco.Cms.Core.DependencyInjection;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

string? keyVaultUri = builder.Configuration["AzureKeyVault:VaultUri"];
string keyVaultSecretName = builder.Configuration["AzureKeyVault:ConnectionStringSecretName"] ?? "umbraco-db-connection";
string mediaConnectionStringSecretName = builder.Configuration["AzureKeyVault:MediaConnectionStringSecretName"] ?? "umbraco-media-storage-connection";

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
}

builder.Services.AddScoped<MigrationContentRootLookup>();
builder.Services.AddScoped<IWordPressMigrationPropertyMapper, WordPressMigrationPropertyMapper>();
builder.Services.AddScoped<WordPressMigrationContentService>();
builder.Services.AddScoped<WordPressMigrationRunner>();

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

if (!string.IsNullOrWhiteSpace(migrationOptions.JsonPath))
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

await app.RunAsync();
