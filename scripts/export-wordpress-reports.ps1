param(
    [string]$BaseUrl = "https://tin100.com",
    [int]$PerPage = 100,
    [int]$MaxItems = 0,
    [string]$OutFile = "d:\Dev\TINUmbraco\src\TINUmbraco.Web\migration-data.reports.from-api.json",
    [switch]$IncludeBody,
    [switch]$Publish
)

$ErrorActionPreference = "Stop"
$script:ReportPageHtmlCache = @{}

function Join-Url {
    param(
        [Parameter(Mandatory = $true)][string]$Base,
        [Parameter(Mandatory = $true)][string]$Path
    )

    if ($Base.EndsWith("/")) {
        $Base = $Base.TrimEnd("/")
    }

    if ($Path.StartsWith("/")) {
        $Path = $Path.TrimStart("/")
    }

    return "$Base/$Path"
}

function Get-FeaturedImageUrl {
    param([object]$Post)

    if ($null -eq $Post._embedded) {
        return $null
    }

    $embedded = $Post._embedded
    $mediaList = $embedded.'wp:featuredmedia'
    if ($null -eq $mediaList -or $mediaList.Count -eq 0) {
        return $null
    }

    $media = $mediaList[0]
    if ($null -ne $media.source_url -and -not [string]::IsNullOrWhiteSpace([string]$media.source_url)) {
        return [string]$media.source_url
    }

    if ($null -ne $media.media_details -and $null -ne $media.media_details.sizes -and $null -ne $media.media_details.sizes.full) {
        $full = $media.media_details.sizes.full
        if ($null -ne $full.source_url -and -not [string]::IsNullOrWhiteSpace([string]$full.source_url)) {
            return [string]$full.source_url
        }
    }

    return $null
}

function Get-FallbackBodyHtml {
    param([object]$Post)

    $title = [System.Net.WebUtility]::HtmlDecode([string]$Post.title.rendered).Trim()
    $link = [string]$Post.link
    $excerptHtml = ""

    if ($null -ne $Post.excerpt -and $null -ne $Post.excerpt.rendered -and -not [string]::IsNullOrWhiteSpace([string]$Post.excerpt.rendered)) {
        $excerptHtml = [string]$Post.excerpt.rendered
    }

    $safeTitle = [System.Net.WebUtility]::HtmlEncode($title)
    $safeLink = [System.Net.WebUtility]::HtmlEncode($link)

    return @"
<p>This content was imported from WordPress where the reports API returned an empty body.</p>
<p><strong>Source title:</strong> $safeTitle</p>
<p><a href="$safeLink" target="_blank" rel="noopener">View original report page</a></p>
$excerptHtml
"@.Trim()
}

function Get-BodyHtmlFromReportPage {
    param([object]$Post)

    $html = Get-ReportPageHtml -Post $Post
    if ([string]::IsNullOrWhiteSpace($html)) {
        return $null
    }

    $regexOptions = [System.Text.RegularExpressions.RegexOptions]::Singleline -bor [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
    $patterns = @(
        '<div class="x-text x-content[^\"]*">\s*(?<body><p>.*?</p>(?:\s*<p>.*?</p>)*)\s*</div>\s*<div class="x-anchor x-anchor-toggle',
        '<div class="x-text x-content[^\"]*">\s*(?<body>.*?)\s*</div>\s*</div>\s*</div>'
    )

    foreach ($pattern in $patterns) {
        $match = [System.Text.RegularExpressions.Regex]::Match($html, $pattern, $regexOptions)
        if (-not $match.Success) {
            continue
        }

        $body = Normalize-ExtractedBodyHtml -Html $match.Groups['body'].Value
        if (-not [string]::IsNullOrWhiteSpace($body)) {
            return $body
        }
    }

    return $null
}

function Get-ReportPageHtml {
    param([object]$Post)

    $link = [string]$Post.link
    if ([string]::IsNullOrWhiteSpace($link)) {
        return $null
    }

    if ($script:ReportPageHtmlCache.ContainsKey($link)) {
        return [string]$script:ReportPageHtmlCache[$link]
    }

    try {
        $response = Invoke-WebRequest -Uri $link -Method Get -UseBasicParsing
        $html = [string]$response.Content
        $script:ReportPageHtmlCache[$link] = $html
        return $html
    }
    catch {
        Write-Warning "Failed to fetch report page HTML for '$link': $($_.Exception.Message)"
        return $null
    }
}

function Get-SeoMetadataFromReportPage {
    param([object]$Post)

    $html = Get-ReportPageHtml -Post $Post
    if ([string]::IsNullOrWhiteSpace($html)) {
        return [ordered]@{}
    }

    $regexOptions = [System.Text.RegularExpressions.RegexOptions]::Singleline -bor [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
    $metadata = [ordered]@{}

    $titleMatch = [System.Text.RegularExpressions.Regex]::Match($html, '<title[^>]*>(?<title>.*?)</title>', $regexOptions)
    if ($titleMatch.Success) {
        $rawTitle = [System.Net.WebUtility]::HtmlDecode($titleMatch.Groups['title'].Value).Trim()
        if (-not [string]::IsNullOrWhiteSpace($rawTitle)) {
            $metadata.seoTitle = $rawTitle
        }
    }

    $metaTagPatterns = @(
        '<meta[^>]*\bname\s*=\s*"description"[^>]*>',
        '<meta[^>]*\bproperty\s*=\s*"og:description"[^>]*>'
    )

    foreach ($tagPattern in $metaTagPatterns) {
        $tagMatch = [System.Text.RegularExpressions.Regex]::Match($html, $tagPattern, $regexOptions)
        if (-not $tagMatch.Success) {
            continue
        }

        $contentMatch = [System.Text.RegularExpressions.Regex]::Match($tagMatch.Value, 'content\s*=\s*"(?<desc>.*?)"', $regexOptions)
        if (-not $contentMatch.Success) {
            continue
        }

        $rawDescription = [System.Net.WebUtility]::HtmlDecode($contentMatch.Groups['desc'].Value).Trim()
        if (-not [string]::IsNullOrWhiteSpace($rawDescription)) {
            $metadata.seoDescription = $rawDescription
            break
        }
    }

    return $metadata
}

function Get-PageHeadingFromReportPage {
    param([object]$Post)

    $html = Get-ReportPageHtml -Post $Post
    if ([string]::IsNullOrWhiteSpace($html)) {
        return $null
    }

    $regexOptions = [System.Text.RegularExpressions.RegexOptions]::Singleline -bor [System.Text.RegularExpressions.RegexOptions]::IgnoreCase

    # Prefer the first H1 on the page as the content heading.
    $h1Match = [System.Text.RegularExpressions.Regex]::Match($html, '<h1[^>]*>(?<heading>.*?)</h1>', $regexOptions)
    if ($h1Match.Success) {
        $decodedHeading = [System.Net.WebUtility]::HtmlDecode($h1Match.Groups['heading'].Value)
        $cleanHeading = [System.Text.RegularExpressions.Regex]::Replace($decodedHeading, '<[^>]+>', ' ')
        $cleanHeading = [System.Text.RegularExpressions.Regex]::Replace($cleanHeading, '\s+', ' ').Trim()

        if (-not [string]::IsNullOrWhiteSpace($cleanHeading)) {
            return $cleanHeading
        }
    }

    return $null
}

function Normalize-ExtractedBodyHtml {
    param([string]$Html)

    if ([string]::IsNullOrWhiteSpace($Html)) {
        return $null
    }

    $clean = $Html.Trim()

    # Prefer real paragraph content if present.
    $pMatches = [System.Text.RegularExpressions.Regex]::Matches(
        $clean,
        '<p\b[^>]*>.*?</p>',
        [System.Text.RegularExpressions.RegexOptions]::Singleline -bor [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)

    if ($pMatches.Count -gt 0) {
        return (($pMatches | ForEach-Object { $_.Value.Trim() }) -join "`n`n")
    }

    # Fall back to plain text with tags stripped.
    $textOnly = [System.Text.RegularExpressions.Regex]::Replace($clean, '<[^>]+>', ' ')
    $textOnly = [System.Text.RegularExpressions.Regex]::Replace($textOnly, '\s+', ' ').Trim()
    return $textOnly
}

function Get-PreferredWordPressDate {
    param(
        [Parameter(Mandatory = $true)][object]$Post,
        [Parameter(Mandatory = $true)][string[]]$CandidateProperties
    )

    foreach ($propertyName in $CandidateProperties) {
        if (-not ($Post.PSObject.Properties.Name -contains $propertyName)) {
            continue
        }

        $raw = [string]$Post.$propertyName
        if (-not [string]::IsNullOrWhiteSpace($raw)) {
            return $raw
        }
    }

    return $null
}

function Convert-PostToMigrationItem {
    param(
        [Parameter(Mandatory = $true)][object]$Post,
        [Parameter(Mandatory = $true)][bool]$IncludeBody,
        [Parameter(Mandatory = $true)][bool]$Publish
    )

    $title = [System.Net.WebUtility]::HtmlDecode([string]$Post.title.rendered).Trim()
    $slug = [string]$Post.slug
    $imageUrl = Get-FeaturedImageUrl -Post $Post

    $values = [ordered]@{}
    $values.title = $title

    $pageHeading = Get-PageHeadingFromReportPage -Post $Post
    if (-not [string]::IsNullOrWhiteSpace($pageHeading)) {
        $values.pageHeading = $pageHeading
    }
    else {
        $values.pageHeading = $title
    }

    $publishedSource = Get-PreferredWordPressDate -Post $Post -CandidateProperties @("date_gmt", "date")
    if (-not [string]::IsNullOrWhiteSpace($publishedSource)) {
        $publishedDate = [System.DateTimeOffset]::MinValue
        if ([System.DateTimeOffset]::TryParse($publishedSource, [ref]$publishedDate)) {
            $values.publishDate = $publishedDate.ToString("yyyy-MM-dd")
            $values.publishedAtUtc = $publishedDate.UtcDateTime.ToString("o")
        }
        else {
            $values.publishDate = $publishedSource
        }
    }

    $modifiedSource = Get-PreferredWordPressDate -Post $Post -CandidateProperties @("modified_gmt", "modified")
    if (-not [string]::IsNullOrWhiteSpace($modifiedSource)) {
        $modifiedDate = [System.DateTimeOffset]::MinValue
        if ([System.DateTimeOffset]::TryParse($modifiedSource, [ref]$modifiedDate)) {
            $values.modifiedAtUtc = $modifiedDate.UtcDateTime.ToString("o")
        }
        else {
            $values.modifiedAtUtc = $modifiedSource
        }
    }

    $seoMetadata = Get-SeoMetadataFromReportPage -Post $Post
    if ($seoMetadata.Contains('seoTitle')) {
        $values.seoTitle = [string]$seoMetadata.seoTitle
    }
    if ($seoMetadata.Contains('seoDescription')) {
        $values.seoDescription = [string]$seoMetadata.seoDescription
    }

    if ($IncludeBody) {
        $rawBody = ""
        if ($null -ne $Post.content -and $null -ne $Post.content.rendered) {
            $rawBody = [string]$Post.content.rendered
        }

        if ([string]::IsNullOrWhiteSpace($rawBody)) {
            $rawBody = Get-BodyHtmlFromReportPage -Post $Post
        }

        if (-not [string]::IsNullOrWhiteSpace($rawBody)) {
            $values.body = $rawBody
        }
        else {
            $values.body = Get-FallbackBodyHtml -Post $Post
        }
    }

    if ($IncludeBody -and $null -ne $Post.excerpt -and $null -ne $Post.excerpt.rendered) {
        $values.excerpt = [string]$Post.excerpt.rendered
    }

    if (-not [string]::IsNullOrWhiteSpace($imageUrl)) {
        $values.featuredImage = $imageUrl
        $values.heroImage = $imageUrl
        $values.coverImage = $imageUrl
        $values.thumbnail = $imageUrl
    }

    return [ordered]@{
        wordPressType = "reports"
        contentTypeAlias = "reportPage"
        name = $title
        slug = $slug
        values = $values
        publish = $Publish
    }
}

$baseEndpoint = Join-Url -Base $BaseUrl -Path "wp-json/wp/v2/reports"
$page = 1
$items = New-Object System.Collections.Generic.List[object]

while ($true) {
    $url = "{0}?per_page={1}&page={2}&_embed" -f $baseEndpoint, $PerPage, $page
    Write-Host "Fetching $url"

    try {
        $response = Invoke-RestMethod -Uri $url -Method Get
    }
    catch {
        if ($_.Exception.Response -and $_.Exception.Response.StatusCode.value__ -eq 400) {
            break
        }

        throw
    }

    if ($null -eq $response -or $response.Count -eq 0) {
        break
    }

    foreach ($post in $response) {
        $item = Convert-PostToMigrationItem -Post $post -IncludeBody:$IncludeBody -Publish:$Publish
        $items.Add($item)

        if ($MaxItems -gt 0 -and $items.Count -ge $MaxItems) {
            break
        }
    }

    if ($MaxItems -gt 0 -and $items.Count -ge $MaxItems) {
        break
    }

    $page++
}

$targetDir = Split-Path -Path $OutFile -Parent
if (-not (Test-Path -Path $targetDir)) {
    New-Item -ItemType Directory -Path $targetDir | Out-Null
}

$json = $items | ConvertTo-Json -Depth 20
Set-Content -Path $OutFile -Value $json -Encoding UTF8

Write-Host "Wrote $($items.Count) report items to $OutFile"
