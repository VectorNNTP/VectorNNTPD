#Requires -Version 5.1
<#
.SYNOPSIS
    Filters CodeQL SARIF for GitHub upload and local ACTIVE counting.

.DESCRIPTION
    Traced C# CodeQL extraction includes compiler and source-generator output under
    **/obj/** and **/bin/**. Repository paths-ignore does not drop those compiled files
    (see Invoke-CodeQlAnalysis.ps1). This filter:

    1. Removes results whose primary location is under bin/ or obj/.
    2. Normalizes CodeQL AlertSuppression entries ({ state: accepted }) to SARIF 2.1
       suppressions ({ kind: inSource, status: accepted }) that GitHub code scanning
       recognizes.
    3. Optionally strips accepted in-source suppressions from the uploaded result set
       so PR "new alert" counts do not treat already-justified findings as actionable.
       In-source // codeql[...] comments remain the source of truth for local analysis.

.PARAMETER InputPath
    Path to the SARIF file produced by codeql database analyze.

.PARAMETER OutputPath
    Path to write the filtered SARIF. May equal InputPath.

.PARAMETER StripAcceptedSuppressions
    When set, omit results that carry an accepted suppression after normalization.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $InputPath,

    [Parameter(Mandatory = $true)]
    [string] $OutputPath,

    [switch] $StripAcceptedSuppressions
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
strip_accepted = sys.argv[3] == "1"
sarif = json.loads(inp.read_text(encoding="utf-8"))
pattern = re.compile(r"(^|/|\\)(bin|obj)(/|\\|$)")
removed_gen = removed_supp = kept = normalized = 0

def normalize_suppressions(result):
    global normalized
    supps = result.get("suppressions")
    if not supps:
        return False
    accepted = False
    rewritten = []
    for item in supps:
        if not isinstance(item, dict):
            rewritten.append(item)
            continue
        status = item.get("status") or item.get("state")
        kind = item.get("kind") or "inSource"
        if status == "accepted":
            accepted = True
            if item.get("status") != "accepted" or item.get("kind") != "inSource" or "state" in item:
                normalized += 1
            rewritten.append({"kind": kind if kind in ("inSource", "external") else "inSource", "status": "accepted"})
        else:
            rewritten.append(item)
    result["suppressions"] = rewritten
    return accepted

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
        normalized_uri = uri.replace("\\", "/")
        if pattern.search(normalized_uri):
            removed_gen += 1
            continue
        accepted = normalize_suppressions(result)
        if strip_accepted and accepted:
            removed_supp += 1
            continue
        filtered.append(result)
        kept += 1
    run["results"] = filtered

outp.write_text(json.dumps(sarif, ensure_ascii=False, indent=2), encoding="utf-8")
print(
    f"Filter-CodeQlSarif kept={kept} removed_gen={removed_gen} "
    f"removed_supp={removed_supp} normalized={normalized} output={outp}"
)
'@

$tempPy = [System.IO.Path]::GetTempFileName() + ".py"
try {
    Set-Content -LiteralPath $tempPy -Value $script -Encoding utf8
    $stripFlag = if ($StripAcceptedSuppressions) { "1" } else { "0" }
    & python $tempPy $InputPath $OutputPath $stripFlag
    if ($LASTEXITCODE -ne 0) {
        throw "Filter-CodeQlSarif python step exited with code $LASTEXITCODE"
    }
}
finally {
    Remove-Item -LiteralPath $tempPy -Force -ErrorAction SilentlyContinue
}
