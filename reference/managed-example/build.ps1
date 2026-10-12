param([ValidateSet('Debug','Release')][string]$Configuration = 'Debug', [string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
$project = Join-Path $PSScriptRoot 'HMM_FPGA_UI_Example.Managed.csproj'
$reports = Join-Path $PSScriptRoot 'reports'
New-Item -ItemType Directory -Force -Path $reports | Out-Null
$output = Join-Path $PSScriptRoot "bin\x64\$Configuration\net9.0-windows\win-x64"
$buildOptions = @()
if($OutputDirectory){
    $output = [IO.Path]::GetFullPath($OutputDirectory)
    $buildOptions += "-p:OutDir=$($output.TrimEnd('\'))\"
}
$core = Join-Path $output 'HMMSysCoreAPILib.dll'
$coreBefore = if(Test-Path -LiteralPath $core){(Get-FileHash -LiteralPath $core -Algorithm SHA256).Hash}else{$null}
# Local targeting packs only; no dependency downloads, SDK installation or native SDK execution.
& $dotnet build $project -c $Configuration -p:Platform=x64 @buildOptions --nologo 2>&1 | Tee-Object -FilePath (Join-Path $reports "build-$Configuration.txt")
if ($LASTEXITCODE -ne 0) { throw "Managed $Configuration build failed." }
if($coreBefore -and (Get-FileHash -LiteralPath $core -Algorithm SHA256).Hash -ne $coreBefore){throw 'Existing operator core DLL changed during build.'}
# Internal diagnostic: build only. Do NOT launch the EXE or run automatic tests after building.
$manifest=Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\HMM_FPGA_UI_Example\sdk-baseline.json') -Raw | ConvertFrom-Json
$verification=@(foreach($file in $manifest.files) {
    $prefix='HMM_SYS_CoreEXE-20260422/'
    if(-not $file.path.StartsWith($prefix)) { continue }
    $relative=$file.path.Substring($prefix.Length)
    if(-not ($relative.StartsWith('data/') -or $relative -in @('HMMSysCoreAPILib.dll','DataInsightLib.dll','io_settings.json'))) { continue }
    $target=Join-Path $output $relative
    $actual=if(Test-Path -LiteralPath $target){(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash}else{''}
    [pscustomobject]@{File=$relative;Expected=$file.sha256;Actual=$actual;HashMatchesBaseline=($actual -eq $file.sha256);HashInformational=($relative.EndsWith('.dll'));Passed=($actual -ne '' -and ($relative.EndsWith('.dll') -or $actual -eq $file.sha256))}
})
$verification | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $reports "deployment-internal-$Configuration.json") -Encoding utf8
if($verification.Count -ne 15 -or @($verification | Where-Object {-not $_.Passed}).Count -ne 0){throw 'Deployment verification failed.'}
Get-ChildItem -LiteralPath $output -File -Recurse | ForEach-Object {
    [pscustomobject]@{ File = $_.FullName.Substring($output.Length + 1); Bytes = $_.Length; SHA256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $reports "output-$Configuration.json") -Encoding utf8
Write-Host "Managed $Configuration build and deployment presence/configuration checks passed. Native DLL hashes are informational; existing core DLL preserved. EXE not launched; tests not run. Output: $output"
