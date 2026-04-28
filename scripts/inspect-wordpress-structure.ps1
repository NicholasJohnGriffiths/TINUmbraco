param(
    [Parameter(Mandatory = $true)]
    [string]$SiteUrl,

    [Parameter(Mandatory = $false)]
    [string]$Username,

    [Parameter(Mandatory = $false)]
    [string]$AppPassword,

    [Parameter(Mandatory = $false)]
    [string]$OutputPath = "wordpress-structure-report.json",

    [Parameter(Mandatory = $false)]
    [switch]$IncludeSamples
)

$ErrorActionPreference = "Stop"

function Join-Url {
    param(
        [string]$Base,
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Base)) {
        return $Path
    }

    return ($Base.TrimEnd('/') + '/' + $Path.TrimStart('/'))
}

function Get-AuthHeaders {
    param(
        [string]$User,
        [string]$Password
    )

    if ([string]::IsNullOrWhiteSpace($User) -or [string]::IsNullOrWhiteSpace($Password)) {
        return @{}
    }

    $bytes = [System.Text.Encoding]::UTF8.GetBytes("$User`:$Password")
    $token = [Convert]::ToBase64String($bytes)

    return @{ Authorization = "Basic $token" }
}

function Invoke-WpApi {
    param(
        [string]$Url,
        [hashtable]$Headers
    )

    try {
        return Invoke-RestMethod -Uri $Url -Headers $Headers -Method Get
    }
    catch {
        return [PSCustomObject]@{
            __error = $true
            message = $_.Exception.Message
            url = $Url
        }
    }
}

$baseUrl = $SiteUrl.TrimEnd('/')
$headers = Get-AuthHeaders -User $Username -Password $AppPassword

$rootUrl = Join-Url -Base $baseUrl -Path "wp-json/"
$typesUrl = Join-Url -Base $baseUrl -Path "wp-json/wp/v2/types"
$taxonomiesUrl = Join-Url -Base $baseUrl -Path "wp-json/wp/v2/taxonomies"

Write-Host "Checking WordPress REST root: $rootUrl"
$root = Invoke-WpApi -Url $rootUrl -Headers $headers

Write-Host "Fetching post types: $typesUrl"
$types = Invoke-WpApi -Url $typesUrl -Headers $headers

Write-Host "Fetching taxonomies: $taxonomiesUrl"
$taxonomies = Invoke-WpApi -Url $taxonomiesUrl -Headers $headers

$typeSummaries = @()
$samplePayloads = @()

if ($types -and -not $types.__error) {
    $typeProps = $types.PSObject.Properties

    foreach ($prop in $typeProps) {
        $typeValue = $prop.Value

        $summary = [PSCustomObject]@{
            key = $prop.Name
            slug = $typeValue.slug
            name = $typeValue.name
            rest_base = $typeValue.rest_base
            rest_namespace = $typeValue.rest_namespace
            supports = $typeValue.supports
            hierarchical = $typeValue.hierarchical
            viewable = $typeValue.viewable
        }

        $typeSummaries += $summary

        if ($IncludeSamples -and -not [string]::IsNullOrWhiteSpace($typeValue.rest_base)) {
            $sampleUrl = Join-Url -Base $baseUrl -Path ("wp-json/{0}/{1}?per_page=1" -f $typeValue.rest_namespace, $typeValue.rest_base)
            Write-Host "Fetching sample for type '$($prop.Name)': $sampleUrl"
            $sample = Invoke-WpApi -Url $sampleUrl -Headers $headers

            $samplePayloads += [PSCustomObject]@{
                type = $prop.Name
                endpoint = $sampleUrl
                sample = $sample
            }
        }
    }
}

$taxonomySummaries = @()

if ($taxonomies -and -not $taxonomies.__error) {
    $taxonomyProps = $taxonomies.PSObject.Properties

    foreach ($prop in $taxonomyProps) {
        $taxonomyValue = $prop.Value

        $taxonomySummaries += [PSCustomObject]@{
            key = $prop.Name
            slug = $taxonomyValue.slug
            name = $taxonomyValue.name
            rest_base = $taxonomyValue.rest_base
            rest_namespace = $taxonomyValue.rest_namespace
            types = $taxonomyValue.types
        }
    }
}

$report = [PSCustomObject]@{
    generatedUtc = (Get-Date).ToUniversalTime().ToString("o")
    siteUrl = $baseUrl
    root = $root
    types = if ($types -and $types.__error) { $types } else { $typeSummaries }
    taxonomies = if ($taxonomies -and $taxonomies.__error) { $taxonomies } else { $taxonomySummaries }
    samples = $samplePayloads
}

$report | ConvertTo-Json -Depth 20 | Set-Content -Path $OutputPath -Encoding UTF8

Write-Host "Report written to: $OutputPath"
