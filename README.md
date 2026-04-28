# TIN Umbraco on Azure (C# Razor)

This workspace contains a latest-version Umbraco app built with C# and Razor, configured for Azure App Service and Azure SQL.

## Included in this solution

- Umbraco app: `src/TINUmbraco.Web`
- Infrastructure as code: `infra/main.bicep`
- Deployment parameters: `infra/main.parameters.json`
- Deployment script: `scripts/deploy-azure.ps1`
- GitHub Actions CI/CD workflow: `.github/workflows/azure-webapp-cicd.yml`

## Prerequisites

- .NET SDK 10.x
- Azure CLI (`az`)
- PowerShell 5.1+ or PowerShell 7+
- Azure subscription

## Infrastructure features

The Bicep template deploys:

- Linux App Service Plan
- Azure Web App for Umbraco
- Optional staging slot (enabled by default)
- Azure SQL logical server + database
- SQL connection string configured as `umbracoDbDSN`
- Security hardening defaults:
  - HTTPS only on app
  - TLS 1.2 minimum on App Service and SQL
  - FTP/SCM basic publishing credentials disabled
  - `AllowAzureServices` SQL firewall rule disabled by default
  - Explicit SQL firewall rules via parameters

## Configure parameters

Edit `infra/main.parameters.json`:

- Required:
  - `appName`
  - `sqlAdminLogin`
  - `sqlAdminPassword`
- Security options:
  - `sqlPublicNetworkAccess` (`Enabled` or `Disabled`)
  - `allowAzureServicesFirewallRule` (`false` recommended)
  - `sqlFirewallRules` (your office/VPN/public IP ranges)
- Staging options:
  - `enableStagingSlot`
  - `stagingSlotName`
- Custom domain options:
  - `enableCustomDomainBinding`
  - `customHostname`

## Deploy infra and app

From repo root:

```powershell
./scripts/deploy-azure.ps1 -SubscriptionId "<subscription-id>" -ResourceGroupName "rg-tinumbraco-prod" -Location "eastus"
```

Deploy to staging slot and swap to production:

```powershell
./scripts/deploy-azure.ps1 -SubscriptionId "<subscription-id>" -ResourceGroupName "rg-tinumbraco-prod" -DeployToStagingSlot -SwapStagingToProduction
```

Use a custom parameter file:

```powershell
./scripts/deploy-azure.ps1 -SubscriptionId "<subscription-id>" -ResourceGroupName "rg-tinumbraco-prod" -ParameterFile "infra/main.parameters.json"
```

## Umbraco unattended setup (recommended)

Set these App Service settings:

- `Umbraco__CMS__Unattended__InstallUnattended=true`
- `Umbraco__CMS__Unattended__UnattendedUserName=<admin-username>`
- `Umbraco__CMS__Unattended__UnattendedEmail=<admin-email>`
- `Umbraco__CMS__Unattended__UnattendedPassword=<strong-password>`

## CI/CD with GitHub Actions

Workflow file: `.github/workflows/azure-webapp-cicd.yml`

### Configure GitHub repository secrets

- `AZURE_CLIENT_ID`
- `AZURE_TENANT_ID`
- `AZURE_SUBSCRIPTION_ID`

### Configure GitHub repository variables

- `AZURE_WEBAPP_NAME` (the app service name)
- `AZURE_RESOURCE_GROUP` (resource group name)
- Optional: `AZURE_STAGING_SLOT` (for slot deploy + swap)

### Create federated identity credentials (OIDC)

1. Create an Entra ID app registration/service principal.
2. Add federated credentials for your repository/branch or environment.
3. Grant the service principal access to your resource group (Contributor at minimum; restrict further when possible).

## Custom domain and managed HTTPS

The template supports optional custom domain + managed certificate binding when:

1. DNS ownership verification records are already in place.
2. `enableCustomDomainBinding=true`
3. `customHostname` is set (example: `www.example.com`)

If DNS is not ready, keep `enableCustomDomainBinding=false`, deploy first, then enable once verification is complete.

## Local run

```powershell
dotnet run --project src/TINUmbraco.Web
```

Then open the local URL and complete setup for local development.
