param([ValidateSet('Debug','Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
Push-Location $workspace
try {
    dotnet build DocAssistant.sln -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    $exe = Join-Path $workspace "DocAssistant/bin/$Configuration/DocAssistant.exe"
    $cases = @('--close-test', '--close-test --with-pdf', '--close-test --extra-window', '--close-test --while-busy', '--smoke', '--smoke', '--smoke', '--smoke', '--smoke')
    foreach ($case in $cases) {
        $process = Start-Process -FilePath $exe -ArgumentList $case -WorkingDirectory $workspace -WindowStyle Hidden -PassThru
        try {
            if (-not $process.WaitForExit(15000)) {
                throw "Shutdown timeout: $case PID=$($process.Id). Inspect the lifecycle log and process before terminating."
            }
            if ($process.ExitCode -ne 0) { throw "Failed: $case exit=$($process.ExitCode)" }
            Write-Output "PASS $case PID=$($process.Id)"
        }
        finally { $process.Dispose() }
    }
}
finally { Pop-Location }
