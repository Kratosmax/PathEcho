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
foreach($spec in @(@("v$current",$currentRoot),@("v$previous",$previousRoot))) {
    gh release download $spec[0] --repo Kratosmax/PathEcho --pattern "*.zip" --pattern "update*.json" --pattern "SHA256SUMS.txt" --dir $spec[1]
    if($LASTEXITCODE -ne 0){throw "Unable to download original release assets."}
}
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
