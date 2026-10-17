#Requires -Version 5.1
<#
.SYNOPSIS
    Publishes VectorNNTPD.BackFillerService as a self-contained x64 Native AOT binary.

.DESCRIPTION
    Resolves this standalone repository from the script file, not the caller's
    working directory. Publishes src/VectorNNTPD.BackFillerService/VectorNNTPD.BackFillerService.csproj for win-x64,
    linux-x64, or both. Output is a flat directory: .artifacts/release/win-64 or
    .artifacts/release/linux-64. The linux-64 directory also contains
    deploy/systemd/vectornntp-backfiller.service, deploy/systemd/backfiller.env.example,
    and README.md.
    A failed publish leaves that platform directory unchanged and exits 1.

.PARAMETER Platform
    win-64, linux-64, or all.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Platform
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$Solution = Join-Path $RepoRoot 'VectorNNTPD.slnx'
$Project = Join-Path $RepoRoot 'src\VectorNNTPD.BackFillerService\VectorNNTPD.BackFillerService.csproj'
$ExeName = 'VectorNNTPD.BackFillerService'
$RequiredPublishFiles = @(
    'VectorNNTPD.BackFillerService.json',
    'VectorNNTP.RabbitMQ.json'
)
$SystemdServiceFileName = 'vectornntp-backfiller.service'
$SystemdServiceSource = Join-Path (Join-Path (Join-Path $RepoRoot 'deploy') 'systemd') $SystemdServiceFileName
$EnvironmentExampleFileName = 'backfiller.env.example'
$EnvironmentExampleSource = Join-Path (Join-Path (Join-Path $RepoRoot 'deploy') 'systemd') $EnvironmentExampleFileName
$LinuxDocumentationFileName = 'README.md'
$LinuxDocumentationSource = Join-Path $RepoRoot $LinuxDocumentationFileName

function Fail {
    param([string] $Message)
    throw $Message
}

function Get-PlatformMap {
    param([string] $Name)
    switch ($Name) {
        'win-64' { return @{ Folder = 'win-64'; Rid = 'win-x64'; ExeSuffix = '.exe' } }
        'linux-64' { return @{ Folder = 'linux-64'; Rid = 'linux-x64'; ExeSuffix = '' } }
        default { return $null }
    }
}

function Test-NativeAotBinary {
    param(
        [string] $Path,
        [string] $Rid
    )

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -eq 0) {
        Fail "Native executable is empty: $Path"
    }

    if ($Rid -eq 'win-x64') {
        if ($bytes.Length -lt 512 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) {
            Fail "Expected a Windows PE executable: $Path"
        }

        $peOffset = [System.BitConverter]::ToInt32($bytes, 0x3C)
        if ($peOffset -lt 0 -or ($peOffset + 256) -ge $bytes.Length) {
            Fail "PE header is outside the file: $Path"
        }

        if ($bytes[$peOffset] -ne 0x50 -or $bytes[$peOffset + 1] -ne 0x45) {
            Fail "Missing PE signature: $Path"
        }

        $machine = [System.BitConverter]::ToUInt16($bytes, $peOffset + 4)
        if ($machine -ne 0x8664) {
            Fail "Expected x64 (0x8664) and found machine 0x$('{0:X4}' -f $machine): $Path"
        }

        $optional = $peOffset + 24
        $magic = [System.BitConverter]::ToUInt16($bytes, $optional)
        if ($magic -ne 0x20B) {
            Fail "Expected PE32+ and found magic 0x$('{0:X4}' -f $magic): $Path"
        }

        $clrSizeOffset = $optional + 112 + (14 * 8) + 4
        $clrSize = [System.BitConverter]::ToUInt32($bytes, $clrSizeOffset)
        if ($clrSize -ne 0) {
            Fail "Executable still has a CLR runtime header, so it is not Native AOT: $Path"
        }

        return
    }

    if ($bytes.Length -lt 64 -or $bytes[0] -ne 0x7F -or $bytes[1] -ne 0x45 -or $bytes[2] -ne 0x4C -or $bytes[3] -ne 0x46) {
        Fail "Expected a Linux ELF executable: $Path"
    }

    if ($bytes[4] -ne 2) {
        Fail "Expected a 64-bit ELF executable: $Path"
    }

    if ($bytes[5] -ne 1) {
        Fail "Expected a little-endian ELF executable: $Path"
    }

    $elfType = [System.BitConverter]::ToUInt16($bytes, 16)
    if ($elfType -ne 2 -and $elfType -ne 3) {
        Fail "Expected an ELF executable (ET_EXEC or ET_DYN) and found type ${elfType}: $Path"
    }

    $elfMachine = [System.BitConverter]::ToUInt16($bytes, 18)
    if ($elfMachine -ne 62) {
        Fail "Expected ELF x86-64 (62) and found machine ${elfMachine}: $Path"
    }
}

function Copy-FlatPublishOutput {
    param(
        [string] $Source,
        [string] $Destination
    )

    New-Item -ItemType Directory -Path $Destination | Out-Null
    $entries = @(Get-ChildItem -LiteralPath $Source -Force)
    foreach ($entry in $entries) {
        if ($entry.PSIsContainer) {
            Fail "Publish output '$($entry.FullName)' contains a directory. Release output must stay flat."
        }

        Copy-Item -LiteralPath $entry.FullName -Destination (Join-Path $Destination $entry.Name)
    }
}

function Test-PublishedRelease {
    param(
        [string] $ReleaseDir,
        [string] $Rid,
        [string] $ExeSuffix
    )

    if (-not (Test-Path -LiteralPath $ReleaseDir)) {
        Fail "Release directory was not created: $ReleaseDir"
    }

    $nested = @(Get-ChildItem -LiteralPath $ReleaseDir -Directory -Force)
    if ($nested.Count -gt 0) {
        Fail "Release directory contains nested directories: $($nested.Name -join ', ')."
    }

    $exePath = Join-Path $ReleaseDir ($ExeName + $ExeSuffix)
    if (-not (Test-Path -LiteralPath $exePath)) {
        Fail "Missing release executable: $exePath"
    }

    $exe = Get-Item -LiteralPath $exePath
    if ($exe.Length -le 0) {
        Fail "Native executable is empty: $exePath"
    }

    $managedPath = Join-Path $ReleaseDir ($ExeName + '.dll')
    if (Test-Path -LiteralPath $managedPath) {
        Fail "Managed assembly '$($ExeName).dll' is present. Native AOT output must not include it."
    }

    foreach ($runtimeFile in @('hostfxr.dll', 'coreclr.dll', 'libhostfxr.so', 'libcoreclr.so', 'hostfxr.dylib', 'libcoreclr.dylib')) {
        $runtimePath = Join-Path $ReleaseDir $runtimeFile
        if (Test-Path -LiteralPath $runtimePath) {
            Fail "Shared runtime file '$runtimeFile' is present. The release is not a self-contained Native AOT layout."
        }
    }

    foreach ($fileName in $RequiredPublishFiles) {
        $filePath = Join-Path $ReleaseDir $fileName
        if (-not (Test-Path -LiteralPath $filePath)) {
            Fail "Missing runtime file published with BackFiller: $filePath"
        }
    }

    foreach ($obsoleteName in @('appsettings.json', 'RabbitMq.json')) {
        $obsoletePath = Join-Path $ReleaseDir $obsoleteName
        if (Test-Path -LiteralPath $obsoletePath) {
            Fail "Obsolete configuration file must not be packaged: $obsoletePath"
        }
    }

    $servicePath = Join-Path $ReleaseDir $SystemdServiceFileName
    $documentationPath = Join-Path $ReleaseDir $LinuxDocumentationFileName
    if ($Rid -eq 'linux-x64') {
        if (-not (Test-Path -LiteralPath $servicePath)) {
            Fail "Missing systemd unit in the linux-x64 release: $servicePath"
        }

        if (-not (Test-Path -LiteralPath $documentationPath)) {
            Fail "Missing documentation in the linux-x64 release: $documentationPath"
        }

        $environmentExamplePath = Join-Path $ReleaseDir $EnvironmentExampleFileName
        if (-not (Test-Path -LiteralPath $environmentExamplePath)) {
            Fail "Missing environment-file example in the linux-x64 release: $environmentExamplePath"
        }
    }
    else {
        if (Test-Path -LiteralPath $servicePath) {
            Fail "systemd unit must not be packaged for ${Rid}: $servicePath"
        }

        $environmentExamplePath = Join-Path $ReleaseDir $EnvironmentExampleFileName
        if (Test-Path -LiteralPath $environmentExamplePath) {
            Fail "Environment-file example must not be packaged for ${Rid}: $environmentExamplePath"
        }
    }

    Test-NativeAotBinary -Path $exePath -Rid $Rid
}

function Publish-Platform {
    param(
        [string] $Folder,
        [string] $Rid,
        [string] $ExeSuffix
    )

    $releaseDir = Join-Path (Join-Path $RepoRoot 'release') $Folder
    $stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("backfiller-release-" + [System.Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stagingRoot | Out-Null
    $stage = Join-Path $stagingRoot 'publish'
    $replacedRelease = $false
    try {
        Write-Host "dotnet publish VectorNNTPD.BackFillerService -c Release -p:Platform=x64 -r $Rid --self-contained true -p:PublishAot=true -o <staging>"
        & dotnet publish $Project -c Release -p:Platform=x64 -r $Rid --self-contained true -p:PublishAot=true -p:SelfContained=true -o $stage --nologo
        if ($LASTEXITCODE -ne 0) {
            Fail "dotnet publish failed for VectorNNTPD.BackFillerService ($Rid) with exit code $LASTEXITCODE."
        }

        $publishedExe = Join-Path $stage ($ExeName + $ExeSuffix)
        if (-not (Test-Path -LiteralPath $publishedExe)) {
            Fail "Publish did not produce $($ExeName)$ExeSuffix."
        }

        if ($Rid -eq 'linux-x64') {
            if (-not (Test-Path -LiteralPath $SystemdServiceSource)) {
                Fail "systemd unit was not found: $SystemdServiceSource"
            }

            if (-not (Test-Path -LiteralPath $LinuxDocumentationSource)) {
                Fail "Linux documentation was not found: $LinuxDocumentationSource"
            }

            if (-not (Test-Path -LiteralPath $EnvironmentExampleSource)) {
                Fail "Environment-file example was not found: $EnvironmentExampleSource"
            }

            Copy-Item -LiteralPath $SystemdServiceSource -Destination (Join-Path $stage $SystemdServiceFileName)
            Copy-Item -LiteralPath $LinuxDocumentationSource -Destination (Join-Path $stage $LinuxDocumentationFileName)
            Copy-Item -LiteralPath $EnvironmentExampleSource -Destination (Join-Path $stage $EnvironmentExampleFileName)
        }

        Test-PublishedRelease -ReleaseDir $stage -Rid $Rid -ExeSuffix $ExeSuffix

        $replacedRelease = $true
        if (Test-Path -LiteralPath $releaseDir) {
            Remove-Item -LiteralPath $releaseDir -Recurse -Force
        }

        Copy-FlatPublishOutput -Source $stage -Destination $releaseDir
        Test-PublishedRelease -ReleaseDir $releaseDir -Rid $Rid -ExeSuffix $ExeSuffix
    }
    catch {
        if ($replacedRelease -and (Test-Path -LiteralPath $releaseDir)) {
            Remove-Item -LiteralPath $releaseDir -Recurse -Force
        }

        throw
    }
    finally {
        if (Test-Path -LiteralPath $stagingRoot) {
            Remove-Item -LiteralPath $stagingRoot -Recurse -Force
        }
    }

    Write-Host ""
    Write-Host "Release directory: $releaseDir"
    Get-ChildItem -LiteralPath $releaseDir -Force | Sort-Object Name | ForEach-Object {
        Write-Host ("{0,12}  {1}" -f $_.Length, $_.Name)
    }
}

try {
    if (-not (Test-Path -LiteralPath $Solution)) {
        Fail "Solution was not found at $Solution."
    }

    if (-not (Test-Path -LiteralPath $Project)) {
        Fail "Project file was not found: $Project"
    }

    $selected = @()
    if ($Platform -eq 'all') {
        $selected = @('win-64', 'linux-64')
    }
    else {
        $mapped = Get-PlatformMap -Name $Platform
        if ($null -eq $mapped) {
            Fail "Unsupported platform '$Platform'. Use win-64, linux-64, or all."
        }

        $selected = @($Platform)
    }

    foreach ($name in $selected) {
        $mapped = Get-PlatformMap -Name $name
        Write-Host "Restoring $($mapped.Rid) assets from $Solution"
        & dotnet restore $Solution -r $mapped.Rid -p:Platform=x64 --nologo
        if ($LASTEXITCODE -ne 0) {
            Fail "dotnet restore failed for $($mapped.Rid) with exit code $LASTEXITCODE."
        }

        Publish-Platform -Folder $mapped.Folder -Rid $mapped.Rid -ExeSuffix $mapped.ExeSuffix
    }
}
catch {
    [Console]::Error.WriteLine("error: $($_.Exception.Message)")
    exit 1
}
