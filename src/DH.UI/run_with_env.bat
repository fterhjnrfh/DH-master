@echo off
setlocal

REM 自动定位 Visual Studio C++ 环境脚本
set "VS_PATH="
if exist "C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat" set "VS_PATH=C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat"
if "%VS_PATH%"=="" if exist "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat" set "VS_PATH=C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
if "%VS_PATH%"=="" if exist "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\VC\Auxiliary\Build\vcvars64.bat" set "VS_PATH=C:\Program Files\Microsoft Visual Studio\2022\Enterprise\VC\Auxiliary\Build\vcvars64.bat"
if "%VS_PATH%"=="" if exist "D:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat" set "VS_PATH=D:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
if "%VS_PATH%"=="" if exist "C:\Program Files (x86)\Microsoft Visual Studio\2019\Professional\VC\Auxiliary\Build\vcvars64.bat" set "VS_PATH=C:\Program Files (x86)\Microsoft Visual Studio\2019\Professional\VC\Auxiliary\Build\vcvars64.bat"
if "%VS_PATH%"=="" if exist "C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\VC\Auxiliary\Build\vcvars64.bat" set "VS_PATH=C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\VC\Auxiliary\Build\vcvars64.bat"

if "%VS_PATH%"=="" (
	echo 未找到 Visual Studio C++ 编译器环境，请编辑 run_with_env.bat 指向 vcvars64.bat
	pause
	exit /b 1
)

call "%VS_PATH%"

REM 切换到脚本所在目录（项目根目录）
cd /d "%~dp0"

set Platform=
set PlatformTarget=

REM 清理解耦，防止从旧机器残留的 x64 输出影响当前环境
if exist "AlgorithmModule\obj\x64" rd /s /q "AlgorithmModule\obj\x64"
if exist "AlgorithmModule\bin\x64" rd /s /q "AlgorithmModule\bin\x64"

dotnet clean
dotnet run

endlocal
pause

