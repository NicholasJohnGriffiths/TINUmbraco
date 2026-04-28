param(
    [Parameter(Mandatory = $true)]
    [string]$SubscriptionId,

    [Parameter(Mandatory = $true)]
    [string]$ResourceGroupName,

    [Parameter(Mandatory = $false)]
    [string]$Location = 'eastus',

    [Parameter(Mandatory = $false)]
    [string]$ParameterFile = 'infra/main.parameters.json',

    [Parameter(Mandatory = $false)]
    [switch]$DeployToStagingSlot,

    [Parameter(Mandatory = $false)]
    [switch]$SwapStagingToProduction
)

$ErrorActionPreference = 'Stop'

Write-Host "Setting Azure subscription context..."
az account set --subscription $SubscriptionId

if (-not (az group exists --name $ResourceGroupName | ConvertFrom-Json)) {
    Write-Host "Creating resource group $ResourceGroupName in $Location..."
    az group create --name $ResourceGroupName --location $Location | Out-Null
}

Write-Host "Deploying infrastructure (App Service + Azure SQL)..."
$deploymentName = "umbraco-$(Get-Date -Format 'yyyyMMddHHmmss')"

$outputs = az deployment group create `
    --name $deploymentName `
    --resource-group $ResourceGroupName `
    --template-file "infra/main.bicep" `
    --parameters "@$ParameterFile" `
    --query "properties.outputs" -o json | ConvertFrom-Json

$webAppName = $outputs.webAppName.value
$stagingSlotName = $outputs.stagingSlotName.value

Write-Host "Publishing Umbraco app to Azure Web App $webAppName..."
dotnet publish "src/TINUmbraco.Web/TINUmbraco.Web.csproj" -c Release -o "publish"

if ($DeployToStagingSlot -and -not [string]::IsNullOrWhiteSpace($stagingSlotName)) {
    Write-Host "Deploying package to staging slot '$stagingSlotName'..."
    az webapp deploy `
        --resource-group $ResourceGroupName `
        --name $webAppName `
        --slot $stagingSlotName `
        --src-path "publish"

    if ($SwapStagingToProduction) {
        Write-Host "Swapping staging slot into production..."
        az webapp deployment slot swap `
            --resource-group $ResourceGroupName `
            --name $webAppName `
            --slot $stagingSlotName `
            --target-slot production
    }
}
else {
    Write-Host "Deploying package to production slot..."
    az webapp deploy `
        --resource-group $ResourceGroupName `
        --name $webAppName `
        --src-path "publish"
}

Write-Host "Deployment complete."
Write-Host "Production URL: https://$webAppName.azurewebsites.net"
if (-not [string]::IsNullOrWhiteSpace($stagingSlotName)) {
    Write-Host "Staging URL: https://$webAppName-$stagingSlotName.azurewebsites.net"
}
