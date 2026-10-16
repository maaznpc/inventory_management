$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repositoryRoot
try {
    dotnet test tests/NaeemTractors.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw "Business tests failed." }
    $output = Join-Path $repositoryRoot "artifacts/windows-x64"
    if (Test-Path $output) { Remove-Item $output -Recurse -Force }
    dotnet publish src/NaeemTractors.Desktop -c Release -r win-x64 --self-contained true -o $output -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw "Windows publishing failed." }
    Copy-Item README.md $output
    $archive = Join-Path $repositoryRoot "artifacts/NaeemTractors-Windows-x64.zip"
    Compress-Archive -Path "$output/*" -DestinationPath $archive -Force
    Write-Host "Windows distribution: $archive"
} finally { Pop-Location }
