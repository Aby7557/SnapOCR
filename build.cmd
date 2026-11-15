@echo off
rem ============================================================
rem  SnapOCR build script
rem  Compiles SnapOCR.cs into SnapOCR.exe using the OS-bundled
rem  .NET Framework 4.x C# compiler. Zero external dependencies.
rem ============================================================
setlocal enabledelayedexpansion
cd /d "%~dp0"

rem ---- locate csc.exe ----
set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [ERROR] csc.exe not found. .NET Framework 4.x is required.
    exit /b 1
)

rem ---- locate framework directory for standard assemblies ----
for %%F in ("%CSC%") do set "FW=%%~dpF"

set "REFS="

rem ---- GAC assemblies needed for HttpClient + WinRT type identity ----
call :gac System.Runtime
call :gac System.Net.Http

rem ---- framework-dir assemblies ----
call :fw System.Web.Extensions

rem ---- all OS WinRT metadata files (Windows.Media.Ocr etc.) ----
set "WINMD="
for %%f in ("C:\Windows\System32\WinMetadata\*.winmd") do (
    set "WINMD=!WINMD! "/r:%%~ff""
)

echo Building SnapOCR.exe ...
"%CSC%" -nologo -noconfig -codepage:65001 -target:winexe -optimize+ -platform:anycpu ^
  -win32manifest:app.manifest -out:SnapOCR.exe SnapOCR.cs ^
  -r:"%FW%System.dll" ^
  -r:"%FW%System.Core.dll" ^
  -r:"%FW%System.Drawing.dll" ^
  -r:"%FW%System.Windows.Forms.dll" ^
  %REFS% %WINMD%

if errorlevel 1 (
    echo.
    echo [ERROR] Build failed.
    exit /b 1
)

echo.
echo [OK] SnapOCR.exe built successfully.
dir SnapOCR.exe | find "SnapOCR.exe"
exit /b 0

:gac
rem find an assembly in GAC_MSIL and add it to REFS
for /d %%d in ("C:\Windows\Microsoft.NET\assembly\GAC_MSIL\%~1\v4.0_*") do (
    if exist "%%d\%~1.dll" set "REFS=!REFS! "/r:%%d\%~1.dll""
)
goto :eof

:fw
rem reference an assembly from the framework directory
if exist "%FW%%~1.dll" set "REFS=!REFS! "/r:%FW%%~1.dll""
goto :eof
