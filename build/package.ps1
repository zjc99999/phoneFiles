<#
.SYNOPSIS
  PhoneFiles 打包脚本：把桌面 UI 和 CLI 探针发布到 artifacts/ 目录。

.DESCRIPTION
  默认发布 framework-dependent 包（体积小，需目标机装 .NET 8 Desktop Runtime）。
  加 -SelfContained 发布 win-x64 自包含包（约 160MB，目标机免装 .NET）。
  每种包都把 rules/rules/*.yml 复制到输出目录的 rules/ 下，保证离线可加载规则。

.EXAMPLE
  .\build\package.ps1
  .\build\package.ps1 -SelfContained
  .\build\package.ps1 -Configuration Debug
#>
param(
    [switch]$SelfContained,
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$outRoot = Join-Path $root 'artifacts'
$stamp = Get-Date -Format 'yyyyMMdd-HHmm'
$outDir = Join-Path $outRoot "$(if ($SelfContained) { 'selfcontained' } else { 'framework' })-$stamp"

Write-Host "== PhoneFiles package =="
Write-Host "mode:        $(if ($SelfContained) { 'self-contained' } else { 'framework-dependent' })"
Write-Host "config:      $Configuration"
Write-Host "output:      $outDir"

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$projects = @(
    'src/PhoneFiles.Desktop/PhoneFiles.Desktop.csproj',
    'src/PhoneFiles.Probe.Console/PhoneFiles.Probe.Console.csproj'
)

$publishArgs = @('-c', $Configuration, '--nologo', '-v:q')
if ($SelfContained) {
    $publishArgs += @('-r', $Runtime, '--self-contained', 'true')
}

foreach ($proj in $projects) {
    $name = [System.IO.Path]::GetFileNameWithoutExtension($proj)
    $dest = Join-Path $outDir $name
    Write-Host "-- publish $name -> $dest"
    & dotnet publish (Join-Path $root $proj) @publishArgs -o $dest
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $proj (exit $LASTEXITCODE)" }
}

# rules are already copied by Desktop.csproj content item; ensure CLI package also has them.
foreach ($dest in (Get-ChildItem -Directory $outDir)) {
    $rulesDir = Join-Path $dest.FullName 'rules'
    if (-not (Test-Path $rulesDir)) {
        New-Item -ItemType Directory -Force -Path $rulesDir | Out-Null
        Copy-Item (Join-Path $root 'rules/rules/*.yml') $rulesDir -Force
    }
}

$all = Get-ChildItem $outDir -File -Recurse
$size = [math]::Round(($all | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host "== done =="
Write-Host "files: $($all.Count), total: $size MB"
Write-Host "desktop exe: $(Join-Path $outDir 'PhoneFiles.Desktop/PhoneFiles.Desktop.exe')"
Write-Host "cli exe:     $(Join-Path $outDir 'PhoneFiles.Probe.Console/PhoneFiles.Probe.Console.exe')"
