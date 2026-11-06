<#
Cut a release: turn "## [Unreleased]" in CHANGELOG.md into "## [X.Y.Z] - date", commit it and tag vX.Y.Z.
It does NOT push; check, then:  git push origin main vX.Y.Z

    .\tools\release.ps1 1.0.1
#>
param([Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Not a X.Y.Z version: '$Version'" }
Set-Location (Join-Path $PSScriptRoot '..')

if (git status --porcelain) { throw 'Working tree is not clean: commit or stash first.' }
if (git tag -l "v$Version") { throw "Tag v$Version already exists." }

$file = 'CHANGELOG.md'
$text = [IO.File]::ReadAllText((Resolve-Path $file))
$m = [regex]::Match($text, '(?ms)^## \[Unreleased\]\s*\r?\n(?<body>.*?)(?=^## \[|\z)')
if (-not $m.Success) { throw 'No "## [Unreleased]" section in CHANGELOG.md.' }
if ([string]::IsNullOrWhiteSpace($m.Groups['body'].Value)) { throw '[Unreleased] is empty: write what changed first.' }

$date = Get-Date -Format 'yyyy-MM-dd'
$nl = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
$text = $text.Remove($m.Index, $m.Length).Insert($m.Index,
    "## [Unreleased]$nl$nl## [$Version] - $date$nl" + $m.Groups['body'].Value.TrimStart("`r", "`n"))
[IO.File]::WriteAllText((Join-Path (Get-Location) $file), $text, (New-Object Text.UTF8Encoding $false))

git add $file
git commit -m "release: $Version"
git tag -a "v$Version" -m "v$Version"
Write-Host "Done. Check with 'git show', then: git push origin main v$Version"
