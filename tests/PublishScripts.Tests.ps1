#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$CiExe,
    [string]$AppAssembly
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$fixture = (Resolve-Path -LiteralPath $CiExe).ProviderPath
$fixtureHash = (Get-FileHash -LiteralPath $fixture).Hash
if (!$AppAssembly) {
    $AppAssembly = Join-Path $repository 'src/WhiteBoxQA.App/bin/Release/net10.0-windows/WhiteBoxQA.App.dll'
}

[xml]$props = Get-Content -LiteralPath (Join-Path $repository 'Directory.Build.props') -Raw
$expectedVersion = $props.Project.PropertyGroup.Version
foreach ($binary in $fixture, $AppAssembly) {
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo($binary).ProductVersion -cne $expectedVersion) {
        throw "프로그램 버전이 Directory.Build.props와 다릅니다. $binary"
    }
}

$temp = Get-Item -LiteralPath ([IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetTempPath())) -Force
$sandbox = Join-Path $temp.FullName ('whiteboxqa-script-test-' + [guid]::NewGuid().ToString('N'))
$testRepo = Join-Path $sandbox '게시 테스트'
$scripts = Join-Path $testRepo 'scripts'
$elsewhere = Join-Path $sandbox '다른 작업 폴더'
$extractTemp = Join-Path $sandbox '추출 경로'
$foreignBundle = Join-Path $extractTemp '.whiteboxqa-bundle-abcdef'
$foreignFile = Join-Path $foreignBundle '외부 추출 파일.txt'
[void][IO.Directory]::CreateDirectory($scripts)
[void][IO.Directory]::CreateDirectory($elsewhere)
[void][IO.Directory]::CreateDirectory($foreignBundle)
Set-Content -LiteralPath $foreignFile -Value '보존할 외부 추출 파일'
$foreignHash = (Get-FileHash -LiteralPath $foreignFile).Hash
$scriptFiles = (Join-Path $repository 'scripts/run-dev.ps1'), (Join-Path $repository 'scripts/publish-win-x64.ps1')
Copy-Item -LiteralPath $scriptFiles -Destination $scripts
$fake = Join-Path $testRepo 'dotnet-test.ps1'
@'
$ErrorActionPreference = 'Stop'
$app = Join-Path $PSScriptRoot 'src/WhiteBoxQA.App/WhiteBoxQA.App.csproj'
if ((Get-Location).ProviderPath -cne $PSScriptRoot) { throw 'SDK working directory differs from repository' }
if ($args[0] -eq 'run') {
    if ($args[1] -ne '--project' -or $args[2] -cne $app -or $args[4] -cne 'Release') { exit 71 }
    'run-dev validated'
    exit 0
}
if ($args[0] -ne 'publish' -or $args[1] -ne $app -or '-p:PublishProfile=WinX64SingleFile' -cnotin $args) {
    throw "Unexpected publish arguments: $($args -join ' | ')"
}
$output = $args[([Array]::IndexOf($args, '-o') + 1)]
$exe = Join-Path $output 'WhiteBoxQA.App.exe'
switch ($env:WHITEBOXQA_TEST_PUBLISH_MODE) {
    'failed' { Set-Content -LiteralPath $exe -Value 'partial'; exit 42 }
    'invalid' { Set-Content -LiteralPath $exe -Value 'invalid'; exit 0 }
    'smoke-failed' { Copy-Item -LiteralPath (Join-Path $env:SystemRoot 'System32/where.exe') -Destination $exe; exit 0 }
    default { Copy-Item -LiteralPath $env:WHITEBOXQA_TEST_PUBLISH_EXE -Destination $exe; exit 0 }
}
'@ | Set-Content -LiteralPath $fake

$publish = Join-Path $scripts 'publish-win-x64.ps1'
$finalExe = Join-Path $testRepo 'dist/current/win-x64/WhiteBoxQA.App.exe'
function Assert-PreviousExe {
    if ((Get-FileHash -LiteralPath $finalExe).Hash -ne $fixtureHash) { throw 'Previous valid EXE changed' }
}
function Assert-PublishFails([string]$Pattern) {
    try {
        & $publish -DotNetPath $fake
    }
    catch {
        if ($_.Exception.Message -notmatch $Pattern) { throw }
        return
    }
    throw 'Expected publish failure'
}
function Assert-TemporaryState {
    if ($env:DOTNET_BUNDLE_EXTRACT_BASE_DIR -cne $foreignBundle -or
        $env:TEMP -cne $extractTemp -or $env:TMP -cne $extractTemp) {
        throw 'Publishing changed the caller environment'
    }
    if ((Get-FileHash -LiteralPath $foreignFile).Hash -ne $foreignHash) {
        throw 'Existing extraction files changed'
    }
    $bundles = @(Get-ChildItem -LiteralPath $extractTemp -Force)
    $staging = @(Get-ChildItem -LiteralPath (Join-Path $testRepo 'dist') -Filter '.whiteboxqa-publish-*' -Force)
    if ($bundles.Count -ne 1 -or $bundles[0].FullName -cne $foreignBundle -or $staging.Count) {
        throw 'Publishing left temporary folders'
    }
}

$savedEnvironment = @{}
foreach ($name in 'TEMP', 'TMP', 'DOTNET_BUNDLE_EXTRACT_BASE_DIR', 'WHITEBOXQA_TEST_PUBLISH_EXE', 'WHITEBOXQA_TEST_PUBLISH_MODE') {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}
Push-Location -LiteralPath $elsewhere
try {
    $env:TEMP = $env:TMP = $extractTemp
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $foreignBundle
    $env:WHITEBOXQA_TEST_PUBLISH_EXE = $fixture
    $dev = & (Join-Path $scripts 'run-dev.ps1') -DotNetPath $fake -Configuration Release
    if ($dev -cne 'run-dev validated') { throw 'Developer launcher did not use the expected root and project' }

    $env:WHITEBOXQA_TEST_PUBLISH_MODE = 'valid'
    $output = @(& $publish -DotNetPath $fake -Verbose 4>&1)
    if (!($output | Where-Object { $_ -is [string] -and $_ -like "*$finalExe" })) {
        throw 'Final output path was not reported'
    }
    $smokeRuns = @($output | Where-Object { $_ -is [Management.Automation.VerboseRecord] -and $_.Message -like '검증 완료 · --*' })
    $languages = @('python', 'java', 'javascript', 'cpp', 'typescript')
    if ($smokeRuns.Count -ne $languages.Count) { throw 'Unexpected parser smoke count' }
    $ownedBundle = $null
    for ($index = 0; $index -lt $languages.Count; $index++) {
        $language = $languages[$index]
        $label, $argument, $extractPath = $smokeRuns[$index].Message -split ' · ', 3
        $bundle = Split-Path -Parent $extractPath
        if ($argument -cne "--$language-parser-smoke-test" -or (Split-Path -Leaf $extractPath) -cne $language -or
            (Split-Path -Parent $bundle) -cne $extractTemp -or (Split-Path -Leaf $bundle) -notmatch '^\.whiteboxqa-bundle-' -or
            ($null -ne $ownedBundle -and $bundle -cne $ownedBundle)) {
            throw "Unexpected smoke argument or extraction path: $argument, $extractPath"
        }
        $ownedBundle = $bundle
    }
    Assert-PreviousExe
    Assert-TemporaryState
    $items = @(Get-ChildItem -LiteralPath (Split-Path -Parent $finalExe) -Force)
    if ($items.Count -ne 1 -or $items[0].FullName -cne $finalExe) { throw 'Unexpected local publish files' }
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo($finalExe).ProductVersion -cne $expectedVersion) {
        throw 'Version differs between the CI and local output paths'
    }

    $env:WHITEBOXQA_TEST_PUBLISH_MODE = 'failed'
    Assert-PublishFails '42'
    Assert-PreviousExe
    $env:WHITEBOXQA_TEST_PUBLISH_MODE = 'invalid'
    Assert-PublishFails 'Windows 실행 파일 형식'
    Assert-PreviousExe
    $env:WHITEBOXQA_TEST_PUBLISH_MODE = 'smoke-failed'
    Assert-PublishFails '검증에 실패했습니다.*--python-parser-smoke-test'
    Assert-PreviousExe
    Assert-TemporaryState

    $unrelated = Join-Path (Split-Path -Parent $finalExe) '사용자 파일.txt'
    Set-Content -LiteralPath $unrelated -Value '보존할 파일'
    Assert-PublishFails '다른 항목'
    Assert-PreviousExe
    if (!(Test-Path -LiteralPath $unrelated) -or (Get-FileHash -LiteralPath $fixture).Hash -ne $fixtureHash) {
        throw 'Existing user or CI output changed'
    }
    Assert-TemporaryState
    '로컬 실행·게시, 이전 EXE 보존, CI 경로 격리 및 프로그램 버전 확인 완료'
}
finally {
    Pop-Location
    foreach ($name in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name])
    }
    $owned = Get-Item -LiteralPath $sandbox -Force
    if ($owned.Parent.FullName -eq $temp.FullName -and $owned.Name -match '^whiteboxqa-script-test-[0-9a-f]{32}$' -and
        !$owned.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
        [IO.Directory]::Delete($owned.FullName, $true)
    }
}
