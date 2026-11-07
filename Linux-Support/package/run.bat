@echo off
setlocal
cd /d "%~dp0"
if not exist ".venv\Scripts\python.exe" (
    echo Environnement introuvable : lance d'abord install.bat
    pause
    exit /b 1
)
rem Sans argument : demarre sur /project. "run.bat ." utilise le dossier courant du .bat.
".venv\Scripts\python.exe" -m liberty_code %*
