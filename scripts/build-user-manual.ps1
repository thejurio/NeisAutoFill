param(
    [string]$TemplatePath,
    [string]$ScreenshotDirectory,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($TemplatePath)) {
    $templateCandidates = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'docs\guide') -Filter '*.template.html' -File)
    if ($templateCandidates.Count -ne 1) {
        throw "Expected exactly one *.template.html in docs\guide, found $($templateCandidates.Count)."
    }
    $TemplatePath = $templateCandidates[0].FullName
}
if ([string]::IsNullOrWhiteSpace($ScreenshotDirectory)) {
    $ScreenshotDirectory = Join-Path $repositoryRoot 'docs\manual_shots\v1.9'
}

$templateFullPath = [System.IO.Path]::GetFullPath($TemplatePath)
$imageRoot = [System.IO.Path]::GetFullPath($ScreenshotDirectory).TrimEnd('\') + '\'
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $outputCandidates = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'docs\guide') -Filter '*.html' -File |
        Where-Object { $_.Name -notlike '*.template.html' })
    if ($outputCandidates.Count -ne 1) {
        throw "Expected exactly one generated *.html in docs\guide, found $($outputCandidates.Count)."
    }
    $OutputPath = $outputCandidates[0].FullName
}
$outputFullPath = [System.IO.Path]::GetFullPath($OutputPath)

if (-not (Test-Path -LiteralPath $templateFullPath -PathType Leaf)) {
    throw "Manual template not found: $templateFullPath"
}
if (-not (Test-Path -LiteralPath $imageRoot -PathType Container)) {
    throw "Manual screenshot directory not found: $imageRoot"
}

$html = [System.IO.File]::ReadAllText($templateFullPath)
$imageToken = [regex]'\{\{image:([^}]+)\}\}'
$matches = $imageToken.Matches($html)
if ($matches.Count -eq 0) {
    throw 'The template has no {{image:filename}} tokens.'
}

$uniqueImages = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($match in $matches) {
    $relativeName = $match.Groups[1].Value.Trim()
    $imagePath = [System.IO.Path]::GetFullPath((Join-Path $imageRoot $relativeName))
    if (-not $imagePath.StartsWith($imageRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Image path escapes the screenshot directory: $relativeName"
    }
    if (-not (Test-Path -LiteralPath $imagePath -PathType Leaf)) {
        throw "Screenshot not found: $imagePath"
    }

    $extension = [System.IO.Path]::GetExtension($imagePath).ToLowerInvariant()
    $mime = switch ($extension) {
        '.png'  { 'image/png' }
        '.jpg'  { 'image/jpeg' }
        '.jpeg' { 'image/jpeg' }
        '.webp' { 'image/webp' }
        default { throw "Unsupported image format: $extension" }
    }

    $base64 = [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($imagePath))
    $html = $html.Replace($match.Value, "data:$mime;base64,$base64")
    $null = $uniqueImages.Add($relativeName)
}

if ($imageToken.IsMatch($html)) {
    throw 'Unresolved image tokens remain in the generated manual.'
}

$slideMatches = [regex]::Matches($html, '<section\s+class="slide(?:\s|\")', 'IgnoreCase')
$titleMatches = [regex]::Matches($html, '<section\s+class="slide[^>]*\sdata-title="[^"]+"', 'IgnoreCase')
if ($slideMatches.Count -ne $titleMatches.Count) {
    throw "Every slide needs data-title. slides=$($slideMatches.Count), titles=$($titleMatches.Count)"
}
if ($slideMatches.Count -lt 10) {
    throw "The manual has too few slides: $($slideMatches.Count)"
}

$outputDirectory = Split-Path -Parent $outputFullPath
[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($outputFullPath, $html, $utf8NoBom)

$size = (Get-Item -LiteralPath $outputFullPath).Length
Write-Host "Manual generated: $outputFullPath"
Write-Host "Slides=$($slideMatches.Count) Screenshots=$($uniqueImages.Count) Bytes=$size"
