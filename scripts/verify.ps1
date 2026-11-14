#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$OutputRoot = (Join-Path $env:TEMP ('LeafTab-verification-' + [Guid]::NewGuid().ToString('N'))),
    [switch]$SkipNative
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$output = [IO.Path]::GetFullPath($OutputRoot).TrimEnd('\')
if ($output.Equals($repository, [StringComparison]::OrdinalIgnoreCase) -or
    $output.StartsWith($repository + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Verification output must be outside the source repository.'
}
if (Test-Path -LiteralPath $output) { throw 'Choose a new verification directory; existing evidence is never overwritten.' }
New-Item -ItemType Directory -Path $output | Out-Null
function Copy-SourceTree([string]$source, [string]$destination) {
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    foreach ($entry in Get-ChildItem -LiteralPath $source -Force) {
        if ($entry.Name -in @('bin', 'obj', '.git', '.vs')) { continue }
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Source link requires review: $($entry.FullName)" }
        $target = Join-Path $destination $entry.Name
        if ($entry.PSIsContainer) { Copy-SourceTree $entry.FullName $target }
        else { Copy-Item -LiteralPath $entry.FullName -Destination $target }
    }
}
foreach ($name in @('src', 'tests')) {
    Copy-SourceTree (Join-Path $repository $name) (Join-Path $output $name)
}
foreach ($name in @('LeafTab.sln', 'README.md', 'DESIGN.md')) {
    Copy-Item -LiteralPath (Join-Path $repository $name) -Destination $output
}
$project = Join-Path $output 'tests\LeafTab.RegressionTests\LeafTab.RegressionTests.csproj'
dotnet build $project -c Release -p:Platform=x64 -v minimal 2>&1 | Tee-Object -FilePath (Join-Path $output 'build.log')
if ($LASTEXITCODE -ne 0) { throw "Build failed. Evidence: $output" }
$previousTestRoot = $env:LEAFTAB_TEST_ROOT
try {
    $env:LEAFTAB_TEST_ROOT = Join-Path $output 'fixtures'
    & (Join-Path $output 'tests\LeafTab.RegressionTests\bin\x64\Release\net48\LeafTab.RegressionTests.exe') 2>&1 |
        Tee-Object -FilePath (Join-Path $output 'regression-tests.log')
    if ($LASTEXITCODE -ne 0) { throw "Regression tests failed. Evidence: $output" }
}
finally { $env:LEAFTAB_TEST_ROOT = $previousTestRoot }
if (-not $SkipNative) {
    $report = Join-Path $output 'native-selftest.md'
    $process = Start-Process -FilePath (Join-Path $output 'src\LeafTab\bin\x64\Release\net48\LeafTab.exe') `
        -ArgumentList ('--self-test "{0}"' -f $report) -WorkingDirectory $output -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddMinutes(3)
    while (-not $process.WaitForExit(1000)) {
        if ([DateTime]::UtcNow -gt $deadline) {
            $null = $process.CloseMainWindow()
            throw "Native self-test timed out. Only the isolated test window was asked to close. Evidence: $output"
        }
    }
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $report)) { throw "Native self-test failed. Evidence: $output" }
    if (-not (Select-String -LiteralPath $report -SimpleMatch '## RESULT: PASS' -Quiet)) { throw "Native self-test did not pass. Evidence: $output" }
}
Write-Output "Verified source copy and evidence: $output"
