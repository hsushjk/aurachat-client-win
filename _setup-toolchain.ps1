param(
    [string]$ToolsDir = (Join-Path $env:USERPROFILE 'tools'),
    [string]$Channel = '9.0'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$dotnetDir = Join-Path $ToolsDir 'dotnet'
$dotnetExe = Join-Path $dotnetDir 'dotnet.exe'
New-Item -ItemType Directory -Path $ToolsDir -Force | Out-Null

if (-not (Test-Path $dotnetExe)) {
    Write-Host "下载 .NET SDK $Channel ..."
    $installer = Join-Path $ToolsDir 'dotnet-install.ps1'
    Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
    & powershell -NoProfile -ExecutionPolicy Bypass -File $installer -Channel $Channel -InstallDir $dotnetDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet-install 失败（退出码 $LASTEXITCODE）" }
}

$sdks = & $dotnetExe --list-sdks
if (-not ($sdks -match '^9\.')) { throw "没装到 .NET 9 SDK，删掉 $dotnetDir 再试" }
Write-Host "dotnet：$dotnetDir"
$sdks | ForEach-Object { Write-Host "        $_" }

Write-Host ''
