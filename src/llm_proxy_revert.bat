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

call :revert "%ROOT%\bin\llama-b7054-bin-win-cpu-x64"
call :revert "%ROOT%\bin\llama-b7054-bin-win-cuda-12.4-x64"
call :revert "%ROOT%\bin\llama-b7054-bin-win-vulkan-x64"

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
