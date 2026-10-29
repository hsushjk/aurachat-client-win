@echo off
chcp 65001 >nul
setlocal EnableExtensions
set "ROOT=%~dp0"

rem 编译要用带 SDK 的 .net
set "DOTNET=%USERPROFILE%\tools\dotnet\dotnet.exe"
if not exist "%DOTNET%" for /f "delims=" %%i in ('where dotnet 2^>nul') do set "DOTNET=%%i"
if not exist "%DOTNET%" (
    echo [build] 没找到 .NET SDK，先运行 _setup-toolchain.ps1
    exit /b 1
)

call "%DOTNET%" --list-sdks 2>nul | findstr /r "^9\." >nul
if errorlevel 1 (
    echo [build] 需要 .NET 9 SDK，先运行 _setup-toolchain.ps1
    exit /b 1
)

rem publish 时产物占用会报 MSB3027
tasklist /fi "imagename eq AuraChat.exe" 2>nul | findstr /i "AuraChat.exe" >nul
if not errorlevel 1 (
    echo [build] AuraChat 正在运行，先从托盘退出再编译
    exit /b 1
)

pushd "%ROOT%AuraChat.Windows"
call "%DOTNET%" publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "..\dist" %*
set "RC=%ERRORLEVEL%"
popd
if not "%RC%"=="0" (
    echo [build] 编译失败，退出码 %RC%
    exit /b %RC%
)
echo [build] 完成：dist\AuraChat.exe
exit /b 0
