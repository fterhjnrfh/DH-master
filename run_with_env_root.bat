@echo off
setlocal

REM --- Locate VS C++ environment ---
set "VS_PATH="
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"

REM Prefer official Visual Studio discovery when available.
if exist "%VSWHERE%" (
    for /f "usebackq delims=" %%I in (`"%VSWHERE%" -latest -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find VC\Auxiliary\Build\vcvars64.bat`) do (
        if "%VS_PATH%"=="" set "VS_PATH=%%I"
    )
)

if exist "C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat" set "VS_PATH=C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat"
if "%VS_PATH%"=="" if exist "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat" set "VS_PATH=C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
if "%VS_PATH%"=="" if exist "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\VC\Auxiliary\Build\vcvars64.bat" set "VS_PATH=C:\Program Files\Microsoft Visual Studio\2022\Enterprise\VC\Auxiliary\Build\vcvars64.bat"
if "%VS_PATH%"=="" if exist "C:\Program Files\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat" set "VS_PATH=C:\Program Files\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
if "%VS_PATH%"=="" if exist "D:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat" set "VS_PATH=D:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
if "%VS_PATH%"=="" if exist "C:\Program Files (x86)\Microsoft Visual Studio\2019\Professional\VC\Auxiliary\Build\vcvars64.bat" set "VS_PATH=C:\Program Files (x86)\Microsoft Visual Studio\2019\Professional\VC\Auxiliary\Build\vcvars64.bat"
if "%VS_PATH%"=="" if exist "C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\VC\Auxiliary\Build\vcvars64.bat" set "VS_PATH=C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\VC\Auxiliary\Build\vcvars64.bat"
if "%VS_PATH%"=="" if exist "C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\VC\Auxiliary\Build\vcvars64.bat" set "VS_PATH=C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\VC\Auxiliary\Build\vcvars64.bat"

if "%VS_PATH%"=="" (
    echo [WARN] Visual Studio C++ build environment not found.
    echo Continue without vcvars...
) else (
    echo Using Visual Studio environment: "%VS_PATH%"
    call "%VS_PATH%"
)

REM --- Move to repository root (this script location) ---
cd /d "%~dp0"

if "%DH_DATA_ROOT%"=="" set "DH_DATA_ROOT=%CD%\data"
if "%DH_SDK_CONFIG%"=="" if exist "%CD%\config" set "DH_SDK_CONFIG=%CD%\config"

REM Clear variables to avoid VS script residue affecting dotnet build
set Platform=
set PlatformTarget=

REM Clean stale x64 outputs (same as DH.UI helper script)
if exist "src\DH.UI\AlgorithmModule\obj\x64" rd /s /q "src\DH.UI\AlgorithmModule\obj\x64"
if exist "src\DH.UI\AlgorithmModule\bin\x64" rd /s /q "src\DH.UI\AlgorithmModule\bin\x64"

REM Release output does not contain the vendor SDK dependency set. Keep running the
REM app in Release, but allow P/Invoke to resolve the existing SDK DLL folder.
set "SDK_BIN=%CD%\bin\Release\net6.0-windows7.0"
if exist "%SDK_BIN%\Hardware_Standard_C_Interface.dll" (
    set "PATH=%SDK_BIN%;%PATH%"
    echo Using SDK dependency folder: "%SDK_BIN%"
) else (
    echo [WARN] SDK dependency folder not found: "%SDK_BIN%"
)

REM Uncomment to do a full clean build when needed
REM dotnet clean DH.sln

REM Build with one MSBuild node; copied workspaces have shown flaky project-reference
REM probing with parallel restore/build.
dotnet build DH.AppHost.csproj -c Release -m:1
if errorlevel 1 goto :run_failed

REM Start main app (DH.AppHost) in Release mode, pass through extra args
"%CD%\bin\Release\net6.0-windows7.0\DH.AppHost.exe" %*
if errorlevel 1 goto :run_failed

goto :end

:run_failed
echo .
echo dotnet run failed. Check output for details.

:end
endlocal
pause
