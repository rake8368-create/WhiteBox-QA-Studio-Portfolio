[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$DotNetPath = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$app = Join-Path $repository 'src/WhiteBoxQA.App/WhiteBoxQA.App.csproj'
Push-Location -LiteralPath $repository
try {
    & $DotNetPath run --project $app --configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw "개발 실행에 실패했습니다 (종료 코드 $LASTEXITCODE)." }
}
finally {
    Pop-Location
}
