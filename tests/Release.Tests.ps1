# No external test framework: exercise the release checks in a disposable Git repo.
$ErrorActionPreference = 'Stop'
$resolver = Join-Path $PSScriptRoot '../scripts/resolve-release.ps1'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ("nila-release-tests-" + [guid]::NewGuid())
$null = New-Item -ItemType Directory -Path $fixture

function Invoke-FixtureGit {
    & git -C $fixture @args | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Fixture Git command failed: $args" }
}

function Assert-Rejected([string]$Tag, [string]$Message) {
    try {
        $null = & $resolver -Tag $Tag -Repository $fixture
    } catch {
        if ($_.Exception.Message -notlike "*$Message*") { throw }
        return
    }
    throw "Expected release $Tag to be rejected."
}

try {
    Invoke-FixtureGit init --quiet
    Invoke-FixtureGit config core.hooksPath (Join-Path $fixture 'no-hooks')
    Set-Content -LiteralPath (Join-Path $fixture 'source.txt') -Value 'original'
    Invoke-FixtureGit add source.txt
    Invoke-FixtureGit -c user.name=Test -c user.email=test@example.invalid -c commit.gpgsign=false commit --quiet -m initial
    Invoke-FixtureGit tag v0.5.2

    $release = & $resolver -Tag v0.5.2 -Repository $fixture
    if ($release.Version -ne '0.5.2' -or $release.Commit -ne (git -C $fixture rev-parse HEAD)) {
        throw 'The tag version and checkout commit must be preserved.'
    }
    Assert-Rejected v0.05.2 'does not match'
    Assert-Rejected v9.9.9 'must point to'

    Set-Content -LiteralPath (Join-Path $fixture 'source.txt') -Value 'modified'
    Assert-Rejected v0.5.2 'clean checkout'
    Invoke-FixtureGit add source.txt
    Assert-Rejected v0.5.2 'clean checkout'
    Invoke-FixtureGit -c user.name=Test -c user.email=test@example.invalid -c commit.gpgsign=false commit --quiet -m changed
    Assert-Rejected v0.5.2 'must point to'
    Invoke-FixtureGit tag v0.5.3

    Set-Content -LiteralPath (Join-Path $fixture 'untracked.txt') -Value 'untracked'
    Assert-Rejected v0.5.3 'clean checkout'
    Write-Output 'Passed: release version, commit, invalid tag, missing tag, wrong commit, unstaged, staged, and untracked changes.'
} finally {
    $resolvedFixture = [IO.Path]::GetFullPath($fixture)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedFixture.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $resolvedFixture) -notlike 'nila-release-tests-*') {
        throw 'Refusing to remove a fixture outside the temporary directory.'
    }
    Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
}
