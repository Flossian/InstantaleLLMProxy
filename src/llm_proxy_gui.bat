@echo off
rem ============================================================
rem llm_proxy_gui.bat  (src folder version)
rem   Rebuild InstantaleLlmProxy.exe (manager GUI) from
rem   llm_proxy_gui.cs and launch it. The exe is placed in the
rem   mod folder (parent of src). For developers; end users can
rem   just run the bundled InstantaleLlmProxy.exe.
rem ============================================================
setlocal
for %%I in ("%~dp0..") do set "MOD=%%~fI"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "SRC=%~dp0llm_proxy_gui.cs"
set "OUT=%MOD%\InstantaleLlmProxy.exe"

if not exist "%CSC%" (
    echo [ERROR] csc.exe not found: %CSC%
    pause
    exit /b 1
)

"%CSC%" /nologo /optimize /codepage:65001 /target:winexe /r:System.Windows.Forms.dll /r:System.Drawing.dll /out:"%OUT%" "%SRC%"
if errorlevel 1 (
    echo [ERROR] build failed
    pause
    exit /b 1
)

start "" "%OUT%"
exit /b 0
