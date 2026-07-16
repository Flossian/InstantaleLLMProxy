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
set "SRCDIR=%~dp0proxy"
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

echo [BUILD] %SRCDIR%\*.cs
"%CSC%" /nologo /optimize /codepage:65001 /out:"%OUT%" "%SRCDIR%\*.cs"
if errorlevel 1 (
    echo [ERROR] build failed
    pause
    exit /b 1
)

rem The folder name carries the bundled llama.cpp build number (llama-b7054-...),
rem which changes whenever the game updates, so match by pattern instead of
rem hardcoding the names.
set "FOUND=0"
for /d %%D in ("%ROOT%\bin\llama-*-bin-win-*") do (
    set "FOUND=1"
    call :apply "%%~fD"
)
if "%FOUND%"=="0" (
    echo [ERROR] no backend folder found: %ROOT%\bin\llama-*-bin-win-*
    pause
    exit /b 1
)

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
