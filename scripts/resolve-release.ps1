param(
    [Parameter(Mandatory)]
    [ValidatePattern('^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')]
    [string]$Tag,

    [Parameter()]
    [string]$Repository = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = "Stop"
$commit = git -C $Repository rev-parse --verify HEAD
if ($LASTEXITCODE -ne 0) { throw "Cannot read the checkout commit." }
$tagCommit = git -C $Repository rev-parse --verify --quiet "refs/tags/$Tag^{commit}"
if ($LASTEXITCODE -ne 0 -or $tagCommit -ne $commit) {
    throw "Release tag $Tag must point to the checked-out commit."
}
$changes = git -C $Repository status --porcelain --untracked-files=normal
if ($LASTEXITCODE -ne 0 -or $changes) {
    throw "Release builds require a clean checkout, including submodules and untracked files."
}

[pscustomobject]@{
    Tag = $Tag
    Version = $Tag.Substring(1)
    Commit = $commit
}
