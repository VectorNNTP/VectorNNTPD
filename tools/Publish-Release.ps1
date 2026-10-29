#Requires -Version 5.1
<#
.SYNOPSIS
    Publishes VectorNNTPD production services as self-contained x64 Native AOT binaries.

.DESCRIPTION
    Resolves this standalone repository from the script file, not the caller's
    working directory. Publishes VectorNNTPD.BackFillerService, VectorNNTPD.StorageService,
    VectorNNTPD.ReaderService, and VectorNNTPD.TransitService for win-x64, linux-x64,
    or both into per-service package directories under release/win-64 or release/linux-64:

      release/<platform>/backfiller/
      release/<platform>/storage/
      release/<platform>/reader/
      release/<platform>/transit/

    Each package contains that host's Native AOT executable, its primary JSON, a copy of
    VectorNNTPD.RabbitMQ.json, and (linux-64 only) the matching systemd unit, environment
    example, and README.md. Packages are independently installable; Reader and Transit never
    share a directory. Publishing stages under a temp directory and validates before
    replacing release/<platform>/. On failure before the swap, the prior release tree is
    left intact. On failure after the swap, the previous tree is restored when available.
    The script exits 1 on any failure.

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

$Applications = @(
    @{
        Name           = 'VectorNNTPD.BackFillerService'
        Package        = 'backfiller'
        Project        = Join-Path $RepoRoot 'src\VectorNNTPD.BackFillerService\VectorNNTPD.BackFillerService.csproj'
        Exe            = 'VectorNNTPD.BackFillerService'
        ConfigFile     = 'VectorNNTPD.BackFillerService.json'
        LinuxUnit      = 'vectornntp-backfiller.service'
        LinuxEnvExample = 'backfiller.env.example'
    },
    @{
        Name           = 'VectorNNTPD.StorageService'
        Package        = 'storage'
        Project        = Join-Path $RepoRoot 'src\VectorNNTPD.StorageService\VectorNNTPD.StorageService.csproj'
        Exe            = 'VectorNNTPD.StorageService'
        ConfigFile     = 'VectorNNTPD.StorageService.json'
        LinuxUnit      = 'vectornntp-storageservice.service'
        LinuxEnvExample = 'storageservice.env.example'
    },
    @{
        Name           = 'VectorNNTPD.ReaderService'
        Package        = 'reader'
        Project        = Join-Path $RepoRoot 'src\VectorNNTPD.ReaderService\VectorNNTPD.ReaderService.csproj'
        Exe            = 'VectorNNTPD.ReaderService'
        ConfigFile     = 'VectorNNTPD.ReaderService.json'
        LinuxUnit      = 'vectornntp-reader.service'
        LinuxEnvExample = 'reader.env.example'
    },
    @{
        Name           = 'VectorNNTPD.TransitService'
        Package        = 'transit'
        Project        = Join-Path $RepoRoot 'src\VectorNNTPD.TransitService\VectorNNTPD.TransitService.csproj'
        Exe            = 'VectorNNTPD.TransitService'
        ConfigFile     = 'VectorNNTPD.TransitService.json'
        LinuxUnit      = 'vectornntp-transit.service'
        LinuxEnvExample = 'transit.env.example'
    }
)

$SharedConfigFile = 'VectorNNTPD.RabbitMQ.json'
$DeploySystemd = Join-Path (Join-Path $RepoRoot 'deploy') 'systemd'
$ReadmeSource = Join-Path $RepoRoot 'README.md'

$ObsoleteNames = @(
    'appsettings.json',
    'RabbitMq.json',
    'VectorNNTP.RabbitMQ.json',
    'VectorNNTP.StorageServer.json',
    'VectorNNTP.StorageServer.Development.json',
    'VectorNNTP.BackFiller.json',
    'VectorNNTPD.StorageService.Development.json',
    'VectorNNTPD.BackFillerService.Development.json',
    'VectorNNTPD.ReaderService.Development.json',
    'VectorNNTPD.TransitService.Development.json',
    'VectorNNTPD.NntpServer.dll',
    'VectorNNTPD.NntpServer.exe',
    'libVectorNNTPD.NntpServer.so'
)

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

function Copy-FlatPublishFiles {
    param(
        [string] $Source,
        [string] $Destination
    )

    $entries = @(Get-ChildItem -LiteralPath $Source -Force)
    foreach ($entry in $entries) {
        if ($entry.PSIsContainer) {
            # Native AOT publish may create empty helper directories; packages stay flat files only.
            continue
        }

        $extension = [System.IO.Path]::GetExtension($entry.Name)
        if ($extension -eq '.pdb' -or $extension -eq '.dbg' -or $extension -eq '.xml') {
            continue
        }

        Copy-Item -LiteralPath $entry.FullName -Destination (Join-Path $Destination $entry.Name) -Force
    }
}

function Test-ServicePackage {
    param(
        [hashtable] $App,
        [string] $PackageDir,
        [string] $Rid,
        [string] $ExeSuffix
    )

    if (-not (Test-Path -LiteralPath $PackageDir)) {
        Fail "Package directory was not created: $PackageDir"
    }

    $nested = @(Get-ChildItem -LiteralPath $PackageDir -Directory -Force)
    if ($nested.Count -gt 0) {
        Fail "Service package must stay flat (no nested directories): $PackageDir contains $($nested.Name -join ', ')."
    }

    $exePath = Join-Path $PackageDir ($App.Exe + $ExeSuffix)
    if (-not (Test-Path -LiteralPath $exePath)) {
        Fail "Missing release executable: $exePath"
    }

    $exe = Get-Item -LiteralPath $exePath
    if ($exe.Length -le 0) {
        Fail "Native executable is empty: $exePath"
    }

    $managedPath = Join-Path $PackageDir ($App.Exe + '.dll')
    if (Test-Path -LiteralPath $managedPath) {
        Fail "Managed assembly '$($App.Exe).dll' is present. Native AOT output must not include it."
    }

    Test-NativeAotBinary -Path $exePath -Rid $Rid

    foreach ($runtimeFile in @('hostfxr.dll', 'coreclr.dll', 'libhostfxr.so', 'libcoreclr.so', 'hostfxr.dylib', 'libcoreclr.dylib')) {
        $runtimePath = Join-Path $PackageDir $runtimeFile
        if (Test-Path -LiteralPath $runtimePath) {
            Fail "Shared runtime file '$runtimeFile' is present in $($App.Package). The release is not self-contained Native AOT."
        }
    }

    foreach ($required in @($App.ConfigFile, $SharedConfigFile)) {
        $path = Join-Path $PackageDir $required
        if (-not (Test-Path -LiteralPath $path)) {
            Fail "Missing configuration file in $($App.Package): $path"
        }
    }

    # Role isolation: other hosts' primary JSON must not appear in this package.
    foreach ($other in $Applications) {
        if ($other.Name -eq $App.Name) {
            continue
        }

        $foreign = Join-Path $PackageDir $other.ConfigFile
        if (Test-Path -LiteralPath $foreign) {
            Fail "Foreign host configuration '$($other.ConfigFile)' must not appear in package $($App.Package)."
        }

        $foreignExe = Join-Path $PackageDir ($other.Exe + $ExeSuffix)
        if (Test-Path -LiteralPath $foreignExe) {
            Fail "Foreign executable '$($other.Exe)$ExeSuffix' must not appear in package $($App.Package)."
        }
    }

    foreach ($obsoleteName in $ObsoleteNames) {
        $obsoletePath = Join-Path $PackageDir $obsoleteName
        if (Test-Path -LiteralPath $obsoletePath) {
            Fail "Obsolete or forbidden file must not be packaged: $obsoletePath"
        }
    }

    $nntpServerHits = @(Get-ChildItem -LiteralPath $PackageDir -Force -File | Where-Object {
            $_.Name -like '*NntpServer*'
        })
    if ($nntpServerHits.Count -gt 0) {
        Fail "Package $($App.Package) must not contain NntpServer artifacts: $($nntpServerHits.Name -join ', ')."
    }

    if ($Rid -eq 'linux-x64') {
        foreach ($name in @($App.LinuxUnit, $App.LinuxEnvExample, 'README.md')) {
            $path = Join-Path $PackageDir $name
            if (-not (Test-Path -LiteralPath $path)) {
                Fail "Missing Linux package file in $($App.Package): $path"
            }
        }

        # Other roles' units/env must not leak into this package.
        foreach ($other in $Applications) {
            if ($other.Name -eq $App.Name) {
                continue
            }

            foreach ($name in @($other.LinuxUnit, $other.LinuxEnvExample)) {
                $path = Join-Path $PackageDir $name
                if (Test-Path -LiteralPath $path) {
                    Fail "Foreign Linux file '$name' must not appear in package $($App.Package)."
                }
            }
        }
    }
    else {
        foreach ($name in @($App.LinuxUnit, $App.LinuxEnvExample)) {
            $path = Join-Path $PackageDir $name
            if (Test-Path -LiteralPath $path) {
                Fail "Linux-only file must not be packaged for ${Rid}: $path"
            }
        }
    }
}

function Test-PlatformRelease {
    param(
        [string] $ReleaseDir,
        [string] $Rid,
        [string] $ExeSuffix
    )

    if (-not (Test-Path -LiteralPath $ReleaseDir)) {
        Fail "Release directory was not created: $ReleaseDir"
    }

    $expected = @($Applications | ForEach-Object { $_.Package })
    $dirs = @(Get-ChildItem -LiteralPath $ReleaseDir -Directory -Force | Select-Object -ExpandProperty Name)
    foreach ($name in $expected) {
        if ($dirs -notcontains $name) {
            Fail "Missing service package directory: $(Join-Path $ReleaseDir $name)"
        }
    }

    $unexpected = @($dirs | Where-Object { $expected -notcontains $_ })
    if ($unexpected.Count -gt 0) {
        Fail "Unexpected directories under $ReleaseDir : $($unexpected -join ', ')."
    }

    $looseFiles = @(Get-ChildItem -LiteralPath $ReleaseDir -File -Force)
    if ($looseFiles.Count -gt 0) {
        Fail "Platform release root must contain only service package directories. Loose files: $($looseFiles.Name -join ', ')."
    }

    foreach ($app in $Applications) {
        Test-ServicePackage -App $app -PackageDir (Join-Path $ReleaseDir $app.Package) -Rid $Rid -ExeSuffix $ExeSuffix
    }
}

function Publish-Platform {
    param(
        [string] $Folder,
        [string] $Rid,
        [string] $ExeSuffix
    )

    $releaseDir = Join-Path (Join-Path $RepoRoot 'release') $Folder
    $stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("vectornntpd-release-" + [System.Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stagingRoot | Out-Null
    $stageRoot = Join-Path $stagingRoot 'packages'
    New-Item -ItemType Directory -Path $stageRoot | Out-Null
    $previousBackup = $null
    $swapStarted = $false
    try {
        foreach ($app in $Applications) {
            if (-not (Test-Path -LiteralPath $app.Project)) {
                Fail "Project file was not found: $($app.Project)"
            }

            $publishStage = Join-Path $stagingRoot ("publish-" + $app.Name)
            Write-Host "dotnet publish $($app.Name) -c Release -p:Platform=x64 -r $Rid --self-contained true -p:PublishAot=true -o <staging>"
            & dotnet publish $app.Project -c Release -p:Platform=x64 -r $Rid --self-contained true -p:PublishAot=true -p:SelfContained=true -o $publishStage --nologo
            if ($LASTEXITCODE -ne 0) {
                Fail "dotnet publish failed for $($app.Name) ($Rid) with exit code $LASTEXITCODE."
            }

            $publishedExe = Join-Path $publishStage ($app.Exe + $ExeSuffix)
            if (-not (Test-Path -LiteralPath $publishedExe)) {
                Fail "Publish did not produce $($app.Exe)$ExeSuffix for $($app.Name)."
            }

            $packageDir = Join-Path $stageRoot $app.Package
            New-Item -ItemType Directory -Path $packageDir | Out-Null
            Copy-FlatPublishFiles -Source $publishStage -Destination $packageDir

            if ($Rid -eq 'linux-x64') {
                $unitSource = Join-Path $DeploySystemd $app.LinuxUnit
                $envSource = Join-Path $DeploySystemd $app.LinuxEnvExample
                foreach ($src in @($unitSource, $envSource, $ReadmeSource)) {
                    if (-not (Test-Path -LiteralPath $src)) {
                        Fail "Linux release source was not found: $src"
                    }
                }

                Copy-Item -LiteralPath $unitSource -Destination (Join-Path $packageDir $app.LinuxUnit)
                Copy-Item -LiteralPath $envSource -Destination (Join-Path $packageDir $app.LinuxEnvExample)
                Copy-Item -LiteralPath $ReadmeSource -Destination (Join-Path $packageDir 'README.md')
            }

            Test-ServicePackage -App $app -PackageDir $packageDir -Rid $Rid -ExeSuffix $ExeSuffix
        }

        Test-PlatformRelease -ReleaseDir $stageRoot -Rid $Rid -ExeSuffix $ExeSuffix

        # Swap only after the staged packages validate. Preserve the prior tree until
        # the new release directory also validates, then discard the backup.
        $swapStarted = $true
        if (Test-Path -LiteralPath $releaseDir) {
            $previousBackup = $releaseDir + '.previous'
            if (Test-Path -LiteralPath $previousBackup) {
                Remove-Item -LiteralPath $previousBackup -Recurse -Force
            }

            Move-Item -LiteralPath $releaseDir -Destination $previousBackup
        }

        New-Item -ItemType Directory -Path $releaseDir | Out-Null
        foreach ($app in $Applications) {
            $dest = Join-Path $releaseDir $app.Package
            New-Item -ItemType Directory -Path $dest | Out-Null
            Copy-FlatPublishFiles -Source (Join-Path $stageRoot $app.Package) -Destination $dest
            if ($Rid -eq 'linux-x64') {
                foreach ($name in @($app.LinuxUnit, $app.LinuxEnvExample, 'README.md')) {
                    Copy-Item -LiteralPath (Join-Path (Join-Path $stageRoot $app.Package) $name) -Destination (Join-Path $dest $name) -Force
                }
            }
        }

        Test-PlatformRelease -ReleaseDir $releaseDir -Rid $Rid -ExeSuffix $ExeSuffix

        if ($null -ne $previousBackup -and (Test-Path -LiteralPath $previousBackup)) {
            Remove-Item -LiteralPath $previousBackup -Recurse -Force
            $previousBackup = $null
        }
    }
    catch {
        if ($swapStarted) {
            if (Test-Path -LiteralPath $releaseDir) {
                Remove-Item -LiteralPath $releaseDir -Recurse -Force
            }

            if ($null -ne $previousBackup -and (Test-Path -LiteralPath $previousBackup)) {
                Move-Item -LiteralPath $previousBackup -Destination $releaseDir
                $previousBackup = $null
            }
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
    foreach ($app in $Applications) {
        $packageDir = Join-Path $releaseDir $app.Package
        Write-Host ""
        Write-Host "Package $($app.Package)/"
        Get-ChildItem -LiteralPath $packageDir -Force | Sort-Object Name | ForEach-Object {
            Write-Host ("{0,12}  {1}" -f $_.Length, $_.Name)
        }
    }
}

try {
    if (-not (Test-Path -LiteralPath $Solution)) {
        Fail "Solution was not found at $Solution."
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
