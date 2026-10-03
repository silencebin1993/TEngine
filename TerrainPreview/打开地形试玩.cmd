@echo off
set "terrainPlayer=%~dp0Builds\TerrainLab\TerrainLab.exe"
if not exist "%terrainPlayer%" (
    echo Terrain preview has not been built. Open this Unity project and use the terrain build menu.
    pause
    exit /b 1
)
start "" "%terrainPlayer%" -screen-fullscreen 0 -screen-width 1600 -screen-height 900
