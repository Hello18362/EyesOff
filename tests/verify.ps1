$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$bin = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force -Path $bin | Out-Null
$exe = Join-Path $bin 'ComponentTests.exe'
& $csc /nologo /codepage:65001 /target:exe /main:EyesOff.ComponentTests "/win32icon:$root\EyesOff.ico" "/win32manifest:$root\EyesOff.manifest" "/out:$exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:System.dll "$root\EyesOff.cs" "$PSScriptRoot\ComponentTests.cs"
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed' }
& $exe (Join-Path $bin 'previews') | Tee-Object -FilePath (Join-Path $bin 'component-test-result.txt')
if ($LASTEXITCODE -ne 0) { throw 'Component checks failed' }
