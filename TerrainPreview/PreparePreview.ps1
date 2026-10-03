$ErrorActionPreference = "Stop"
$terrainWorktreeRoot = Split-Path -Parent $PSScriptRoot
$terrainLinks = @{
    "Assets/TerrainVisual" = "UnityProject/Assets/GameScripts/Main/TerrainVisual"
    "Assets/TerrainEditor" = "UnityProject/Assets/Editor/TerrainVisual"
    "Assets/GameRes/Raw/TerrainLab" = "UnityProject/Assets/GameRes/Raw/TerrainLab"
    "Assets/Orbis/Orbis_Terrains" = "UnityProject/Assets/GameRes/Art/Terrain/.ThirdParty/Orbis_Terrains"
}
foreach ($terrainRelativePath in $terrainLinks.Keys) {
    $terrainLinkPath = Join-Path $PSScriptRoot $terrainRelativePath
    $terrainTargetPath = Join-Path $terrainWorktreeRoot $terrainLinks[$terrainRelativePath]
    if (-not (Test-Path -LiteralPath $terrainTargetPath)) { throw "Missing source directory: $terrainTargetPath" }
    if (-not (Test-Path -LiteralPath $terrainLinkPath)) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $terrainLinkPath) -Force | Out-Null
        New-Item -ItemType Junction -Path $terrainLinkPath -Target $terrainTargetPath | Out-Null
    }
    if ((Test-Path -LiteralPath "$terrainTargetPath.meta") -and -not (Test-Path -LiteralPath "$terrainLinkPath.meta")) {
        Copy-Item -LiteralPath "$terrainTargetPath.meta" -Destination "$terrainLinkPath.meta"
    }
}
Write-Output "Terrain preview is ready."
