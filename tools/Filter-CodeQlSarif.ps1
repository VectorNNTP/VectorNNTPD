#Requires -Version 5.1
<#
.SYNOPSIS
    Removes CodeQL SARIF results whose primary location is under bin/ or obj/.

.DESCRIPTION
    Traced C# CodeQL extraction includes compiler and source-generator output under
    **/obj/** and **/bin/**. Repository paths-ignore / query-filters do not drop those
    compiled files from analysis results (see Invoke-CodeQlAnalysis.ps1 notes and PR #256).
    This filter keeps first-party alerts and in-source AlertSuppression markings while
    discarding generated-code noise before GitHub upload or local ACTIVE counts.

.PARAMETER InputPath
    Path to the SARIF file produced by codeql database analyze.

.PARAMETER OutputPath
    Path to write the filtered SARIF. May equal InputPath.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $InputPath,

    [Parameter(Mandatory = $true)]
    [string] $OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $InputPath)) {
    throw "SARIF input was not found at $InputPath"
}

$parent = Split-Path -Parent $OutputPath
if ($parent -and -not (Test-Path -LiteralPath $parent)) {
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
}

$python = Get-Command python -ErrorAction SilentlyContinue
if (-not $python) {
    throw "python was not found on PATH; required to filter SARIF without corrupting large JSON."
}

$script = @'
import json
import re
import sys
from pathlib import Path

inp, outp = Path(sys.argv[1]), Path(sys.argv[2])
sarif = json.loads(inp.read_text(encoding="utf-8"))
pattern = re.compile(r"(^|/|\\)(bin|obj)(/|\\|$)")
removed = kept = 0
for run in sarif.get("runs") or []:
    results = run.get("results") or []
    filtered = []
    for result in results:
        uri = ""
        locs = result.get("locations") or []
        if locs:
            uri = (
                (locs[0].get("physicalLocation") or {})
                .get("artifactLocation", {})
                .get("uri", "")
                or ""
            )
        normalized = uri.replace("\\", "/")
        if pattern.search(normalized):
            removed += 1
            continue
        filtered.append(result)
        kept += 1
    run["results"] = filtered
outp.write_text(json.dumps(sarif, ensure_ascii=False, indent=2), encoding="utf-8")
print(f"Filter-CodeQlSarif kept={kept} removed={removed} output={outp}")
'@

$tempPy = [System.IO.Path]::GetTempFileName() + ".py"
try {
    Set-Content -LiteralPath $tempPy -Value $script -Encoding utf8
    & python $tempPy $InputPath $OutputPath
    if ($LASTEXITCODE -ne 0) {
        throw "Filter-CodeQlSarif python step exited with code $LASTEXITCODE"
    }
}
finally {
    Remove-Item -LiteralPath $tempPy -Force -ErrorAction SilentlyContinue
}
