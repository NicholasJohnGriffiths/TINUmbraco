param(
    [string]$BaseUrl = "https://tin100.com",
    [int]$PerPage = 100,
    [int]$MaxItems = 0,
    [string]$OutFile = "d:\Dev\TINUmbraco\src\TINUmbraco.Web\migration-data.membership.from-api.json",
    [switch]$Publish
)

$ErrorActionPreference = "Stop"
$script:DirectoryPageHtmlCache = @{}

function Get-DirectoryPageHtml {
    param([object]$Item)

    $link = [string]$Item.link
    if ([string]::IsNullOrWhiteSpace($link)) {
        return $null
    }

    if ($script:DirectoryPageHtmlCache.ContainsKey($link)) {
        return [string]$script:DirectoryPageHtmlCache[$link]
    }

    try {
        $response = Invoke-WebRequest -Uri $link -Method Get -UseBasicParsing -TimeoutSec 30
        $rawBytes = $response.RawContentStream.ToArray()
        $html = [System.Text.Encoding]::UTF8.GetString($rawBytes)
        $script:DirectoryPageHtmlCache[$link] = $html
        return $html
    }
    catch {
        return $null
    }
}

function Get-SeoTitleFromPage {
    param([string]$Html)

    if ([string]::IsNullOrWhiteSpace($Html)) {
        return $null
    }

    $regexOptions = [System.Text.RegularExpressions.RegexOptions]::Singleline -bor [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
    
    # Try to find title from meta tags (og:title or standard title)
    $patterns = @(
        '<meta\s+property="og:title"\s+content="(?<title>[^"]+)"',
        '<meta\s+name="title"\s+content="(?<title>[^"]+)"',
        '<title[^>]*>(?<title>[^<]+)</title>'
    )

    foreach ($pattern in $patterns) {
        $match = [System.Text.RegularExpressions.Regex]::Match($Html, $pattern, $regexOptions)
        if ($match.Success) {
            $title = [System.Net.WebUtility]::HtmlDecode($match.Groups['title'].Value).Trim()
            # Remove " | TIN" suffix if present
            $title = $title -replace '\s*\|\s*TIN.*$', ''
            if (-not [string]::IsNullOrWhiteSpace($title)) {
                return $title
            }
        }
    }

    return $null
}

function Normalize-ExtractedText {
    param([string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return $Text
    }

    $normalized = $Text

    # Replace common mojibake sequences without embedding non-ASCII literals directly.
    $mojibakeRightSingleQuote = ([string][char]0x00E2) + [char]0x20AC + [char]0x2122
    $mojibakeLeftSingleQuote = ([string][char]0x00E2) + [char]0x20AC + [char]0x02DC
    $mojibakeLeftDoubleQuote = ([string][char]0x00E2) + [char]0x20AC + [char]0x0153
    $mojibakeRightDoubleQuote = ([string][char]0x00E2) + [char]0x20AC + [char]0x009D
    $mojibakeEnDash = ([string][char]0x00E2) + [char]0x20AC + [char]0x201C
    $mojibakeEmDash = ([string][char]0x00E2) + [char]0x20AC + [char]0x201D
    $mojibakeC2 = [string][char]0x00C2

    $normalized = $normalized.Replace($mojibakeRightSingleQuote, "'")
    $normalized = $normalized.Replace($mojibakeLeftSingleQuote, "'")
    $normalized = $normalized.Replace($mojibakeLeftDoubleQuote, '"')
    $normalized = $normalized.Replace($mojibakeRightDoubleQuote, '"')
    $normalized = $normalized.Replace($mojibakeEnDash, '-')
    $normalized = $normalized.Replace($mojibakeEmDash, '-')
    $normalized = $normalized.Replace($mojibakeC2, '')
    return $normalized.Trim()
}

function Get-AboutFromPage {
    param([string]$Html)

    if ([string]::IsNullOrWhiteSpace($Html)) {
        return $null
    }

    $regexOptions = [System.Text.RegularExpressions.RegexOptions]::Singleline -bor [System.Text.RegularExpressions.RegexOptions]::IgnoreCase

    # Directory pages render the main description inside an x-text x-content block.
    $match = [System.Text.RegularExpressions.Regex]::Match(
        $Html,
        '<div\s+class="x-text\s+x-content[^\"]*"[^>]*>(?<content>.*?)</div>',
        $regexOptions)

    if (-not $match.Success) {
        return $null
    }

    $raw = [string]$match.Groups['content'].Value
    $text = [System.Text.RegularExpressions.Regex]::Replace($raw, '<[^>]+>', ' ')
    $text = [System.Net.WebUtility]::HtmlDecode($text)
    $text = [System.Text.RegularExpressions.Regex]::Replace($text, '\s+', ' ').Trim()

    if ([string]::IsNullOrWhiteSpace($text)) {
        return $null
    }

    return (Normalize-ExtractedText -Text $text)
}

function Get-WebsiteFromPage {
    param([string]$Html)

    if ([string]::IsNullOrWhiteSpace($Html)) {
        return $null
    }

    $regexOptions = [System.Text.RegularExpressions.RegexOptions]::Singleline -bor [System.Text.RegularExpressions.RegexOptions]::IgnoreCase

    # Prefer website link in the "Find Us Online" section.
    $match = [System.Text.RegularExpressions.Regex]::Match(
        $Html,
        'Find\s+Us\s+Online</h3>.*?<a\s+href="(?<url>https?://[^\"]+)"',
        $regexOptions)

    if ($match.Success) {
        $url = [string]$match.Groups['url'].Value
        if (-not [string]::IsNullOrWhiteSpace($url)) {
            return $url.Trim()
        }
    }

    return $null
}

function Get-LogoFromPage {
    param(
        [string]$Html,
        [string]$CompanyName
    )

    if ([string]::IsNullOrWhiteSpace($Html)) {
        return $null
    }

    $regexOptions = [System.Text.RegularExpressions.RegexOptions]::Singleline -bor [System.Text.RegularExpressions.RegexOptions]::IgnoreCase

    $logoMatches = [System.Text.RegularExpressions.Regex]::Matches(
        $Html,
        '<img[^>]*src="(?<src>https?://[^\"]+)"[^>]*alt="(?<alt>[^\"]*)"',
        $regexOptions)

    foreach ($m in $logoMatches) {
        $src = [string]$m.Groups['src'].Value
        $alt = [string]$m.Groups['alt'].Value

        if ($alt -notmatch 'logo') {
            continue
        }

        if ($src -match 'TIN-Logo|/TIN-Logo|tin-logo') {
            continue
        }

        if ($alt -match '\bTIN\b') {
            continue
        }

        if (-not [string]::IsNullOrWhiteSpace($CompanyName) -and $alt -match [System.Text.RegularExpressions.Regex]::Escape($CompanyName)) {
            return $src
        }
    }

    foreach ($m in $logoMatches) {
        $src = [string]$m.Groups['src'].Value
        $alt = [string]$m.Groups['alt'].Value
        if ($alt -match 'logo' -and $src -notmatch 'TIN-Logo|/TIN-Logo|tin-logo') {
            return $src
        }
    }

    # Fallback to first image in the page body.
    $imgMatch = [System.Text.RegularExpressions.Regex]::Match(
        $Html,
        '<img[^>]*src="(?<src>https?://[^\"]+)"',
        $regexOptions)

    if ($imgMatch.Success) {
        return [string]$imgMatch.Groups['src'].Value
    }

    return $null
}

function Get-AllDirectoryItems {
    $allItems = [System.Collections.Generic.List[object]]::new()
    $page = 1

    while ($true) {
        $url = "$BaseUrl/wp-json/wp/v2/directory?per_page=$PerPage&page=$page&status=publish&_embed=wp:featuredmedia"
        Write-Host "Fetching page ${page}: $url"

        try {
            $response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 60
        }
        catch {
            if ($_.Exception.Response -and $_.Exception.Response.StatusCode -eq 400) {
                Write-Host "Page $page returned 400 - no more pages."
                break
            }
            throw
        }

        $items = $response.Content | ConvertFrom-Json
        if ($null -eq $items -or $items.Count -eq 0) {
            break
        }

        foreach ($item in $items) {
            $allItems.Add($item)
        }

        Write-Host "  Fetched $($items.Count) items (total so far: $($allItems.Count))"

        if ($MaxItems -gt 0 -and $allItems.Count -ge $MaxItems) {
            Write-Host "Reached MaxItems limit of $MaxItems."
            break
        }

        $totalPages = [int]$response.Headers['X-WP-TotalPages']
        if ($page -ge $totalPages) {
            break
        }

        $page++
    }

    return $allItems
}

function Get-FeaturedImageUrl {
    param([object]$Item)

    if ($null -eq $Item._embedded) {
        return $null
    }

    $mediaList = $Item._embedded.'wp:featuredmedia'
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

function ConvertTo-MigrationItem {
    param([object]$Item)

    $title = [System.Net.WebUtility]::HtmlDecode([string]$Item.title.rendered).Trim()
    $slug = [string]$Item.slug

    # Fetch page HTML for additional data extraction
    $html = Get-DirectoryPageHtml -Item $Item

    # Map fields to Umbraco property aliases
    $values = [ordered]@{}

    # Company Name (use title)
    if (-not [string]::IsNullOrWhiteSpace($title)) {
        $values.'Company Name' = $title
    }

    # Populate description/website/logo from directory page HTML when available.
    $about = Get-AboutFromPage -Html $html
    if (-not [string]::IsNullOrWhiteSpace($about)) {
        $values.'About' = (Normalize-ExtractedText -Text $about)
    }

    $website = Get-WebsiteFromPage -Html $html
    if (-not [string]::IsNullOrWhiteSpace($website)) {
        $values.'Website' = $website
    }

    $logo = Get-LogoFromPage -Html $html -CompanyName $title
    if (-not [string]::IsNullOrWhiteSpace($logo)) {
        $values.'Company Logo - Full Color' = $logo
    }

    # SEO Title - extract from page if available
    if (-not [string]::IsNullOrWhiteSpace($html)) {
        $seoTitle = Get-SeoTitleFromPage -Html $html
        if (-not [string]::IsNullOrWhiteSpace($seoTitle)) {
            $values.'seoTitle' = $seoTitle
        }
    }

    # Company Type from taxonomy
    if ($null -ne $Item.company_type -and $Item.company_type.Count -gt 0) {
        try {
            $termId = $Item.company_type[0]
            $termResp = Invoke-WebRequest -Uri "https://tin100.com/wp-json/wp/v2/company_type/$termId" -UseBasicParsing -TimeoutSec 10
            $term = $termResp.Content | ConvertFrom-Json
            if ($null -ne $term -and -not [string]::IsNullOrWhiteSpace([string]$term.name)) {
                $values.'Company Type' = [string]$term.name
            }
        }
        catch {
            # Silently skip if term fetch fails
        }
    }

    return [ordered]@{
        WordPressType    = "directory"
        ContentTypeAlias = "memberPage"
        Name             = $title
        Slug             = $slug
        Values           = $values
        Publish          = [bool]$Publish
    }
}

# --- Main ---

Write-Host "Fetching all published directory items from $BaseUrl..."
$items = Get-AllDirectoryItems

if ($MaxItems -gt 0 -and $items.Count -gt $MaxItems) {
    $items = $items | Select-Object -First $MaxItems
}

Write-Host "Converting $($items.Count) items to migration format..."
$migrationItems = [System.Collections.Generic.List[object]]::new()
$itemIndex = 0
foreach ($item in $items) {
    $itemIndex++
    if ($itemIndex % 10 -eq 0) {
        Write-Host "  [$itemIndex/$($items.Count)] Processing company types..."
    }
    $migrationItem = ConvertTo-MigrationItem -Item $item
    $migrationItems.Add($migrationItem)
}

Write-Host "Writing $($migrationItems.Count) items to: $OutFile"
$json = $migrationItems | ConvertTo-Json -Depth 10
[System.IO.File]::WriteAllText($OutFile, $json, [System.Text.UTF8Encoding]::new($false))

Write-Host "Done. Output: $OutFile"
Write-Host "Total items exported: $($migrationItems.Count)"
