[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot

function Get-SourceCommit {
    $commit = & git -C $taskRoot rev-parse --verify HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Unable to read the source commit. Run publish from a Git worktree.' }
    return ([string]$commit).Trim()
}

function Assert-CleanSourceState {
    param([string]$ExpectedCommit)

    $currentCommit = Get-SourceCommit
    if ($ExpectedCommit -and $currentCommit -ne $ExpectedCommit) {
        throw 'The source commit changed during publishing. Discard this payload and publish again from one unchanged commit.'
    }

    $changes = @(& git -C $taskRoot status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to check the source worktree. Run publish from a Git worktree.' }
    if ($changes.Count -ne 0) {
        throw 'Publishing requires a clean source worktree. Commit or remove all tracked and untracked source changes, then try again.'
    }

    return $currentCommit
}

$sourceCommit = Assert-CleanSourceState
[xml]$properties = Get-Content -LiteralPath (Join-Path $taskRoot 'Directory.Build.props')
$version = [string]$properties.Project.PropertyGroup.Version
$output = Join-Path $taskRoot 'artifacts\publish'
$archive = Join-Path $taskRoot "artifacts\OpenBlade.HdmiFix-$version-win-x64.zip"
if (Test-Path -LiteralPath $output) { throw 'The publish folder already exists. Preserve or move it before preparing a new payload.' }
foreach ($existingFile in @($archive, "$archive.sha256")) {
    if (Test-Path -LiteralPath $existingFile) {
        throw 'The release archive or checksum already exists. Preserve or move it before preparing a new payload.'
    }
}
& dotnet publish (Join-Path $taskRoot 'src\OpenBlade.HdmiFix.Desktop\OpenBlade.HdmiFix.Desktop.csproj') -c Release -r win-x64 --self-contained true -p:NuGetAudit=false -o $output
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
foreach ($name in @('OpenBlade.HdmiFix.exe', 'OpenBlade.HdmiFix.dll', 'OpenBlade.HdmiFix.Core.dll', 'OpenBlade.HdmiFix.runtimeconfig.json', 'hostfxr.dll', 'wpfgfx_cor3.dll')) {
    $file = Get-Item -LiteralPath (Join-Path $output $name)
    if ($file.Length -eq 0) { throw "Empty payload file: $name" }
}
Copy-Item -LiteralPath (Join-Path $taskRoot 'README.md'), (Join-Path $taskRoot 'LICENSE'), (Join-Path $taskRoot 'VALIDATION.md') -Destination $output
Copy-Item -LiteralPath (Join-Path $taskRoot 'docs') -Destination $output -Recurse
$assets = Get-Content -LiteralPath (Join-Path $taskRoot 'src\OpenBlade.HdmiFix.Desktop\obj\project.assets.json') -Raw | ConvertFrom-Json
$runtimePackages = @($assets.project.frameworks.PSObject.Properties.Value.downloadDependencies | Where-Object {
    $_.name -in @('Microsoft.NETCore.App.Runtime.win-x64', 'Microsoft.WindowsDesktop.App.Runtime.win-x64')
})
if ($runtimePackages.Count -ne 2) { throw 'The runtime package provenance is incomplete.' }
foreach ($runtimePackage in $runtimePackages) {
    $runtimeVersion = $runtimePackage.version.Trim('[', ']').Split(',')[0].Trim()
    $runtimePath = Join-Path $assets.project.restore.packagesPath ($runtimePackage.name.ToLowerInvariant() + '\' + $runtimeVersion)
    $noticePath = Join-Path $output ('notices\' + $runtimePackage.name)
    New-Item -ItemType Directory -Path $noticePath | Out-Null
    $runtimeNotices = @(Get-ChildItem -LiteralPath $runtimePath -File | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)' })
    if ($runtimeNotices.Count -eq 0) { throw "Missing runtime license: $($runtimePackage.name)" }
    foreach ($runtimeNotice in $runtimeNotices) { Copy-Item -LiteralPath $runtimeNotice.FullName -Destination $noticePath }
}
Assert-CleanSourceState -ExpectedCommit $sourceCommit | Out-Null
$provenance = @{ version = $version; sourceCommit = [string]$sourceCommit; runtimePackages = $runtimePackages } | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText((Join-Path $output 'build.json'), $provenance + "`r`n", [Text.UTF8Encoding]::new($false))
Compress-Archive -LiteralPath $output -DestinationPath $archive
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$archive.sha256", "$hash  $([IO.Path]::GetFileName($archive))`r`n", [Text.UTF8Encoding]::new($false))
Write-Output "Prepared $archive"
