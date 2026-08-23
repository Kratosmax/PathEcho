param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$DotNetPath = "dotnet",
    [string]$IsccPath = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    [Parameter(Mandatory = $true)][string]$PrivateKeyPath,
    [string]$ExpectedTag
)

$ErrorActionPreference = "Stop"
$repository = Split-Path -Parent $PSScriptRoot
$version = ([xml](Get-Content -Raw -LiteralPath (Join-Path $repository "Directory.Build.props"))).Project.PropertyGroup.Version
if ($ExpectedTag -and $ExpectedTag -ne "v$version") { throw "Tag $ExpectedTag does not match version $version." }
if (-not (Test-Path -LiteralPath $PrivateKeyPath)) { throw "Update signing key not found." }
if (-not (Test-Path -LiteralPath $IsccPath)) { throw "Inno Setup compiler not found: $IsccPath" }

$releaseRoot = Join-Path $repository "temp\release"
$resolvedRepository = [IO.Path]::GetFullPath($repository)
$resolvedRelease = [IO.Path]::GetFullPath($releaseRoot)
if (-not $resolvedRelease.StartsWith($resolvedRepository + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean outside repository temp: $resolvedRelease"
}
if (Test-Path -LiteralPath $resolvedRelease) { Remove-Item -LiteralPath $resolvedRelease -Recurse -Force }
New-Item -ItemType Directory -Path $resolvedRelease | Out-Null

& $DotNetPath build (Join-Path $repository "PathEcho.sln") -c $Configuration --no-restore -m:1 -v:minimal
if ($LASTEXITCODE -ne 0) { throw "Build failed." }
& $DotNetPath run --project (Join-Path $repository "tests\PathEcho.SmokeTests\PathEcho.SmokeTests.csproj") -c $Configuration --no-build
if ($LASTEXITCODE -ne 0) { throw "Smoke tests failed." }

$artifacts = @()
foreach ($channel in @("Lite", "Full")) {
    $packageRoot = Join-Path $resolvedRelease "package-$channel"
    $updaterRoot = Join-Path $resolvedRelease "updater-$channel"
    $selfContained = if ($channel -eq "Full") { "true" } else { "false" }
    & $DotNetPath publish (Join-Path $repository "src\PathEcho\PathEcho.csproj") -c $Configuration -r $Runtime --self-contained $selfContained -o $packageRoot -m:1
    if ($LASTEXITCODE -ne 0) { throw "$channel app publish failed." }
    & $DotNetPath publish (Join-Path $repository "src\PathEcho.Updater\PathEcho.Updater.csproj") -c $Configuration -r $Runtime --self-contained $selfContained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $updaterRoot -m:1
    if ($LASTEXITCODE -ne 0) { throw "$channel updater publish failed." }
    Get-ChildItem -LiteralPath $updaterRoot -File | Copy-Item -Destination $packageRoot -Force

    $utf8 = [Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText((Join-Path $packageRoot "channel.txt"), "$channel`r`n", $utf8)
    [IO.File]::WriteAllText((Join-Path $packageRoot "version.txt"), "$version`r`n", $utf8)
    [IO.File]::WriteAllText((Join-Path $packageRoot ".pathecho-install-root"), "PathEcho`r`n", $utf8)

    $zip = Join-Path $resolvedRelease "PathEcho-$version-$channel.zip"
    Compress-Archive -Path (Join-Path $packageRoot "*") -DestinationPath $zip -CompressionLevel Optimal
    $packageHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
    $packagedUpdater = Join-Path $packageRoot "PathEcho.Updater.exe"
    & $packagedUpdater --package $zip --sha256 $packageHash --channel $channel --version $version --verify-only true
    if ($LASTEXITCODE -ne 0) { throw "$channel package updater verification failed." }
    $setupBase = "PathEcho-$version-$channel-Setup"
    & $IsccPath "/DAppVersion=$version" "/DChannel=$channel" "/DSourceDir=$packageRoot" "/DOutputDir=$resolvedRelease" "/DOutputBaseFilename=$setupBase" (Join-Path $repository "build\PathEcho.iss")
    if ($LASTEXITCODE -ne 0) { throw "$channel setup build failed." }
    $artifacts += $zip
    $artifacts += Join-Path $resolvedRelease "$setupBase.exe"
}

$notes = Join-Path $repository "RELEASE_NOTES_CURRENT.md"
$releaseTool = Join-Path $repository "build\PathEcho.ReleaseTool\PathEcho.ReleaseTool.csproj"
foreach ($channel in @("Lite", "Full")) {
    $package = Join-Path $resolvedRelease "PathEcho-$version-$channel.zip"
    $url = "https://github.com/Kratosmax/PathEcho/releases/download/v$version/PathEcho-$version-$channel.zip"
    $manifest = Join-Path $resolvedRelease "update-$($channel.ToLowerInvariant()).json"
    & $DotNetPath run --project $releaseTool -c $Configuration -- $PrivateKeyPath $version $channel $package $url $notes $manifest PathEcho
    if ($LASTEXITCODE -ne 0) { throw "$channel manifest signing failed." }
}
Copy-Item (Join-Path $resolvedRelease "update-lite.json") (Join-Path $resolvedRelease "update.json")

$checksumFiles = @($artifacts) + @(
    (Join-Path $resolvedRelease "update.json"),
    (Join-Path $resolvedRelease "update-lite.json"),
    (Join-Path $resolvedRelease "update-full.json"))
$checksumLines = foreach ($file in $checksumFiles) {
    "{0}  {1}" -f (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash, [IO.Path]::GetFileName($file)
}
[IO.File]::WriteAllLines((Join-Path $resolvedRelease "SHA256SUMS.txt"), $checksumLines, [Text.UTF8Encoding]::new($false))

Get-ChildItem -LiteralPath $resolvedRelease -File | Sort-Object Name | Select-Object Name,Length,@{Name="Sha256";Expression={(Get-FileHash $_.FullName -Algorithm SHA256).Hash}} | ConvertTo-Json
