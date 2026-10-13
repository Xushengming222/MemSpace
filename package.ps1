$ErrorActionPreference = 'Stop'
$projectDirectory = $PSScriptRoot
$artifactDirectory = Join-Path $projectDirectory 'artifacts'
$releaseDirectory = Join-Path $projectDirectory 'release\MemSpace-1.3.1'
$null = New-Item -ItemType Directory -Path $releaseDirectory -Force
foreach ($name in @('MemSpace.exe', 'MemSpace.exe.config')) {
    $sourcePath = Join-Path $artifactDirectory $name
    if (!(Test-Path -LiteralPath $sourcePath)) { throw ('请先运行 build.ps1：缺少 ' + $name) }
    Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $releaseDirectory $name)
}
Copy-Item -LiteralPath (Join-Path $projectDirectory 'docs\快速使用.txt') -Destination $releaseDirectory
Copy-Item -LiteralPath (Join-Path $projectDirectory 'LICENSE') -Destination $releaseDirectory
$zipPath = Join-Path $artifactDirectory 'MemSpace-1.3.1.zip'
Compress-Archive -LiteralPath $releaseDirectory -DestinationPath $zipPath -Force
$hash = Get-FileHash -LiteralPath (Join-Path $releaseDirectory 'MemSpace.exe') -Algorithm SHA256
Write-Output ('运行程序：' + (Join-Path $releaseDirectory 'MemSpace.exe'))
Write-Output ('便携包：' + $zipPath)
Write-Output ('SHA256：' + $hash.Hash)
