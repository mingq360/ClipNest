# 打包 ClipNest 成单文件 exe
#
#   默认自包含：目标机器不需要安装 .NET，单个 ClipNest.exe 拷过去就能跑（约 65 MB）
#   -FrameworkDependent：只依赖已安装的 .NET 8 桌面运行时，体积小得多（约 3 MB）
#
#   用法：
#     .\publish.ps1
#     .\publish.ps1 -FrameworkDependent
#     .\publish.ps1 -Runtime win-arm64

param(
    [string]$Runtime = "win-x64",
    [switch]$FrameworkDependent
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "src/ClipNest/ClipNest.csproj"
$output = Join-Path $root "artifacts/publish/$Runtime"

# WPF 不支持 PublishTrimmed，所以别加裁剪参数；单文件靠压缩把体积压下来
$publishArgs = @(
    "publish", $project,
    "-c", "Release",
    "-r", $Runtime,
    "--self-contained", (-not $FrameworkDependent).ToString().ToLowerInvariant(),
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "-p:DebugType=none",
    "-o", $output,
    "--nologo"
)

Write-Host "打包中：$Runtime（$(if ($FrameworkDependent) { '框架依赖' } else { '自包含' })）" -ForegroundColor Cyan

if (Test-Path $output) { Remove-Item $output -Recurse -Force }
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败，退出码 $LASTEXITCODE" }

$exe = Join-Path $output "ClipNest.exe"
if (-not (Test-Path $exe)) { throw "没有找到产物：$exe" }

$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ""
Write-Host "完成：$exe" -ForegroundColor Green
Write-Host "体积：$sizeMb MB"
Write-Host "该目录下只有 exe 一个文件，可以直接分发。"
