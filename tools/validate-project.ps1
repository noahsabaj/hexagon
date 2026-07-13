[CmdletBinding()]
param(
    [Parameter()]
    [string] $HexagonRoot = (Split-Path -Parent $PSScriptRoot),

    [Parameter()]
    [string] $SchemaRoot = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'hl2rp-hexagon'),

    [Parameter()]
    [string] $SboxRoot = 'C:\Program Files (x86)\Steam\steamapps\common\sbox'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Failures = [System.Collections.Generic.List[string]]::new()
$pathComparer = [System.StringComparer]::OrdinalIgnoreCase

function Add-Failure {
    param([Parameter(Mandatory)][string] $Message)
    $script:Failures.Add($Message)
}

function Read-JsonFile {
    param([Parameter(Mandatory)][string] $Path)

    try {
        return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        Add-Failure "Invalid JSON '$Path': $($_.Exception.Message)"
        return $null
    }
}

function Get-NormalizedAssetPath {
    param(
        [Parameter(Mandatory)][string] $AssetsRoot,
        [Parameter(Mandatory)][string] $Path
    )

    # Windows PowerShell 5.1 runs on .NET Framework, where Path.GetRelativePath
    # is unavailable. Asset enumeration guarantees that Path is below AssetsRoot.
    $rootPath = [System.IO.Path]::GetFullPath($AssetsRoot).TrimEnd('\', '/')
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($rootPath + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Asset '$fullPath' is outside '$rootPath'."
    }

    return $fullPath.Substring($rootPath.Length + 1).Replace('\', '/').ToLowerInvariant()
}

function Get-JsonProperties {
    param([Parameter()][AllowNull()] $Value)

    if ($null -eq $Value) {
        return
    }

    if ($Value -is [System.Management.Automation.PSCustomObject]) {
        foreach ($property in $Value.PSObject.Properties) {
            [pscustomobject]@{ Name = $property.Name; Value = $property.Value }
            Get-JsonProperties -Value $property.Value
        }
        return
    }

    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]) {
        foreach ($item in $Value) {
            Get-JsonProperties -Value $item
        }
    }
}

function Test-ProjectManifest {
    param(
        [Parameter(Mandatory)][string] $ProjectRoot,
        [Parameter(Mandatory)][string] $ManifestName,
        [Parameter(Mandatory)][ValidateSet('library', 'game')][string] $ExpectedType
    )

    $manifestPath = Join-Path $ProjectRoot $ManifestName
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        Add-Failure "Missing package manifest: $manifestPath"
        return
    }

    $manifest = Read-JsonFile -Path $manifestPath
    if ($null -eq $manifest) {
        return
    }

    if ($manifest.Type -ne $ExpectedType) {
        Add-Failure "'$manifestPath' must have Type '$ExpectedType', found '$($manifest.Type)'."
    }

    $startupProperty = $manifest.Metadata.PSObject.Properties['StartupScene']
    if ($ExpectedType -eq 'library') {
        if ($null -ne $startupProperty) {
            Add-Failure "Library package '$manifestPath' must not declare Metadata.StartupScene."
        }

        $libraryScenes = @(Get-ChildItem -LiteralPath (Join-Path $ProjectRoot 'Assets') -Recurse -File -Filter '*.scene' -ErrorAction SilentlyContinue)
        if ($libraryScenes.Count -gt 0) {
            Add-Failure "Library package '$manifestPath' owns scene resources: $($libraryScenes.FullName -join ', ')"
        }
        return
    }

    if ($null -eq $startupProperty -or [string]::IsNullOrWhiteSpace([string]$startupProperty.Value)) {
        Add-Failure "Game package '$manifestPath' must declare Metadata.StartupScene."
        return
    }

    $relativeScene = ([string]$startupProperty.Value).Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    $startupPath = Join-Path (Join-Path $ProjectRoot 'Assets') $relativeScene
    if (-not (Test-Path -LiteralPath $startupPath -PathType Leaf)) {
        Add-Failure "Startup scene '$($startupProperty.Value)' does not exist at '$startupPath'."
    }

    $dedicatedProperty = $manifest.Metadata.PSObject.Properties['DedicatedServerStartupScene']
    if ($null -eq $dedicatedProperty -or [string]::IsNullOrWhiteSpace([string]$dedicatedProperty.Value)) {
        Add-Failure "Game package '$manifestPath' must declare Metadata.DedicatedServerStartupScene."
    }
    else {
        $relativeDedicatedScene = ([string]$dedicatedProperty.Value).Replace('/', [System.IO.Path]::DirectorySeparatorChar)
        $dedicatedStartupPath = Join-Path (Join-Path $ProjectRoot 'Assets') $relativeDedicatedScene
        if (-not (Test-Path -LiteralPath $dedicatedStartupPath -PathType Leaf)) {
            Add-Failure "Dedicated startup scene '$($dedicatedProperty.Value)' does not exist at '$dedicatedStartupPath'."
        }
    }
}

function Test-MountedAssetCollisions {
    param([Parameter(Mandatory)][string[]] $ProjectRoots)

    $owners = [System.Collections.Generic.Dictionary[string, System.Collections.Generic.List[string]]]::new($pathComparer)
    foreach ($projectRoot in $ProjectRoots) {
        $assetsRoot = Join-Path $projectRoot 'Assets'
        if (-not (Test-Path -LiteralPath $assetsRoot -PathType Container)) {
            continue
        }

        foreach ($file in Get-ChildItem -LiteralPath $assetsRoot -Recurse -File) {
            $relativePath = Get-NormalizedAssetPath -AssetsRoot $assetsRoot -Path $file.FullName
            if (-not $owners.ContainsKey($relativePath)) {
                $owners[$relativePath] = [System.Collections.Generic.List[string]]::new()
            }
            $owners[$relativePath].Add($file.FullName)
        }
    }

    foreach ($entry in $owners.GetEnumerator() | Where-Object { $_.Value.Count -gt 1 }) {
        Add-Failure "Mounted asset path collision '$($entry.Key)': $($entry.Value -join ', ')"
    }
}

function Test-AssetNamespaces {
    param(
        [Parameter(Mandatory)][string] $LibraryRoot,
        [Parameter(Mandatory)][string] $GameRoot
    )

    $libraryAssetsRoot = Join-Path $LibraryRoot 'Assets'
    foreach ($file in Get-ChildItem -LiteralPath $libraryAssetsRoot -Recurse -File -ErrorAction SilentlyContinue) {
        $path = Get-NormalizedAssetPath -AssetsRoot $libraryAssetsRoot -Path $file.FullName
        if (-not $path.StartsWith('hexagon/', [System.StringComparison]::OrdinalIgnoreCase)) {
            Add-Failure "Hexagon library asset '$path' is outside the required 'hexagon/' namespace."
        }
    }

    $gameAssetsRoot = Join-Path $GameRoot 'Assets'
    foreach ($file in Get-ChildItem -LiteralPath $gameAssetsRoot -Recurse -File -ErrorAction SilentlyContinue) {
        $path = Get-NormalizedAssetPath -AssetsRoot $gameAssetsRoot -Path $file.FullName
        if ($path -notin @('scenes/main.scene', 'scenes/main.scene_c') -and
            -not $path.StartsWith('hl2rp/', [System.StringComparison]::OrdinalIgnoreCase)) {
            Add-Failure "HL2RP asset '$path' is outside the required 'hl2rp/' namespace."
        }
    }
}

function Test-SceneIdentity {
    param([Parameter(Mandatory)][string[]] $ProjectRoots)

    $sceneIds = [System.Collections.Generic.Dictionary[string, string]]::new($pathComparer)
    $persistentIds = [System.Collections.Generic.Dictionary[string, string]]::new($pathComparer)

    foreach ($projectRoot in $ProjectRoots) {
        $assetsRoot = Join-Path $projectRoot 'Assets'
        foreach ($sceneFile in Get-ChildItem -LiteralPath $assetsRoot -Recurse -File -Filter '*.scene' -ErrorAction SilentlyContinue) {
            $scene = Read-JsonFile -Path $sceneFile.FullName
            if ($null -eq $scene) {
                continue
            }

            $rootId = [string]$scene.__guid
            if ([string]::IsNullOrWhiteSpace($rootId)) {
                Add-Failure "Scene '$($sceneFile.FullName)' has no root __guid."
            }
            elseif ($sceneIds.ContainsKey($rootId)) {
                Add-Failure "Duplicate scene root GUID '$rootId': '$($sceneIds[$rootId])' and '$($sceneFile.FullName)'."
            }
            else {
                $sceneIds[$rootId] = $sceneFile.FullName
            }

            $localGuids = [System.Collections.Generic.Dictionary[string, string]]::new($pathComparer)
            foreach ($property in Get-JsonProperties -Value $scene) {
                if ($property.Name -eq '__guid' -and $property.Value -is [string]) {
                    $id = [string]$property.Value
                    if ([string]::IsNullOrWhiteSpace($id)) {
                        Add-Failure "Empty __guid in scene '$($sceneFile.FullName)'."
                    }
                    elseif ($localGuids.ContainsKey($id)) {
                        Add-Failure "Duplicate object/component GUID '$id' in scene '$($sceneFile.FullName)'."
                    }
                    else {
                        $localGuids[$id] = $sceneFile.FullName
                    }
                }

                if ($property.Name -in @('PersistentId', 'SceneEntityId') -and $property.Value -is [string]) {
                    $id = [string]$property.Value
                    if ([string]::IsNullOrWhiteSpace($id) -or $id -eq '00000000-0000-0000-0000-000000000000') {
                        Add-Failure "Blank persistent scene identity in '$($sceneFile.FullName)'."
                    }
                    elseif ($persistentIds.ContainsKey($id)) {
                        Add-Failure "Duplicate persistent scene identity '$id': '$($persistentIds[$id])' and '$($sceneFile.FullName)'."
                    }
                    else {
                        $persistentIds[$id] = $sceneFile.FullName
                    }
                }
            }
        }
    }
}

function Test-ShowcaseScene {
    param([Parameter(Mandatory)][string] $GameRoot)

    $scenePath = Join-Path $GameRoot 'Assets\scenes\main.scene'
    $scene = Read-JsonFile -Path $scenePath
    if ($null -eq $scene) {
        return
    }

    # Windows PowerShell's ConvertFrom-Json treats __type as ETS metadata and
    # omits it from PSCustomObject properties. Read only this discriminator from
    # the already-validated JSON source instead.
    $source = Get-Content -LiteralPath $scenePath -Raw
    $types = @([regex]::Matches($source, '"__type"\s*:\s*"(?<type>[^"]+)"') |
        ForEach-Object { $_.Groups['type'].Value })
    $requiredOnce = @(
        'Hexagon.V2.Runtime.HexagonBootstrapComponent',
        'Sandbox.SpawnPoint',
        'HL2RP.V2.World.HL2RPDoorComponent',
        'HL2RP.V2.World.HL2RPStorageComponent',
        'HL2RP.V2.World.HL2RPVendorComponent',
        'HL2RP.V2.World.HL2RPRationDispenserComponent',
        'HL2RP.V2.World.HL2RPVendingMachineComponent',
        'HL2RP.V2.World.HL2RPForcefieldComponent',
        'HL2RP.V2.World.HL2RPScannerDockComponent',
        'HL2RP.V2.World.HL2RPScannerDroneComponent',
        'HL2RP.V2.World.HL2RPCombatTargetComponent',
        'HL2RP.V2.World.HL2RPWorldItemAreaComponent'
    )
    foreach ($requiredType in $requiredOnce) {
        $count = @($types | Where-Object { $_ -ceq $requiredType }).Count
        if ($count -ne 1) {
            Add-Failure "Showcase scene must contain exactly one '$requiredType', found $count."
        }
    }

    $legacyTypes = @($types | Where-Object { $_ -match '^Hexagon\.(?!V2\.)' })
    if ($legacyTypes.Count -gt 0) {
        Add-Failure "Showcase scene retains legacy Hexagon components: $($legacyTypes -join ', ')"
    }

    $persistentCount = @($types | Where-Object { $_ -ceq 'Hexagon.V2.Runtime.PersistentSceneEntity' }).Count
    if ($persistentCount -ne 10) {
        Add-Failure "Showcase scene must contain ten persistent feature identities, found $persistentCount."
    }
}

function Get-ResolvableModelPaths {
    param(
        [Parameter(Mandatory)][string[]] $ProjectRoots,
        [Parameter(Mandatory)][string] $EngineRoot
    )

    $models = [System.Collections.Generic.HashSet[string]]::new($pathComparer)
    foreach ($projectRoot in $ProjectRoots) {
        $assetsRoot = Join-Path $projectRoot 'Assets'
        foreach ($file in Get-ChildItem -LiteralPath $assetsRoot -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '\.vmdl(?:_c)?$' }) {
            $path = Get-NormalizedAssetPath -AssetsRoot $assetsRoot -Path $file.FullName
            [void]$models.Add(($path -replace '_c$', ''))
        }
    }

    if (Test-Path -LiteralPath $EngineRoot -PathType Container) {
        $engineAssetRoots = [System.Collections.Generic.List[string]]::new()
        $coreRoot = Join-Path $EngineRoot 'core'
        if (Test-Path -LiteralPath $coreRoot -PathType Container) {
            $engineAssetRoots.Add($coreRoot)
        }
        $addonsRoot = Join-Path $EngineRoot 'addons'
        foreach ($assets in Get-ChildItem -LiteralPath $addonsRoot -Directory -ErrorAction SilentlyContinue |
            ForEach-Object { Join-Path $_.FullName 'Assets' } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Container }) {
            $engineAssetRoots.Add($assets)
        }

        foreach ($assetsRoot in $engineAssetRoots) {
            foreach ($file in Get-ChildItem -LiteralPath $assetsRoot -Recurse -File -Filter '*.vmdl_c' -ErrorAction SilentlyContinue) {
                $path = Get-NormalizedAssetPath -AssetsRoot $assetsRoot -Path $file.FullName
                [void]$models.Add(($path -replace '_c$', ''))
            }
        }
    }

    return $models
}

function Test-ModelReferences {
    param(
        [Parameter(Mandatory)][string[]] $ProjectRoots,
        [Parameter(Mandatory)][System.Collections.Generic.HashSet[string]] $ResolvableModels
    )

    foreach ($projectRoot in $ProjectRoots) {
        $assetsRoot = Join-Path $projectRoot 'Assets'
        foreach ($jsonFile in Get-ChildItem -LiteralPath $assetsRoot -Recurse -File -ErrorAction SilentlyContinue | Where-Object Extension -in @('.scene', '.prefab', '.item', '.json')) {
            $document = Read-JsonFile -Path $jsonFile.FullName
            if ($null -eq $document) {
                continue
            }

            foreach ($property in Get-JsonProperties -Value $document) {
                if ($property.Name -notmatch '(?i)(^|World|View)Model(Name)?$' -or $property.Value -isnot [string]) {
                    continue
                }

                $model = ([string]$property.Value).Replace('\', '/').TrimStart('/').ToLowerInvariant()
                if ([string]::IsNullOrWhiteSpace($model) -or -not $model.EndsWith('.vmdl')) {
                    continue
                }

                if (-not $ResolvableModels.Contains($model)) {
                    Add-Failure "Unresolved model '$model' referenced by '$($jsonFile.FullName)'."
                }
            }
        }

        foreach ($sourceFile in Get-ChildItem -LiteralPath (Join-Path $projectRoot 'Code') -Recurse -File -Filter '*.cs' -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '[\\/]obj[\\/]' }) {
            $source = Get-Content -LiteralPath $sourceFile.FullName -Raw
            foreach ($match in [regex]::Matches($source, '(?i)["''](?<model>models/[a-z0-9_./-]+\.vmdl)["'']')) {
                $model = $match.Groups['model'].Value.ToLowerInvariant()
                if (-not $ResolvableModels.Contains($model)) {
                    Add-Failure "Unresolved model '$model' referenced by '$($sourceFile.FullName)'."
                }
            }
        }
    }
}

function Test-DefinitionIds {
    param([Parameter(Mandatory)][string[]] $ProjectRoots)

    $definitionIds = [System.Collections.Generic.Dictionary[string, string]]::new($pathComparer)
    $definitionExtensions = @('.item', '.faction', '.class', '.attrib', '.vendor', '.definition')

    foreach ($projectRoot in $ProjectRoots) {
        $assetsRoot = Join-Path $projectRoot 'Assets'
        foreach ($definitionFile in Get-ChildItem -LiteralPath $assetsRoot -Recurse -File -ErrorAction SilentlyContinue | Where-Object Extension -in $definitionExtensions) {
            $definition = Read-JsonFile -Path $definitionFile.FullName
            if ($null -eq $definition) {
                continue
            }

            $idProperty = $definition.PSObject.Properties['DefinitionId']
            if ($null -eq $idProperty) {
                $idProperty = $definition.PSObject.Properties['Id']
            }

            $id = if ($null -eq $idProperty) { '' } else { [string]$idProperty.Value }
            if ([string]::IsNullOrWhiteSpace($id)) {
                Add-Failure "Definition '$($definitionFile.FullName)' has no stable DefinitionId/Id."
            }
            elseif ($definitionIds.ContainsKey($id)) {
                Add-Failure "Duplicate definition ID '$id': '$($definitionIds[$id])' and '$($definitionFile.FullName)'."
            }
            else {
                $definitionIds[$id] = $definitionFile.FullName
            }
        }
    }
}

function Test-StaticModuleGraph {
    param([Parameter(Mandatory)][string[]] $ProjectRoots)

    foreach ($projectRoot in $ProjectRoots) {
        foreach ($graphFile in Get-ChildItem -LiteralPath $projectRoot -Recurse -File -ErrorAction SilentlyContinue | Where-Object Name -Match '(^|\.)modules\.json$') {
            $graph = Read-JsonFile -Path $graphFile.FullName
            if ($null -eq $graph) {
                continue
            }

            $modules = @($graph.Modules)
            $ids = [System.Collections.Generic.HashSet[string]]::new($pathComparer)
            foreach ($module in $modules) {
                $id = [string]$module.Id
                if ([string]::IsNullOrWhiteSpace($id)) {
                    Add-Failure "Module without a stable ID in '$($graphFile.FullName)'."
                }
                elseif (-not $ids.Add($id)) {
                    Add-Failure "Duplicate module ID '$id' in '$($graphFile.FullName)'."
                }
            }

            $remaining = @{}
            foreach ($module in $modules) {
                $id = [string]$module.Id
                if ([string]::IsNullOrWhiteSpace($id)) {
                    continue
                }
                $remaining[$id] = [System.Collections.Generic.HashSet[string]]::new($pathComparer)
                foreach ($dependency in @($module.Dependencies)) {
                    $dependencyId = [string]$dependency
                    if (-not $ids.Contains($dependencyId)) {
                        Add-Failure "Module '$id' depends on missing module '$dependencyId' in '$($graphFile.FullName)'."
                    }
                    else {
                        [void]$remaining[$id].Add($dependencyId)
                    }
                }
            }

            while ($remaining.Count -gt 0) {
                $ready = @($remaining.Keys | Where-Object { $remaining[$_].Count -eq 0 })
                if ($ready.Count -eq 0) {
                    Add-Failure "Module dependency cycle in '$($graphFile.FullName)': $($remaining.Keys -join ', ')"
                    break
                }

                foreach ($readyId in $ready) {
                    $remaining.Remove($readyId)
                    foreach ($dependencies in $remaining.Values) {
                        [void]$dependencies.Remove($readyId)
                    }
                }
            }
        }
    }
}

$HexagonRoot = (Resolve-Path -LiteralPath $HexagonRoot).Path
$SchemaRoot = (Resolve-Path -LiteralPath $SchemaRoot).Path
$projectRoots = @($HexagonRoot, $SchemaRoot)
$resolvableModels = Get-ResolvableModelPaths -ProjectRoots $projectRoots -EngineRoot $SboxRoot

Test-ProjectManifest -ProjectRoot $HexagonRoot -ManifestName 'hexagon.sbproj' -ExpectedType 'library'
Test-ProjectManifest -ProjectRoot $SchemaRoot -ManifestName 'hl2rp.sbproj' -ExpectedType 'game'
Test-AssetNamespaces -LibraryRoot $HexagonRoot -GameRoot $SchemaRoot
Test-MountedAssetCollisions -ProjectRoots $projectRoots
Test-SceneIdentity -ProjectRoots $projectRoots
Test-ShowcaseScene -GameRoot $SchemaRoot
Test-ModelReferences -ProjectRoots $projectRoots -ResolvableModels $resolvableModels
Test-DefinitionIds -ProjectRoots $projectRoots
Test-StaticModuleGraph -ProjectRoots $projectRoots

if ($script:Failures.Count -gt 0) {
    $message = "Project validation failed with $($script:Failures.Count) error(s):`n - " + ($script:Failures -join "`n - ")
    throw $message
}

Write-Host 'Project validation passed: manifests, mounted paths, scene identities, mounted models, definitions, and static module graphs.' -ForegroundColor Green
