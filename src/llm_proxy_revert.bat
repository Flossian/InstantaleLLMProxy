@echo off
rem ============================================================
rem llm_proxy_revert.bat  (src folder version)
rem   Remove the proxy wrapper and restore the original
rem   llama-server.exe in all backend folders.
rem   Layout: <game root>\<mod folder>\src\  (this file is in src)
rem   Normally not needed: use InstantaleLlmProxy.exe instead.
rem ============================================================
setlocal
for %%I in ("%~dp0..\..") do set "ROOT=%%~fI"

rem Match by pattern: the folder name carries the bundled llama.cpp build number
rem (llama-b7054-...), which changes whenever the game updates.
set "FOUND=0"
for /d %%D in ("%ROOT%\bin\llama-*-bin-win-*") do (
    set "FOUND=1"
    call :revert "%%~fD"
)
if "%FOUND%"=="0" echo [ERROR] no backend folder found: %ROOT%\bin\llama-*-bin-win-*

echo.
echo [DONE] reverted.
exit /b 0

:revert
set "DIR=%~1"
if exist "%DIR%\llm_proxy_dir.txt" del "%DIR%\llm_proxy_dir.txt"
if not exist "%DIR%\llama-server-real.exe" (
    echo [SKIP] not applied: %DIR%
    exit /b 0
)
del "%DIR%\llama-server.exe"
ren "%DIR%\llama-server-real.exe" "llama-server.exe"
echo [OK]   %DIR%
exit /b 0
