@echo off
setlocal
set "psHost="
for %%P in (pwsh.exe) do set "psHost=%%~$PATH:P"
if not defined psHost if exist "%ProgramFiles%\PowerShell\7\pwsh.exe" set "psHost=%ProgramFiles%\PowerShell\7\pwsh.exe"
if not defined psHost if exist "%USERPROFILE%\.cache\codex-runtimes\codex-primary-runtime\dependencies\native\powershell\pwsh.exe" set "psHost=%USERPROFILE%\.cache\codex-runtimes\codex-primary-runtime\dependencies\native\powershell\pwsh.exe"
if not defined psHost (
    echo PowerShell 7.2 or newer is required.
    pause
    exit /b 1
)
"%psHost%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build.ps1" %*
set "buildExit=%errorlevel%"
echo.
if "%buildExit%"=="0" (echo Mod build completed.) else (echo Mod build failed. Exit code: %buildExit%)
pause
exit /b %buildExit%
