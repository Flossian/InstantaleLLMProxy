@echo off
rem ============================================================
rem llm_proxy_apply.bat  (src folder version)
rem   Build llm_proxy.cs and install it as llama-server.exe.
rem   The original server is kept as llama-server-real.exe.
rem   Layout: <game root>\<mod folder>\src\  (this file is in src)
rem   Normally not needed: use InstantaleLlmProxy.exe instead.
rem ============================================================
setlocal
for %%I in ("%~dp0..") do set "MOD=%%~fI"
for %%I in ("%~dp0..\..") do set "ROOT=%%~fI"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "SRC=%~dp0llm_proxy.cs"
set "OUT=%~dp0llm_proxy_wrapper.exe"

if not exist "%CSC%" (
    echo [ERROR] csc.exe not found: %CSC%
    pause
    exit /b 1
)
if not exist "%ROOT%\bin" (
    echo [ERROR] game bin folder not found: %ROOT%\bin
    echo         Put the mod folder directly under the game folder.
    pause
    exit /b 1
)

echo [BUILD] %SRC%
"%CSC%" /nologo /optimize /codepage:65001 /out:"%OUT%" "%SRC%"
if errorlevel 1 (
    echo [ERROR] build failed
    pause
    exit /b 1
)

call :apply "%ROOT%\bin\llama-b7054-bin-win-cpu-x64"
call :apply "%ROOT%\bin\llama-b7054-bin-win-cuda-12.4-x64"
call :apply "%ROOT%\bin\llama-b7054-bin-win-vulkan-x64"

echo.
echo [DONE] llm_proxy applied. Rules: %MOD%\llm_replacements.txt
exit /b 0

:apply
set "DIR=%~1"
if not exist "%DIR%\llama-server.exe" (
    echo [SKIP] no llama-server.exe: %DIR%
    exit /b 0
)
if not exist "%DIR%\llama-server-real.exe" (
    ren "%DIR%\llama-server.exe" "llama-server-real.exe"
    if errorlevel 1 (
        echo [ERROR] rename failed: %DIR%
        exit /b 1
    )
)
copy /y "%OUT%" "%DIR%\llama-server.exe" >nul
rem Record the mod folder location so the wrapper can find the rules file
rem regardless of the mod folder's name or the game root layout.
>"%DIR%\llm_proxy_dir.txt" echo %MOD%
echo [OK]   %DIR%
exit /b 0
