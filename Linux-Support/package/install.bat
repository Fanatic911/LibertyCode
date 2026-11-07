@echo off
setlocal
cd /d "%~dp0"
title LibertyCode - Installation

echo ==============================================
echo   LibertyCode - installation des dependances
echo ==============================================
echo.

set "PY="
where py >nul 2>nul && set "PY=py -3"
if not defined PY (
    where python >nul 2>nul && set "PY=python"
)
if not defined PY (
    echo [ERREUR] Python 3.10 ou plus recent est introuvable.
    echo Installe-le depuis https://www.python.org/downloads/ ^(coche "Add python.exe to PATH"^).
    pause
    exit /b 1
)

%PY% -c "import sys; sys.exit(0 if sys.version_info >= (3,10) else 1)"
if errorlevel 1 (
    echo [ERREUR] Python 3.10 ou plus recent est requis.
    %PY% --version
    pause
    exit /b 1
)

if not exist ".venv\Scripts\python.exe" (
    echo Creation de l'environnement virtuel .venv ...
    %PY% -m venv .venv
    if errorlevel 1 (
        echo [ERREUR] Impossible de creer l'environnement virtuel.
        pause
        exit /b 1
    )
)

echo Installation des paquets ^(rich, prompt_toolkit, httpx^) ...
".venv\Scripts\python.exe" -m pip install --upgrade pip
".venv\Scripts\python.exe" -m pip install -r requirements.txt
if errorlevel 1 (
    echo [ERREUR] L'installation des dependances a echoue.
    pause
    exit /b 1
)

echo.
echo Installation terminee. Lance LibertyCode avec run.bat
pause
