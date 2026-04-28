@description('Location for all Azure resources')
param location string = resourceGroup().location

@description('Global name prefix for the deployment (used for App Service and SQL resources)')
@minLength(3)
@maxLength(18)
param appName string

@description('SKU name for the Linux App Service plan')
@allowed([
  'B1'
  'S1'
  'P1v3'
])
param appServiceSkuName string = 'B1'

@description('Create a staging deployment slot for safer releases')
param enableStagingSlot bool = true

@description('Name of the staging slot when enabled')
param stagingSlotName string = 'staging'

@description('Enable custom hostname + managed certificate binding once DNS is ready')
param enableCustomDomainBinding bool = false

@description('Custom hostname to bind (for example: www.contoso.com)')
param customHostname string = ''

@description('Allow Azure services firewall rule (0.0.0.0) on SQL Server')
param allowAzureServicesFirewallRule bool = false

@description('Additional SQL firewall rules to allow specific client ranges')
param sqlFirewallRules array = []

@description('Public network access mode for Azure SQL logical server')
@allowed([
  'Enabled'
  'Disabled'
])
param sqlPublicNetworkAccess string = 'Enabled'

@description('Azure SQL admin login name')
param sqlAdminLogin string

@description('Azure SQL admin login password')
@secure()
param sqlAdminPassword string

@description('Azure SQL database name for Umbraco')
param sqlDatabaseName string = 'umbraco'

@description('Name of the App Service plan')
param appServicePlanName string = '${appName}-plan'

@description('Name of the Azure Web App')
param webAppName string = toLower('${appName}-web')

@description('Name of the Azure SQL logical server')
param sqlServerName string = toLower('${appName}sql')

@description('Name of the App Service environment variable for Umbraco DB connection string')
param umbracoConnectionStringName string = 'umbracoDbDSN'

var sqlConnectionString = 'Server=tcp:${sqlServerName}.${environment().suffixes.sqlServerHostname},1433;Initial Catalog=${sqlDatabaseName};Persist Security Info=False;User ID=${sqlAdminLogin};Password=${sqlAdminPassword};MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'

resource appServicePlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: appServicePlanName
  location: location
  kind: 'linux'
  sku: {
    name: appServiceSkuName
    tier: appServiceSkuName == 'B1' ? 'Basic' : (appServiceSkuName == 'S1' ? 'Standard' : 'PremiumV3')
    capacity: 1
  }
  properties: {
    reserved: true
  }
}

resource webApp 'Microsoft.Web/sites@2023-12-01' = {
  name: webAppName
  location: location
  kind: 'app,linux'
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    clientAffinityEnabled: false
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      alwaysOn: appServiceSkuName == 'B1' ? false : true
      minTlsVersion: '1.2'
      scmMinTlsVersion: '1.2'
      ftpsState: 'Disabled'
      appSettings: [
        {
          name: 'ASPNETCORE_ENVIRONMENT'
          value: 'Production'
        }
        {
          name: 'WEBSITE_RUN_FROM_PACKAGE'
          value: '1'
        }
      ]
      connectionStrings: [
        {
          name: umbracoConnectionStringName
          connectionString: sqlConnectionString
          type: 'SQLAzure'
        }
      ]
    }
  }
}

resource webAppFtpPublishingPolicy 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2023-12-01' = {
  parent: webApp
  name: 'ftp'
  properties: {
    allow: false
  }
}

resource webAppScmPublishingPolicy 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2023-12-01' = {
  parent: webApp
  name: 'scm'
  properties: {
    allow: false
  }
}

resource stagingSlot 'Microsoft.Web/sites/slots@2023-12-01' = if (enableStagingSlot) {
  parent: webApp
  name: stagingSlotName
  location: location
  kind: 'app,linux'
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      alwaysOn: appServiceSkuName == 'B1' ? false : true
      minTlsVersion: '1.2'
      scmMinTlsVersion: '1.2'
      ftpsState: 'Disabled'
      appSettings: [
        {
          name: 'ASPNETCORE_ENVIRONMENT'
          value: 'Production'
        }
        {
          name: 'WEBSITE_RUN_FROM_PACKAGE'
          value: '1'
        }
      ]
      connectionStrings: [
        {
          name: umbracoConnectionStringName
          connectionString: sqlConnectionString
          type: 'SQLAzure'
        }
      ]
    }
  }
}

resource stagingSlotFtpPublishingPolicy 'Microsoft.Web/sites/slots/basicPublishingCredentialsPolicies@2023-12-01' = if (enableStagingSlot) {
  parent: stagingSlot
  name: 'ftp'
  properties: {
    allow: false
  }
}

resource stagingSlotScmPublishingPolicy 'Microsoft.Web/sites/slots/basicPublishingCredentialsPolicies@2023-12-01' = if (enableStagingSlot) {
  parent: stagingSlot
  name: 'scm'
  properties: {
    allow: false
  }
}

resource managedCertificate 'Microsoft.Web/certificates@2023-12-01' = if (enableCustomDomainBinding && !empty(customHostname)) {
  name: '${webApp.name}-${replace(customHostname, '.', '-')}-managed-cert'
  location: location
  properties: {
    canonicalName: customHostname
    serverFarmId: appServicePlan.id
  }
}

resource customHostnameBinding 'Microsoft.Web/sites/hostNameBindings@2023-12-01' = if (enableCustomDomainBinding && !empty(customHostname)) {
  parent: webApp
  name: customHostname
  properties: {
    siteName: webApp.name
    hostNameType: 'Verified'
    sslState: 'SniEnabled'
    thumbprint: managedCertificate!.properties.thumbprint
  }
}

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: sqlServerName
  location: location
  properties: {
    administratorLogin: sqlAdminLogin
    administratorLoginPassword: sqlAdminPassword
    version: '12.0'
    publicNetworkAccess: sqlPublicNetworkAccess
    minimalTlsVersion: '1.2'
  }
}

resource sqlDatabase 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: sqlDatabaseName
  location: location
  sku: {
    name: 'S0'
    tier: 'Standard'
  }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
  }
}

resource allowAzureServicesFirewallRuleResource 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = if (allowAzureServicesFirewallRule && sqlPublicNetworkAccess == 'Enabled') {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource sqlFirewallRuleResources 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = [for rule in sqlFirewallRules: if (sqlPublicNetworkAccess == 'Enabled') {
  parent: sqlServer
  name: rule.name
  properties: {
    startIpAddress: rule.startIpAddress
    endIpAddress: rule.endIpAddress
  }
}]

output webAppName string = webApp.name
output webAppUrl string = 'https://${webApp.properties.defaultHostName}'
output stagingSlotName string = enableStagingSlot ? stagingSlotName : ''
output stagingSlotUrl string = enableStagingSlot ? 'https://${webApp.name}-${stagingSlotName}.azurewebsites.net' : ''
output sqlServerName string = sqlServer.name
output sqlDatabaseName string = sqlDatabase.name
