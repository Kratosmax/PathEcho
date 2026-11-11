#requires -Version 7.0
param(
    [Parameter(Mandatory=$true)][ValidatePattern('^v?[0-9]+\.[0-9]+\.[0-9]+$')][string]$CurrentTag,
    [Parameter(Mandatory=$true)][ValidatePattern('^v?[0-9]+\.[0-9]+\.[0-9]+$')][string]$PreviousTag
)
$ErrorActionPreference = "Stop"
$repository = Split-Path -Parent $PSScriptRoot
$current = $CurrentTag.TrimStart('v')
$previous = $PreviousTag.TrimStart('v')
$evidence = Join-Path $repository ("temp/upgrade-acceptance-" + [guid]::NewGuid().ToString('N'))
$currentRoot = Join-Path $evidence "current"
$previousRoot = Join-Path $evidence "previous"
New-Item -ItemType Directory -Path $currentRoot,$previousRoot | Out-Null
function Download-OriginalAssets([string]$Tag, [string]$Version, [string]$Directory) {
    # Drafts are not reliably addressable through the public by-tag endpoint.
    $metadata = gh api "repos/Kratosmax/PathEcho/releases?per_page=100"
    if ($LASTEXITCODE -ne 0) { throw "Unable to list authenticated releases." }
    $release = @($metadata | ConvertFrom-Json) | Where-Object { $_.tag_name -eq $Tag } | Select-Object -First 1
    if ($null -eq $release) { throw "The signed release $Tag is not visible to this job's repository token." }
    foreach($name in @("PathEcho-$Version-Lite.zip", "PathEcho-$Version-Full.zip", "update-lite.json", "update-full.json", "update.json", "SHA256SUMS.txt")) {
        $asset = @($release.assets | Where-Object { $_.name -eq $name })
        if ($asset.Count -ne 1) { throw "Missing or duplicate release asset: $name" }
        $destination = Join-Path $Directory $name
        $start = [Diagnostics.ProcessStartInfo]::new((Get-Command gh.exe -ErrorAction Stop).Source)
        $start.UseShellExecute = $false
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        foreach($argument in @("api",("repos/Kratosmax/PathEcho/releases/assets/"+$asset[0].id),"-H","Accept: application/octet-stream")){$start.ArgumentList.Add($argument)}
        $process = [Diagnostics.Process]::Start($start)
        $errors = $process.StandardError.ReadToEndAsync()
        $output = [IO.File]::Open($destination,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try { $process.StandardOutput.BaseStream.CopyTo($output) } finally { $output.Dispose() }
        $process.WaitForExit()
        if($process.ExitCode -ne 0){throw "Asset download failed: $name :: $($errors.GetAwaiter().GetResult())"}
        if((Get-Item -LiteralPath $destination).Length -ne $asset[0].size){throw "Incomplete asset: $name"}
        $hash=(Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
        if($asset[0].digest -and ("sha256:"+$hash.ToLowerInvariant()) -ne $asset[0].digest){throw "Release API digest mismatch: $name"}
        Write-Output "VERIFIED_DOWNLOAD: $name"
        $process.Dispose()
    }
}
Download-OriginalAssets "v$current" $current $currentRoot
Download-OriginalAssets "v$previous" $previous $previousRoot
foreach($channel in @("Lite","Full")) {
    foreach($spec in @(@($currentRoot,$current),@($previousRoot,$previous))) {
        $manifest = Get-Content -Raw -LiteralPath (Join-Path $spec[0] ("update-"+$channel.ToLowerInvariant()+".json")) | ConvertFrom-Json
        $zip = Join-Path $spec[0] ("PathEcho-"+$spec[1]+"-"+$channel+".zip")
        if((Get-Item -LiteralPath $zip).Length -ne $manifest.PackageSize -or (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $manifest.Sha256){throw "Published original package hash/size mismatch."}
    }
    Expand-Archive -LiteralPath (Join-Path $previousRoot ("PathEcho-$previous-$channel.zip")) -DestinationPath (Join-Path $previousRoot $channel)
}
$oldCore = Join-Path $previousRoot "Lite/PathEcho.Core.dll"
dotnet build (Join-Path $repository "tests/PathEcho.UpgradeTests/PathEcho.UpgradeTests.csproj") -c Release "-p:PreviousCorePath=$oldCore" -m:1 -v:minimal
if($LASTEXITCODE -ne 0){throw "Original-client test host build failed."}
$hostDll = Join-Path $repository "temp/build/PathEcho.UpgradeTests/Release/net8.0-windows/PathEcho.UpgradeTests.dll"
foreach($channel in @("Lite","Full")) {
    dotnet $hostDll $channel $currentRoot (Join-Path $previousRoot $channel) $current 2>&1 | Tee-Object -FilePath (Join-Path $evidence ("upgrade-"+$channel+".log"))
    if($LASTEXITCODE -ne 0){throw "$channel original-client full upgrade failed."}
}
Write-Output "UPGRADE_ACCEPTANCE_PASSED: $evidence"
