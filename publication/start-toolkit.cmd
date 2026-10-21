@echo off
setlocal
cd /d "%~dp0"
if not exist "RageCoop.Server.dll" (
  echo RageCoop.Server.dll is missing. Install the compatible server first.
  exit /b 1
)
if not exist "Bootstrap\CoopAdmin.Startup.dll" (
  echo Bootstrap\CoopAdmin.Startup.dll is missing. Extract the complete addon.
  exit /b 1
)
if not exist "Bootstrap\0Harmony.dll" (
  echo Bootstrap\0Harmony.dll is missing. Extract the complete addon.
  exit /b 1
)
set "DOTNET_STARTUP_HOOKS=%~dp0Bootstrap\CoopAdmin.Startup.dll"
set "DOTNET_ROLL_FORWARD=Major"
dotnet RageCoop.Server.dll
exit /b %errorlevel%
