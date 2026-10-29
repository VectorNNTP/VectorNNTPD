#Requires -Version 5.1
<#
.SYNOPSIS
    Runs a local traced C# CodeQL analysis for this repository.

.DESCRIPTION
    Creates a CodeQL database by tracing the repository's Release x64 restore and
    non-incremental build, then analyzes that database with the queries selected by
    .github/codeql/codeql-config.yml. That config selects
    .github/codeql/csharp-with-alert-suppression.qls, which imports the
    security-extended and security-and-quality suites and adds AlertSuppression.ql.
    Analyze is invoked with no extra query arguments, so it uses the queries
    stored from that config at database creation. That is the same selection the
    GitHub Action analyzes with database run-queries. The traced build uses
    --no-incremental so MSBuild always runs CoreCompile/csc; an up-to-date
    incremental skip would leave project sources out of the database.

    The script checks that the local suite resolves to those two suites plus
    AlertSuppression.ql exactly once. It does not download query packs. Evidence
    is written under .artifacts/codeql/, which Git ignores. A successful database
    build is not treated as a finished analysis: the script also requires
    database analyze to exit 0.

    For a traced C# build, the CLI records that paths and paths-ignore do not
    filter compiled files.

.PARAMETER CodeQl
    Path to codeql.exe. Defaults to C:\Tools\codeql\codeql.exe.
#>
[CmdletBinding()]
param(
    [string] $CodeQl = "C:\Tools\codeql\codeql.exe"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Invoke-CodeQl {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Arguments,

        [Parameter(Mandatory = $true)]
        [string] $LogPath
    )

    # Run through cmd so CodeQL's stderr progress is captured in the log instead of
    # becoming a PowerShell error record. The cmd exit code is the CodeQL exit code.
    $renderedArguments = foreach ($argument in $Arguments) {
        if ($argument -match '[\s"]') {
            '"' + ($argument -replace '"', '\"') + '"'
        }
        else {
            $argument
        }
    }

    $commandLine = "`"$CodeQl`" $($renderedArguments -join ' ') > `"$LogPath`" 2>&1"
    & cmd.exe /c $commandLine
    $exitCode = $LASTEXITCODE
    [System.IO.File]::AppendAllText($LogPath, "EXIT_CODE=$exitCode$([Environment]::NewLine)")
    if ($exitCode -ne 0) {
        throw "codeql $($Arguments[0]) $($Arguments[1]) exited with code $exitCode. See $LogPath"
    }
}

if (-not (Test-Path -LiteralPath $CodeQl)) {
    throw "CodeQL CLI was not found at $CodeQl"
}

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$evidenceRoot = Join-Path $repoRoot ".artifacts\codeql"
$database = Join-Path $evidenceRoot "csharp"
$config = Join-Path $repoRoot ".github\codeql\codeql-config.yml"
$buildCommand = Join-Path $evidenceRoot "build.cmd"
New-Item -ItemType Directory -Force -Path $evidenceRoot | Out-Null

if (-not (Test-Path -LiteralPath $config)) {
    throw "CodeQL configuration was not found at $config"
}

# --no-incremental forces CoreCompile/csc so the CodeQL tracer sees every project.
# An up-to-date incremental skip leaves only the .csproj in the database (no .cs TRAPs).
@(
    "@echo off"
    "dotnet restore VectorNNTPD.slnx -p:Platform=x64"
    "if errorlevel 1 exit /b %errorlevel%"
    "dotnet build VectorNNTPD.slnx --configuration Release -p:Platform=x64 --no-restore --no-incremental"
    "if errorlevel 1 exit /b %errorlevel%"
) | Set-Content -LiteralPath $buildCommand -Encoding ascii

$previousErrorAction = $ErrorActionPreference
$ErrorActionPreference = "Continue"
try {
    & $CodeQl version | Set-Content -LiteralPath (Join-Path $evidenceRoot "codeql-version.txt") -Encoding utf8
    if ($LASTEXITCODE -ne 0) {
        throw "codeql version exited with code $LASTEXITCODE"
    }
}
finally {
    $ErrorActionPreference = $previousErrorAction
}

$suite = Join-Path $repoRoot ".github\codeql\csharp-with-alert-suppression.qls"
if (-not (Test-Path -LiteralPath $suite)) {
    throw "Query suite was not found at $suite"
}

function Get-ResolvedQueries {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Selectors
    )

    $output = & $CodeQl resolve queries --format=json @Selectors
    if ($LASTEXITCODE -ne 0) {
        throw "codeql resolve queries exited with code $LASTEXITCODE"
    }

    return @($output | ConvertFrom-Json)
}

$ErrorActionPreference = "Continue"
try {
    $configured = Get-ResolvedQueries -Selectors @($suite)
    $suitesOnly = Get-ResolvedQueries -Selectors @(
        "codeql/csharp-queries:codeql-suites/csharp-security-extended.qls",
        "codeql/csharp-queries:codeql-suites/csharp-security-and-quality.qls"
    )
}
finally {
    $ErrorActionPreference = "Stop"
}

$added = @($configured | Where-Object { $_ -notin $suitesOnly })
$removed = @($suitesOnly | Where-Object { $_ -notin $configured })
$suppression = @($added | Where-Object { $_ -like "*\AlertSuppression.ql" -or $_ -like "*/AlertSuppression.ql" })
if ($removed.Count -ne 0 -or $added.Count -ne 1 -or $suppression.Count -ne 1) {
    throw "The configured suite must equal security-extended plus security-and-quality plus AlertSuppression.ql once. Added=$($added.Count) Removed=$($removed.Count) Suppression=$($suppression.Count)"
}

if (Test-Path -LiteralPath $database) {
    Remove-Item -LiteralPath $database -Recurse -Force
}

Push-Location $repoRoot
try {
    Invoke-CodeQl -LogPath (Join-Path $evidenceRoot "database-create.log") -Arguments @(
        "database", "create", $database,
        "--language=csharp",
        "--source-root", $repoRoot,
        "--threads=0",
        "--codescanning-config", $config,
        "--command", $buildCommand
    )

    Invoke-CodeQl -LogPath (Join-Path $evidenceRoot "database-analyze.log") -Arguments @(
        "database", "analyze", $database,
        "--format=sarifv2.1.0",
        "--output", (Join-Path $evidenceRoot "results.sarif"),
        "--threads=0",
        "--no-sarif-minify",
        "--sarif-category=csharp"
    )
}
finally {
    Pop-Location
}
