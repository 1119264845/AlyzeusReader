$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$outputPath = Join-Path $projectRoot 'dist'

dotnet publish (Join-Path $projectRoot 'JianRead.csproj') `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  -p:NuGetAudit=false `
  --output $outputPath

Write-Host "`n构建完成: $outputPath\阿利宙斯阅读.exe" -ForegroundColor Green
