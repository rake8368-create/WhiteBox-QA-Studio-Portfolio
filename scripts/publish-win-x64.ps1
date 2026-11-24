#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$DotNetPath = 'dotnet',
    [switch]$OpenFolder
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$app = Join-Path $repository 'src/WhiteBoxQA.App/WhiteBoxQA.App.csproj'
$dist = Join-Path $repository 'dist'
$current = Join-Path $dist 'current'
$destination = Join-Path $current 'win-x64'
$exeName = 'WhiteBoxQA.App.exe'
$finalExe = Join-Path $destination $exeName

function Initialize-PublishDirectory([string]$Path) {
    $directory = [IO.Directory]::CreateDirectory($Path)
    if ($directory.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
        throw "게시 경로가 링크를 가리킵니다. $Path"
    }
}

function Remove-PublishWorkDirectory([string]$Path, [string]$ParentPath, [string]$NamePattern) {
    try {
        $resolved = Get-Item -LiteralPath $Path -Force
        $parent = Get-Item -LiteralPath ([IO.Path]::TrimEndingDirectorySeparator($ParentPath)) -Force
        if ($resolved.PSIsContainer -and !$resolved.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint) -and
            !$parent.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint) -and $resolved.Parent.FullName -eq $parent.FullName -and
            $resolved.Name -match $NamePattern) {
            [IO.Directory]::Delete($resolved.FullName, $true)
            return
        }
        Write-Warning "게시 작업 폴더의 경로를 검증하지 못했습니다. $Path"
    }
    catch {
        Write-Warning "게시 작업 폴더를 정리하지 못했습니다. $Path"
    }
}

function Test-PublishFile($Item) {
    $Item.Name -ceq $exeName -and !$Item.PSIsContainer -and
        !$Item.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)
}

function Assert-WindowsX64([string]$Path) {
    $reader = [IO.BinaryReader]::new([IO.File]::OpenRead($Path))
    try {
        if ($reader.BaseStream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5A4D) {
            throw '게시 파일이 Windows 실행 파일 형식이 아닙니다.'
        }
        $reader.BaseStream.Position = 0x3C
        $header = $reader.ReadInt32()
        if ($header -lt 64 -or $header -gt ($reader.BaseStream.Length - 6)) {
            throw '게시 파일의 PE 헤더 위치가 유효하지 않습니다.'
        }
        $reader.BaseStream.Position = $header
        if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664) {
            throw '게시 파일의 Windows x64 PE 헤더를 확인할 수 없습니다.'
        }
    }
    finally {
        $reader.Dispose()
    }
}

function Invoke-ParserSmoke([string]$Path, [string]$Argument, [string]$BundleDirectory) {
    $start = [Diagnostics.ProcessStartInfo]::new($Path, $Argument)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WorkingDirectory = Split-Path -Parent $Path
    $start.Environment['PATH'] = Join-Path $env:SystemRoot 'System32'
    $start.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = $BundleDirectory
    foreach ($variable in 'JAVA_HOME', 'PYTHONHOME', 'PYTHONPATH') {
        [void]$start.Environment.Remove($variable)
    }
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (!$process.WaitForExit(60000)) {
            $process.Kill()
            $process.WaitForExit()
            throw "게시 파일 검증 시간이 초과되었습니다. $Argument"
        }
        if ($process.ExitCode -ne 0) {
            throw "게시 파일 검증에 실패했습니다. $Argument (종료 코드 $($process.ExitCode))"
        }
    }
    finally {
        $process.Dispose()
    }
}

function Initialize-PublishDestination {
    Initialize-PublishDirectory $dist
    Initialize-PublishDirectory $current
    Initialize-PublishDirectory $destination
    $existing = @(Get-ChildItem -LiteralPath $destination -Force)
    if ($existing.Count -gt 1 -or ($existing.Count -eq 1 -and !(Test-PublishFile $existing[0]))) {
        throw "현재 게시 폴더에 다른 항목이 있습니다. 파일을 보존하고 게시를 중단했습니다. $destination"
    }
}

if (!$IsWindows) { throw 'Windows에서 실행하세요.' }
Initialize-PublishDestination

$staging = Join-Path $dist ('.whiteboxqa-publish-' + [guid]::NewGuid().ToString('N'))
$stagedPublish = Join-Path $staging 'publish'
$temp = [IO.Path]::GetTempPath()
$bundleDirectory = $null
Initialize-PublishDirectory $stagedPublish
Push-Location -LiteralPath $repository
try {
    $publishArguments = @('publish', $app, '-c', 'Release', '-p:PublishProfile=WinX64SingleFile', '-o', $stagedPublish, '-warnaserror')
    & $DotNetPath @publishArguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish에 실패했습니다 (종료 코드 $LASTEXITCODE)." }

    $files = @(Get-ChildItem -LiteralPath $stagedPublish -Force)
    if ($files.Count -ne 1 -or !(Test-PublishFile $files[0])) {
        throw '게시 결과는 WhiteBoxQA.App.exe 파일 한 개여야 합니다.'
    }
    $stagedExe = $files[0].FullName
    Assert-WindowsX64 $stagedExe
    # Create an owned temp directory away from staging and extract afresh for every smoke.
    $bundleDirectory = [IO.Directory]::CreateTempSubdirectory('.whiteboxqa-bundle-')
    foreach ($language in 'python', 'java', 'javascript', 'cpp', 'typescript') {
        $extractPath = Join-Path $bundleDirectory.FullName $language
        Invoke-ParserSmoke $stagedExe "--$language-parser-smoke-test" $extractPath
        Write-Verbose "검증 완료 · --$language-parser-smoke-test · $extractPath"
    }

    # Stage and destination are on the same volume. Replace in one filesystem operation.
    Initialize-PublishDestination
    [IO.File]::Move($stagedExe, $finalExe, $true)
}
finally {
    Pop-Location
    if ($bundleDirectory) {
        Remove-PublishWorkDirectory $bundleDirectory.FullName $temp '^\.whiteboxqa-bundle-[a-z0-9.]+$'
    }
    Remove-PublishWorkDirectory $staging $dist '^\.whiteboxqa-publish-[0-9a-f]{32}$'
}

Write-Output "게시 완료 · $finalExe"
if ($OpenFolder) { Invoke-Item -LiteralPath $destination }
