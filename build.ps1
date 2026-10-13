param([switch]$Test)
$ErrorActionPreference = 'Stop'
$projectDirectory = $PSScriptRoot
$frameworkDirectory = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compilerPath = Join-Path $frameworkDirectory 'csc.exe'
if (!(Test-Path -LiteralPath $compilerPath)) { throw '需要 Windows 的 .NET Framework 4.x 编译器。' }
$artifactDirectory = Join-Path $projectDirectory 'artifacts'
$null = New-Item -ItemType Directory -Path $artifactDirectory -Force
$sources = @(Get-ChildItem -LiteralPath (Join-Path $projectDirectory 'src') -Filter '*.cs' | ForEach-Object FullName)
$sources += Join-Path $projectDirectory 'tests\SelfTests.cs'
$references = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Xml.dll') | ForEach-Object { '/reference:' + (Join-Path $frameworkDirectory $_) }
$outputPath = Join-Path $artifactDirectory 'MemSpace.exe'
& $compilerPath /nologo /target:winexe /platform:anycpu /optimize+ /warnaserror+ /codepage:65001 ('/win32manifest:' + (Join-Path $projectDirectory 'src\app.manifest')) ('/out:' + $outputPath) $references $sources
if ($LASTEXITCODE -ne 0) { throw '主程序编译失败。' }
Copy-Item -LiteralPath (Join-Path $projectDirectory 'src\App.config') -Destination ($outputPath + '.config')
$iconPath = Join-Path $artifactDirectory 'MemSpace.ico'
$iconProcess = Start-Process -FilePath $outputPath -ArgumentList @('--export-icon', ('"' + $iconPath + '"')) -WindowStyle Hidden -Wait -PassThru
if ($iconProcess.ExitCode -ne 0) { throw '生成应用图标失败。' }
& $compilerPath /nologo /target:winexe /platform:anycpu /optimize+ /warnaserror+ /codepage:65001 ('/win32icon:' + $iconPath) ('/win32manifest:' + (Join-Path $projectDirectory 'src\app.manifest')) ('/out:' + $outputPath) $references $sources
if ($LASTEXITCODE -ne 0) { throw '写入应用图标失败。' }
& $compilerPath /nologo /target:exe /optimize+ ('/out:' + (Join-Path $artifactDirectory 'MemSpace.TestWorker.exe')) (Join-Path $projectDirectory 'tests\Worker.cs')
if ($LASTEXITCODE -ne 0) { throw '测试进程编译失败。' }
Write-Host ('已生成：' + $outputPath)
if ($Test) {
    $testOutput = Join-Path $artifactDirectory 'test-results.txt'
    $testProcess = Start-Process -FilePath $outputPath -ArgumentList @('--self-test', ('"' + $testOutput + '"')) -WindowStyle Hidden -Wait -PassThru
    Get-Content -LiteralPath $testOutput -Encoding UTF8
    if ($testProcess.ExitCode -ne 0) { throw '自动测试失败。' }
}
