<#
.SYNOPSIS
    Tags the current commit with its Nerdbank.GitVersioning version and pushes the tag,
    which triggers the CI release job that packs and pushes EdgeHop to nuget.org.

.DESCRIPTION
    The package version is NOT chosen here. Nerdbank.GitVersioning derives it from
    version.json plus git height (commits since version.json last changed), so the tag is
    named after the version the commit already computes to. That is the whole point of
    running 'nbgv tag' rather than picking a tag by hand: the git tag and the published
    package version stay one-to-one.

    Steps performed:
      1. Verify the nbgv CLI is available (installs it as a global tool if missing).
      2. Verify the working tree is clean and on a public-release branch (main/master).
      3. Verify the local branch is in sync with its remote.
      4. Report the version this commit will publish as, and refuse if that version is
         already tagged.
      5. Tag the commit via 'nbgv tag' and push the tag.

    CI (.github/workflows/ci.yml) takes over from the tag: it builds, tests, packs with
    -p:PublicRelease=true, and pushes to nuget.org via NuGet trusted publishing (OIDC).
    No API key is needed locally.

    To move to a new version line (0.1-alpha -> 0.1-beta, or -> 0.2), run
    'nbgv prepare-release' and push that commit BEFORE running this script; it resets git
    height to 0 so the next publish starts at .0.

.PARAMETER WhatIf
    Run every check and report the version and tag name, but do not tag or push.

.PARAMETER Remote
    Git remote to push the tag to. Defaults to 'origin'.

.EXAMPLE
    .\Deployment\Publish.ps1 -WhatIf
    Shows what would be published without touching the repo.

.EXAMPLE
    .\Deployment\Publish.ps1
    Tags HEAD and pushes, triggering the nuget.org release.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $Remote = 'origin'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Run from the repo root regardless of where the script was invoked from.
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    function Assert-LastExitCode {
        param([string] $What)
        if ($LASTEXITCODE -ne 0) { throw "$What failed with exit code $LASTEXITCODE." }
    }

    # --- 1. nbgv CLI -------------------------------------------------------------------
    # Build-time Nerdbank.GitVersioning (PrivateAssets=all in Directory.Packages.props) does
    # not give us a CLI, so the global tool is a separate machine-local install.
    if (-not (Get-Command nbgv -ErrorAction SilentlyContinue)) {
        Write-Host 'nbgv CLI not found; installing it as a .NET global tool...' -ForegroundColor Yellow
        dotnet tool install -g nbgv
        Assert-LastExitCode 'dotnet tool install -g nbgv'

        $toolPath = Join-Path $env:USERPROFILE '.dotnet\tools'
        if (Test-Path $toolPath) { $env:PATH = "$toolPath;$env:PATH" }

        if (-not (Get-Command nbgv -ErrorAction SilentlyContinue)) {
            throw "nbgv installed but is not on PATH. Open a new shell and re-run, or add $toolPath to PATH."
        }
    }

    # --- 2. clean tree, public-release branch ------------------------------------------
    $dirty = git status --porcelain
    Assert-LastExitCode 'git status'
    if ($dirty) {
        throw "Working tree is not clean. Commit or stash before publishing:`n$($dirty -join [Environment]::NewLine)"
    }

    $branch = (git rev-parse --abbrev-ref HEAD).Trim()
    Assert-LastExitCode 'git rev-parse'
    # Must match publicReleaseRefs in version.json, or the package gets a local -gXXXX suffix.
    if ($branch -notin @('main', 'master')) {
        throw "On branch '$branch'. Publish only from main/master — publicReleaseRefs in version.json covers those, and anywhere else produces a -gXXXX suffixed version."
    }

    # --- 3. in sync with the remote ----------------------------------------------------
    Write-Host "Fetching $Remote..." -ForegroundColor Cyan
    git fetch $Remote --tags --quiet
    Assert-LastExitCode 'git fetch'

    $counts = (git rev-list --left-right --count "$Remote/$branch...HEAD").Trim() -split '\s+'
    Assert-LastExitCode 'git rev-list'
    $behind = [int] $counts[0]
    $ahead  = [int] $counts[1]
    if ($behind -gt 0) { throw "Local $branch is $behind commit(s) behind $Remote/$branch. Pull first." }
    if ($ahead  -gt 0) { throw "Local $branch is $ahead commit(s) ahead of $Remote/$branch. Push before tagging — CI builds the pushed commit." }

    # --- 4. the version this commit publishes as ---------------------------------------
    $version = nbgv get-version --format json | ConvertFrom-Json
    Assert-LastExitCode 'nbgv get-version'

    # PublicRelease=true (which CI passes) strips the -gXXXX suffix nbgv reports locally.
    $publicVersion = $version.SimpleVersion + '-' + $version.PrereleaseVersion.TrimStart('-')
    if (-not $version.PrereleaseVersion) { $publicVersion = $version.SimpleVersion }
    $tag = 'v' + $version.SimpleVersion

    Write-Host ''
    Write-Host "  Branch:          $branch @ $($version.GitCommitId.Substring(0,7))"
    Write-Host "  Tag to create:   $tag"
    Write-Host "  Publishes as:    $publicVersion" -ForegroundColor Green
    Write-Host ''

    $existing = git tag --list $tag
    Assert-LastExitCode 'git tag --list'
    if ($existing) {
        throw "Tag $tag already exists — this commit has already been released, or git height has not advanced. Land a commit on $branch first, or run 'nbgv prepare-release' to move to a new version line."
    }

    # --- 5. tag and push ---------------------------------------------------------------
    if (-not $PSCmdlet.ShouldProcess("$tag -> $Remote", 'Create and push release tag')) {
        Write-Host 'WhatIf: no tag created, nothing pushed.' -ForegroundColor Yellow
        return
    }

    nbgv tag
    Assert-LastExitCode 'nbgv tag'

    git push $Remote $tag
    Assert-LastExitCode "git push $Remote $tag"

    Write-Host ''
    Write-Host "Pushed $tag. CI is packing and will push $publicVersion to nuget.org." -ForegroundColor Green
    Write-Host 'Watch the run:  gh run watch'
}
finally {
    Pop-Location
}
