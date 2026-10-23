#Requires -Version 7.2
[CmdletBinding()]
param(
    [string]$GameRoot = 'D:/SteamLibrary/steamapps/common/RimWorld',
    [ValidateSet('All', 'Core', 'Multiplayer', 'Compatibility', 'ThirdPerson')]
    [string]$Mod = 'All',
    [string]$HarmonyPath,
    [string]$MultiplayerRoot,
    [string]$DotNet = 'dotnet',
    [switch]$Install,
    [switch]$Launch
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$modVersion = [IO.File]::ReadAllText((Join-Path $repoRoot 'Version.txt')).Trim()
if (!$modVersion -or $modVersion.Contains("`r") -or $modVersion.Contains("`n")) { throw 'Version.txt must contain a single non-empty version line.' }
if ($Launch) {
    if (!$Install) { throw '-Launch requires -Install.' }
    $gameExecutable = [IO.Path]::GetFullPath((Join-Path $GameRoot 'RimWorldWin64.exe'))
    if (!(Test-Path -LiteralPath $gameExecutable -PathType Leaf)) { throw "RimWorld executable missing: $gameExecutable" }
}
$gameManaged = Join-Path $GameRoot 'RimWorldWin64_Data/Managed'
$workshopRoot = [IO.Path]::GetFullPath((Join-Path $GameRoot '../../workshop/content/294100'))
if (!$HarmonyPath) {
    $harmonyCandidates = @(
        (Join-Path $GameRoot 'Mods/HarmonyMod/Current/Assemblies/0Harmony.dll'),
        (Join-Path $GameRoot 'Mods/HarmonyMod/1.6/Assemblies/0Harmony.dll'),
        (Join-Path $workshopRoot '2009463077/1.6/Assemblies/0Harmony.dll'),
        (Join-Path $workshopRoot '2009463077/Current/Assemblies/0Harmony.dll')
    )
    $HarmonyPath = $harmonyCandidates[0]
    foreach ($candidate in $harmonyCandidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $HarmonyPath = $candidate; break }
    }
}
if (!$MultiplayerRoot) {
    $multiplayerCandidates = @(
        (Join-Path $GameRoot 'Mods/Multiplayer'),
        (Join-Path $GameRoot 'Mods/Multiplayer/1.6'),
        (Join-Path $workshopRoot '2606448745/1.6'),
        (Join-Path $workshopRoot '2606448745')
    )
    $MultiplayerRoot = $multiplayerCandidates[0]
    foreach ($candidate in $multiplayerCandidates) {
        if (Test-Path -LiteralPath (Join-Path $candidate 'Assemblies/0MultiplayerAPI.dll') -PathType Leaf) { $MultiplayerRoot = $candidate; break }
    }
}
foreach ($required in @((Join-Path $gameManaged 'Assembly-CSharp.dll'), $HarmonyPath)) {
    if (!(Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required dependency missing: $required" }
}
if ($Install) {
    if (!(Test-Path -LiteralPath (Join-Path $GameRoot 'Mods') -PathType Container)) { throw "RimWorld Mods directory missing: $GameRoot" }
    if (Get-Process -Name RimWorldWin64, RimWorld -ErrorAction SilentlyContinue) { throw 'Close RimWorld before building and installing the mods.' }
}

$packages = @(
    @{ Id='Core'; Name='RimVision'; Project='Source/RimVision/Rim3D.csproj'; Dll='Recovery.Rim3D.dll' },
    @{ Id='Multiplayer'; Name='RimVision-Multiplayer-Compat'; Project='Source/Multiplayer/Rim3D.Multiplayer.csproj'; Dll='Recovery.Rim3D.Multiplayer.dll' },
    @{ Id='Compatibility'; Name='RimVision-Mod-Compatibility'; Project='Source/Compatibility/Rim3D.Compatibility.csproj'; Dll='Recovery.Rim3D.Compatibility.dll' },
    @{ Id='ThirdPerson'; Name='RimVision-ThirdPerson'; Project='Source/ThirdPerson/RimVision.ThirdPerson.csproj'; Dll='RimVision.ThirdPerson.dll' }
)
$selected = @($packages | Where-Object { $Mod -eq 'All' -or $_.Id -eq $Mod })
if ($selected.Id -contains 'Multiplayer') {
    $mpApi = Join-Path $MultiplayerRoot 'Assemblies/0MultiplayerAPI.dll'
    if (!(Test-Path -LiteralPath $mpApi -PathType Leaf)) { throw "Required dependency missing: $mpApi. Use -Mod Core to build only RimVision." }
}
$shaderBundle = Join-Path $repoRoot 'Mods/RimVision/ShaderBundles/rim3d-shaders'
if ($selected.Id -contains 'Core' -and !(Test-Path -LiteralPath $shaderBundle -PathType Leaf)) {
    throw "Precompiled shader bundle missing: $shaderBundle"
}

function Copy-Content([string]$From, [string]$To) {
    if (!(Test-Path -LiteralPath $From -PathType Container)) { throw "Content directory missing: $From" }
    New-Item -ItemType Directory -Path $To -Force | Out-Null
    foreach ($entry in Get-ChildItem -LiteralPath $From -Force -Recurse -File) {
        $relative = [IO.Path]::GetRelativePath($From, $entry.FullName)
        if ($relative -match '(^|[/\\])(bin|obj|\.git|__pycache__)([/\\]|$)' -or $entry.Extension -in @('.meta', '.pyc')) { continue }
        $output = Join-Path $To $relative
        New-Item -ItemType Directory -Path (Split-Path $output) -Force | Out-Null
        Copy-Item -LiteralPath $entry.FullName -Destination $output -Force
    }
}

function Set-DescriptionVersion([string]$Path, [string]$Version) {
    $document = [xml]::new()
    $document.PreserveWhitespace = $true
    $document.Load($Path)
    $description = $document.SelectSingleNode('/ModMetaData/description | /LanguageData/RimVision_ModDescription')
    if ($null -eq $description -or !$description.InnerText.Contains('{RimVisionVersion}')) { throw "Description version placeholder missing: $Path" }
    $description.InnerText = $description.InnerText.Replace('{RimVisionVersion}', $Version)
    $document.Save($Path)
}

function Install-Packages($Packages, [string]$GameDirectory, [string]$BuildDirectory) {
    if (Get-Process -Name RimWorldWin64, RimWorld -ErrorAction SilentlyContinue) { throw 'Close RimWorld before installing the mods.' }
    $gameDirectoryFull = [IO.Path]::GetFullPath($GameDirectory)
    $modsDirectory = Join-Path $gameDirectoryFull 'Mods'
    $runId = [Guid]::NewGuid().ToString('N')
    $stageRoot = Join-Path $gameDirectoryFull ('.rimvision-install-' + $runId)
    $backupRoot = Join-Path $gameDirectoryFull ('RimVisionBackups/' + $runId)
    $entries = [Collections.Generic.List[object]]::new()
    foreach ($package in $Packages) {
        $source = Join-Path $BuildDirectory $package.Name
        $destination = [IO.Path]::GetFullPath((Join-Path $modsDirectory $package.Name))
        $stage = [IO.Path]::GetFullPath((Join-Path $stageRoot $package.Name))
        $backup = [IO.Path]::GetFullPath((Join-Path $backupRoot $package.Name))
        foreach ($pathPair in @(@($destination, $modsDirectory), @($stage, $stageRoot), @($backup, $backupRoot))) {
            if (![string]::Equals([IO.Path]::GetDirectoryName($pathPair[0]), $pathPair[1], [StringComparison]::OrdinalIgnoreCase)) { throw "Installation path is outside its intended directory: $($pathPair[0])" }
        }
        $sourceAbout = Join-Path $source 'About/About.xml'
        if (!(Test-Path -LiteralPath $sourceAbout -PathType Leaf) -or !(Test-Path -LiteralPath (Join-Path $source "Assemblies/$($package.Dll)") -PathType Leaf)) { throw "Incomplete built package: $source" }
        [xml]$sourceMetadata = Get-Content -LiteralPath $sourceAbout -Raw
        $packageId = [string]$sourceMetadata.ModMetaData.packageId
        if (!$packageId) { throw "Package ID missing: $sourceAbout" }
        if (Test-Path -LiteralPath $destination) {
            $installedAbout = Join-Path $destination 'About/About.xml'
            if (!(Test-Path -LiteralPath $installedAbout -PathType Leaf)) { throw "Cannot replace an unidentified mod directory: $destination" }
            [xml]$installedMetadata = Get-Content -LiteralPath $installedAbout -Raw
            if (![string]::Equals($packageId, [string]$installedMetadata.ModMetaData.packageId, [StringComparison]::OrdinalIgnoreCase)) { throw "Installed package ID differs: $destination" }
        }
        Copy-Content $source $stage
        $publishedId = Join-Path $destination 'About/PublishedFileId.txt'
        $stagedId = Join-Path $stage 'About/PublishedFileId.txt'
        if (Test-Path -LiteralPath $publishedId -PathType Leaf) {
            if ((Test-Path -LiteralPath $stagedId -PathType Leaf) -and [IO.File]::ReadAllText($publishedId).Trim() -ne [IO.File]::ReadAllText($stagedId).Trim()) { throw "PublishedFileId differs for $($package.Name). Installed mod was not changed." }
            Copy-Item -LiteralPath $publishedId -Destination $stagedId -Force
        }
        $entries.Add(@{ Name=$package.Name; Destination=$destination; Stage=$stage; Backup=$backup; BackedUp=$false; Installed=$false })
    }
    try {
        foreach ($entry in $entries) {
            if (Test-Path -LiteralPath $entry.Destination) {
                New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
                Move-Item -LiteralPath $entry.Destination -Destination $entry.Backup
                $entry.BackedUp = $true
            }
            Move-Item -LiteralPath $entry.Stage -Destination $entry.Destination
            $entry.Installed = $true
        }
    }
    catch {
        $installationError = $_
        for ($i = $entries.Count - 1; $i -ge 0; $i--) {
            $entry = $entries[$i]
            try {
                if ($entry.Installed) { Move-Item -LiteralPath $entry.Destination -Destination $entry.Stage }
                if ($entry.BackedUp) { Move-Item -LiteralPath $entry.Backup -Destination $entry.Destination }
            }
            catch { Write-Warning "Could not restore $($entry.Name): $_. Backup: $($entry.Backup)" }
        }
        Write-Warning "Installation failed. Staging: $stageRoot. Backups: $backupRoot"
        throw $installationError
    }
    Remove-Item -LiteralPath $stageRoot
    foreach ($entry in $entries) { Write-Output "Installed $($entry.Name): $($entry.Destination)" }
    if (Test-Path -LiteralPath $backupRoot) { Write-Output "Previous installed versions: $backupRoot" }
}

$buildRoot = Join-Path $repoRoot 'Builds'
$stagingRoot = Join-Path $buildRoot ('.staging-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
$msbuildProperties = @("-p:GameRoot=$GameRoot", "-p:GameManaged=$gameManaged", "-p:HarmonyPath=$HarmonyPath", "-p:MultiplayerRoot=$MultiplayerRoot")
$coreProject = Join-Path $repoRoot 'Source/RimVision/Rim3D.csproj'
& $DotNet build $coreProject -c Release --nologo @msbuildProperties
if ($LASTEXITCODE -ne 0) { throw "Core compilation failed. Staging retained: $stagingRoot" }

foreach ($package in $selected) {
    $project = Join-Path $repoRoot $package.Project
    if ($package.Id -ne 'Core') {
        & $DotNet build $project -c Release --nologo -p:BuildProjectReferences=false @msbuildProperties
        if ($LASTEXITCODE -ne 0) { throw "Compilation failed: $($package.Name). Staging retained: $stagingRoot" }
    }
    $assembly = Join-Path (Split-Path $project) "bin/Release/net472/$($package.Dll)"
    if (!(Test-Path -LiteralPath $assembly -PathType Leaf)) { throw "Compiled assembly missing: $assembly" }
    $stage = Join-Path $stagingRoot $package.Name
    Copy-Content (Join-Path $repoRoot "Mods/$($package.Name)") $stage
    if ($package.Id -in @('Core', 'Multiplayer')) {
        Set-DescriptionVersion (Join-Path $stage 'About/About.xml') $modVersion
        if ($package.Id -eq 'Core') {
            foreach ($language in @('English', 'Russian')) {
                Set-DescriptionVersion (Join-Path $stage "Languages/$language/Keyed/ModDescription.xml") $modVersion
            }
        }
    }
    $assemblies = Join-Path $stage 'Assemblies'
    New-Item -ItemType Directory -Path $assemblies -Force | Out-Null
    Copy-Item -LiteralPath $assembly -Destination $assemblies
    if ($package.Id -eq 'Core') {
        if (!('Rim3DModelLodBuild' -as [type])) { Add-Type -Path (Join-Path $repoRoot 'Tools/Models/ModelLods.cs') }
        [Rim3DModelLodBuild]::Generate((Join-Path $stage 'OptionalContent'))
        Copy-Content (Join-Path $repoRoot 'Docs/API') (Join-Path $stage 'DeveloperAPI')
        Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination (Join-Path $stage 'README.md')
    }
    $stagedZip = Join-Path $stagingRoot "$($package.Name).zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $stagedZip, [IO.Compression.CompressionLevel]::Optimal, $true)
}

$backup = Join-Path $buildRoot ('.previous/' + [Guid]::NewGuid().ToString('N'))
foreach ($package in $selected) {
    foreach ($name in @($package.Name, "$($package.Name).zip")) {
        $destination = Join-Path $buildRoot $name
        if (Test-Path -LiteralPath $destination) {
            New-Item -ItemType Directory -Path $backup -Force | Out-Null
            Move-Item -LiteralPath $destination -Destination $backup
        }
        Move-Item -LiteralPath (Join-Path $stagingRoot $name) -Destination $destination
    }
    Write-Output "Built $($package.Name): $(Join-Path $buildRoot "$($package.Name).zip")"
}
Remove-Item -LiteralPath $stagingRoot
if ($Install) { Install-Packages $selected $GameRoot $buildRoot }
if ($Launch) {
    Start-Process -FilePath $gameExecutable -WorkingDirectory (Split-Path $gameExecutable) -WindowStyle Normal
    Write-Output "Launched RimWorld: $gameExecutable"
}
