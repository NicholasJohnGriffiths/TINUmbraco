param(
    [string]$BaseUrl = "https://tin100.com",
    [int]$PerPage = 100,
    [int]$MaxItems = 0,
    [string]$OutFile = "d:\Dev\TINUmbraco\src\TINUmbraco.Web\migration-data.news.from-api.json",
    [switch]$IncludeBody,
    [switch]$Publish
)

$ErrorActionPreference = "Stop"
$script:NewsPageHtmlCache = @{}

function Get-PostPageHtml {
    param([object]$Post)

    $link = [string]$Post.link
    if ([string]::IsNullOrWhiteSpace($link)) {
        return $null
    }

    if ($script:NewsPageHtmlCache.ContainsKey($link)) {
        return [string]$script:NewsPageHtmlCache[$link]
    }

    try {
        $response = Invoke-WebRequest -Uri $link -Method Get -UseBasicParsing -TimeoutSec 60
        # Read raw bytes and decode as UTF-8 directly, bypassing PowerShell's charset misdetection
        # RawContentStream contains only the response body bytes (no headers)
        $rawBytes = $response.RawContentStream.ToArray()
        $html = [System.Text.Encoding]::UTF8.GetString($rawBytes)
        $script:NewsPageHtmlCache[$link] = $html
        return $html
    }
    catch {
        Write-Warning "Failed to fetch news page HTML for '$link': $($_.Exception.Message)"
        return $null
    }
}

function Get-BodyHtmlFromNewsPage {
    param([object]$Post)

    $html = Get-PostPageHtml -Post $Post
    if ([string]::IsNullOrWhiteSpace($html)) {
        return $null
    }

    $regexOptions = [System.Text.RegularExpressions.RegexOptions]::Singleline -bor [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
    $matches = [System.Text.RegularExpressions.Regex]::Matches(
        $html,
        '<div class="x-text x-content[^\"]*">\s*(?<body>.*?)\s*</div>',
        $regexOptions)

    $bestBody = $null
    $bestScore = -1

    foreach ($match in $matches) {
        $candidate = [string]$match.Groups['body'].Value
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            continue
        }

        $paragraphCount = [System.Text.RegularExpressions.Regex]::Matches($candidate, '<p\b', $regexOptions).Count
        if ($paragraphCount -le 0) {
            continue
        }

        # Prefer content blocks with substantial paragraph content.
        $score = ($paragraphCount * 1000) + $candidate.Length
        if ($score -gt $bestScore) {
            $bestScore = $score
            $bestBody = $candidate
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($bestBody)) {
        return $bestBody.Trim()
    }

    return $null
}

function Get-SeoMetadataFromNewsPage {
    param([object]$Post)

    $html = Get-PostPageHtml -Post $Post
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

function Get-FeaturedImageUrl {
    param([object]$Post)

    if ($null -eq $Post._embedded) {
        return $null
    }

    $mediaList = $Post._embedded.'wp:featuredmedia'
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

function Get-AllPosts {
    $allPosts = [System.Collections.Generic.List[object]]::new()
    $page = 1

    while ($true) {
        $url = "$BaseUrl/wp-json/wp/v2/posts?per_page=$PerPage&page=$page&status=publish&_embed=wp:featuredmedia"
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

        $posts = $response.Content | ConvertFrom-Json
        if ($null -eq $posts -or $posts.Count -eq 0) {
            break
        }

        foreach ($post in $posts) {
            $allPosts.Add($post)
        }

        Write-Host "  Fetched $($posts.Count) posts (total so far: $($allPosts.Count))"

        if ($MaxItems -gt 0 -and $allPosts.Count -ge $MaxItems) {
            Write-Host "Reached MaxItems limit of $MaxItems."
            break
        }

        $totalPages = [int]$response.Headers['X-WP-TotalPages']
        if ($page -ge $totalPages) {
            break
        }

        $page++
    }

    return $allPosts
}

function ConvertTo-MigrationItem {
    param([object]$Post)

    $title = [System.Net.WebUtility]::HtmlDecode([string]$Post.title.rendered).Trim()
    $slug = [string]$Post.slug

    $values = [ordered]@{
        title       = $title
        publishDate = [string]$Post.date_gmt
        publishedAtUtc = [string]$Post.date_gmt
        modifiedAtUtc  = [string]$Post.modified_gmt
    }

    # Excerpt / summary
    if ($null -ne $Post.excerpt -and $null -ne $Post.excerpt.rendered) {
        $excerpt = [System.Net.WebUtility]::HtmlDecode([string]$Post.excerpt.rendered).Trim()
        # Strip wrapping <p> tags for a plain summary
        $excerpt = [System.Text.RegularExpressions.Regex]::Replace($excerpt, '^\s*<p>(.*?)</p>\s*$', '$1',
            [System.Text.RegularExpressions.RegexOptions]::Singleline)
        if (-not [string]::IsNullOrWhiteSpace($excerpt)) {
            $excerpt = $excerpt.Replace([char]0x00A0, ' ')
            $excerpt = $excerpt.Replace([string][char]0x200B, '')
            $values.summary = $excerpt
            $values.excerpt = $excerpt
        }
    }

    # Body content
    if ($IncludeBody) {
        $body = $null
        if ($null -ne $Post.content -and $null -ne $Post.content.rendered) {
            $body = [string]$Post.content.rendered
        }

        if ([string]::IsNullOrWhiteSpace($body)) {
            $body = Get-BodyHtmlFromNewsPage -Post $Post
        }

        if (-not [string]::IsNullOrWhiteSpace($body)) {
            # Normalize HTML entities and invisible/replacement Unicode chars
            $body = $body.Replace('&nbsp;', ' ')              # HTML non-breaking space entity
            $body = $body.Replace([char]0x00A0, ' ')          # non-breaking space (U+00A0)
            $body = $body.Replace([string][char]0xFFFD, ' ')  # Unicode replacement char (encoding corruption) - use space to preserve word boundaries
            $body = $body.Replace([string][char]0x200B, '')   # zero-width space
            $body = $body.Replace([string][char]0x200C, '')   # zero-width non-joiner
            $body = $body.Replace([string][char]0x200D, '')   # zero-width joiner
            $values.body = $body
        }
    }

    # SEO from page HTML (Yoast not available via API)
    $seoMetadata = Get-SeoMetadataFromNewsPage -Post $Post
    if ($seoMetadata.Contains('seoTitle')) {
        $values.seoTitle = [string]$seoMetadata.seoTitle
    }
    if ($seoMetadata.Contains('seoDescription')) {
        $values.seoDescription = [string]$seoMetadata.seoDescription
    }

    # Featured image
    $imageUrl = Get-FeaturedImageUrl -Post $Post
    if (-not [string]::IsNullOrWhiteSpace($imageUrl)) {
        $values.featuredImage = $imageUrl
        $values.heroImage     = $imageUrl
        $values.coverImage    = $imageUrl
        $values.thumbnail     = $imageUrl
    }

    return [ordered]@{
        wordPressType    = "post"
        contentTypeAlias = "newsItemPage"
        name             = $title
        slug             = $slug
        values           = $values
        publish          = [bool]$Publish
    }
}

# --- Main ---

Write-Host "Fetching all published posts from $BaseUrl..."
$posts = Get-AllPosts

if ($MaxItems -gt 0 -and $posts.Count -gt $MaxItems) {
    $posts = $posts | Select-Object -First $MaxItems
}

Write-Host "Converting $($posts.Count) posts to migration items..."
$items = $posts | ForEach-Object { ConvertTo-MigrationItem -Post $_ }

Write-Host "Writing $($items.Count) items to: $OutFile"
$json = $items | ConvertTo-Json -Depth 10
[System.IO.File]::WriteAllText($OutFile, $json, [System.Text.UTF8Encoding]::new($false))

Write-Host "Done. Output: $OutFile"
