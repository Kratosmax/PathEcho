param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$DotNetPath = "dotnet",
    [string]$OutputDirectory = "temp\preview"
)

$ErrorActionPreference = "Stop"
$repository = Split-Path -Parent $PSScriptRoot
$version = ([xml](Get-Content -Raw -LiteralPath (Join-Path $repository "Directory.Build.props"))).Project.PropertyGroup.Version
$tempRoot = [IO.Path]::GetFullPath((Join-Path $repository "temp"))
$previewRoot = [IO.Path]::GetFullPath((Join-Path $repository $OutputDirectory))
$tempPrefix = $tempRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $previewRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Preview output must remain inside the repository temp directory: $previewRoot"
}

$packageRoot = Join-Path $previewRoot "PathEcho-$version-Lite"
$updaterRoot = Join-Path $previewRoot "updater"
$archive = Join-Path $previewRoot "PathEcho-$version-Lite.zip"

foreach ($path in @($packageRoot, $updaterRoot)) {
    $resolvedPreview = [IO.Path]::GetFullPath($previewRoot)
    $resolvedPath = [IO.Path]::GetFullPath($path)
    $previewPrefix = $resolvedPreview.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedPath.StartsWith($previewPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a path outside the preview output directory: $resolvedPath"
    }

    if (Test-Path -LiteralPath $resolvedPath) {
        Remove-Item -LiteralPath $resolvedPath -Recurse -Force
    }
}

if (Test-Path -LiteralPath $archive) {
    Remove-Item -LiteralPath $archive -Force
}

$appArguments = @("publish", (Join-Path $repository "src\PathEcho\PathEcho.csproj"), "-c", $Configuration, "-r", $Runtime, "--self-contained", "false", "-o", $packageRoot, "-m:1")
& $DotNetPath @appArguments
if ($LASTEXITCODE -ne 0) { throw "PathEcho publish failed." }

$updaterArguments = @("publish", (Join-Path $repository "src\PathEcho.Updater\PathEcho.Updater.csproj"), "-c", $Configuration, "-r", $Runtime, "--self-contained", "false", "-p:PublishSingleFile=true", "-o", $updaterRoot, "-m:1")
& $DotNetPath @updaterArguments
if ($LASTEXITCODE -ne 0) { throw "PathEcho.Updater publish failed." }

Get-ChildItem -LiteralPath $updaterRoot -File | Copy-Item -Destination $packageRoot -Force
$newLine = [Environment]::NewLine
[IO.File]::WriteAllText((Join-Path $packageRoot "channel.txt"), "Lite$newLine", [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $packageRoot "version.txt"), "$version$newLine", [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $packageRoot ".pathecho-install-root"), "PathEcho$newLine", [Text.UTF8Encoding]::new($false))

Compress-Archive -Path (Join-Path $packageRoot "*") -DestinationPath $archive -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
[IO.File]::WriteAllText("$archive.sha256", "$hash  $([IO.Path]::GetFileName($archive))$newLine", [Text.UTF8Encoding]::new($false))

[PSCustomObject]@{
    Version = $version
    Channel = "Lite"
    PackageDirectory = $packageRoot
    Archive = $archive
    Size = (Get-Item -LiteralPath $archive).Length
    Sha256 = $hash
} | ConvertTo-Json
