[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
[xml]$properties = Get-Content -LiteralPath (Join-Path $taskRoot 'Directory.Build.props')
$version = [string]$properties.Project.PropertyGroup.Version
$output = Join-Path $taskRoot 'artifacts\publish'
$archive = Join-Path $taskRoot "artifacts\OpenBlade.HdmiFix-$version-win-x64.zip"
if (Test-Path -LiteralPath $output) { throw 'The publish folder already exists. Preserve or move it before preparing a new payload.' }
& dotnet publish (Join-Path $taskRoot 'src\OpenBlade.HdmiFix.Desktop\OpenBlade.HdmiFix.Desktop.csproj') -c Release -r win-x64 --self-contained true -p:NuGetAudit=false -o $output
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
foreach ($name in @('OpenBlade.HdmiFix.exe', 'OpenBlade.HdmiFix.dll', 'OpenBlade.HdmiFix.Core.dll', 'OpenBlade.HdmiFix.runtimeconfig.json', 'hostfxr.dll', 'wpfgfx_cor3.dll')) {
    $file = Get-Item -LiteralPath (Join-Path $output $name)
    if ($file.Length -eq 0) { throw "Empty payload file: $name" }
}
Copy-Item -LiteralPath (Join-Path $taskRoot 'README.md'), (Join-Path $taskRoot 'LICENSE'), (Join-Path $taskRoot 'VALIDATION.md') -Destination $output
Copy-Item -LiteralPath (Join-Path $taskRoot 'docs') -Destination $output -Recurse
Compress-Archive -LiteralPath $output -DestinationPath $archive
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$archive.sha256", "$hash  $([IO.Path]::GetFileName($archive))`r`n", [Text.UTF8Encoding]::new($false))
Write-Output "Prepared $archive"
