[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$publishScript = Join-Path $PSScriptRoot 'Publish.ps1'
$shell = (Get-Process -Id $PID).Path
$utf8 = [Text.UTF8Encoding]::new($false)
$fixtures = [Collections.Generic.List[string]]::new()

function Write-TestFile {
    param(
        [string]$Path,
        [string]$Content
    )

    $parent = Split-Path -Parent $Path
    if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    [IO.File]::WriteAllText($Path, $Content, $utf8)
}

function Assert-Test {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) { throw $Message }
}

function Invoke-Git {
    param(
        [string]$Root,
        [Parameter(ValueFromRemainingArguments = $true)]
        [string[]]$Arguments
    )

    & git -C $Root @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Git failed in publish regression fixture: git $($Arguments -join ' ')" }
}

function New-PublishFixture {
    $root = Join-Path ([IO.Path]::GetTempPath()) ('openblade-publish-' + [guid]::NewGuid().ToString('N'))
    $fixtures.Add($root)
    New-Item -ItemType Directory -Path $root | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $root 'build') | Out-Null
    Copy-Item -LiteralPath $publishScript -Destination (Join-Path $root 'build\Publish.ps1')

    Write-TestFile (Join-Path $root '.gitignore') "**/bin/`r`n**/obj/`r`nartifacts/`r`n"
    Write-TestFile (Join-Path $root 'Directory.Build.props') "<Project><PropertyGroup><Version>1.2.3</Version></PropertyGroup></Project>`r`n"
    Write-TestFile (Join-Path $root 'README.md') "Fixture readme.`r`n"
    Write-TestFile (Join-Path $root 'LICENSE') "Fixture license.`r`n"
    Write-TestFile (Join-Path $root 'VALIDATION.md') "Fixture validation.`r`n"
    Write-TestFile (Join-Path $root 'docs\building.md') "Fixture building guide.`r`n"
    Write-TestFile (Join-Path $root 'src\OpenBlade.HdmiFix.Desktop\OpenBlade.HdmiFix.Desktop.csproj') "<Project />`r`n"

    $packages = Join-Path $root 'packages'
    $runtimeNames = @('Microsoft.NETCore.App.Runtime.win-x64', 'Microsoft.WindowsDesktop.App.Runtime.win-x64')
    foreach ($runtimeName in $runtimeNames) {
        Write-TestFile (Join-Path $packages ($runtimeName.ToLowerInvariant() + '\1.0.0\LICENSE.txt')) "Runtime license.`r`n"
    }
    $assets = @{
        project = @{
            restore = @{ packagesPath = $packages }
            frameworks = @{
                'net10.0-windows' = @{
                    downloadDependencies = @(
                        @{ name = $runtimeNames[0]; version = '[1.0.0, 1.0.0]' },
                        @{ name = $runtimeNames[1]; version = '[1.0.0, 1.0.0]' }
                    )
                }
            }
        }
    } | ConvertTo-Json -Depth 8
    Write-TestFile (Join-Path $root 'src\OpenBlade.HdmiFix.Desktop\obj\project.assets.json') ($assets + "`r`n")

    $stub = @'
$output = Join-Path $env:PUBLISH_TEST_ROOT 'artifacts\publish'
New-Item -ItemType Directory -Path $output -Force | Out-Null
foreach ($name in @('OpenBlade.HdmiFix.exe', 'OpenBlade.HdmiFix.dll', 'OpenBlade.HdmiFix.Core.dll', 'OpenBlade.HdmiFix.runtimeconfig.json', 'hostfxr.dll', 'wpfgfx_cor3.dll')) {
    [IO.File]::WriteAllText((Join-Path $output $name), 'payload')
}
[IO.File]::WriteAllText($env:PUBLISH_TEST_MARKER, 'called')
if ($env:PUBLISH_TEST_AFTER_DOTNET -eq 'dirty') {
    [IO.File]::AppendAllText((Join-Path $env:PUBLISH_TEST_ROOT 'README.md'), 'changed')
}
if ($env:PUBLISH_TEST_AFTER_DOTNET -eq 'head') {
    & git -C $env:PUBLISH_TEST_ROOT commit --allow-empty -m 'move fixture head' | Out-Null
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
'@
    Write-TestFile (Join-Path $root 'test-tools\dotnet-stub.ps1') ($stub + "`r`n")
    Write-TestFile (Join-Path $root 'test-tools\dotnet.cmd') "@echo off`r`npowershell.exe -NoProfile -ExecutionPolicy Bypass -File `"%~dp0dotnet-stub.ps1`"`r`nexit /b %ERRORLEVEL%`r`n"

    & git init -q $root
    if ($LASTEXITCODE -ne 0) { throw 'Unable to initialize the publish regression fixture.' }
    Invoke-Git $root config user.name 'OpenBlade Tests'
    Invoke-Git $root config user.email 'tests@openblade.invalid'
    Invoke-Git $root add .
    Invoke-Git $root commit -q -m 'fixture'
    return $root
}

function Invoke-FixturePublish {
    param(
        [string]$Root,
        [string]$AfterDotnet = ''
    )

    $marker = Join-Path $Root 'artifacts\dotnet-called.txt'
    $oldPath = $env:PATH
    $oldMarker = $env:PUBLISH_TEST_MARKER
    $oldRoot = $env:PUBLISH_TEST_ROOT
    $oldAfterDotnet = $env:PUBLISH_TEST_AFTER_DOTNET
    $oldErrorActionPreference = $ErrorActionPreference
    try {
        $env:PATH = (Join-Path $Root 'test-tools') + [IO.Path]::PathSeparator + $oldPath
        $env:PUBLISH_TEST_MARKER = $marker
        $env:PUBLISH_TEST_ROOT = $Root
        $env:PUBLISH_TEST_AFTER_DOTNET = $AfterDotnet
        $ErrorActionPreference = 'Continue'
        $output = & $shell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Root 'build\Publish.ps1') 2>&1 | Out-String
        return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output; Marker = $marker }
    }
    finally {
        $env:PATH = $oldPath
        $env:PUBLISH_TEST_MARKER = $oldMarker
        $env:PUBLISH_TEST_ROOT = $oldRoot
        $env:PUBLISH_TEST_AFTER_DOTNET = $oldAfterDotnet
        $ErrorActionPreference = $oldErrorActionPreference
    }
}

try {
    $dirtyRoot = New-PublishFixture
    [IO.File]::AppendAllText((Join-Path $dirtyRoot 'README.md'), 'dirty')
    Write-TestFile (Join-Path $dirtyRoot 'untracked.txt') "untracked`r`n"
    $dirtyResult = Invoke-FixturePublish $dirtyRoot
    Assert-Test ($dirtyResult.ExitCode -ne 0) 'Dirty source unexpectedly published.'
    Assert-Test ($dirtyResult.Output -match 'clean source worktree') 'Dirty source did not report the clean-worktree requirement.'
    Assert-Test (-not (Test-Path -LiteralPath $dirtyResult.Marker)) 'Dirty source invoked dotnet before failing.'
    Assert-Test (-not (Test-Path -LiteralPath (Join-Path $dirtyRoot 'artifacts\publish'))) 'Dirty source created publish output before failing.'

    $changedRoot = New-PublishFixture
    $changedResult = Invoke-FixturePublish $changedRoot 'dirty'
    Assert-Test ($changedResult.ExitCode -ne 0) 'Source changed during the build unexpectedly published.'
    Assert-Test ($changedResult.Output -match 'clean source worktree') "A post-build source change did not report the clean-worktree requirement: $($changedResult.Output)"
    Assert-Test (Test-Path -LiteralPath $changedResult.Marker) 'The post-build source-change check did not run after dotnet.'
    Assert-Test (-not (Test-Path -LiteralPath (Join-Path $changedRoot 'artifacts\publish\build.json'))) 'A changed source state received provenance.'
    Assert-Test (-not (Test-Path -LiteralPath (Join-Path $changedRoot 'artifacts\OpenBlade.HdmiFix-1.2.3-win-x64.zip'))) 'A changed source state produced an archive.'

    $headRoot = New-PublishFixture
    $originalHead = (& git -C $headRoot rev-parse HEAD).Trim()
    $headResult = Invoke-FixturePublish $headRoot 'head'
    Assert-Test ($headResult.ExitCode -ne 0) 'A changed source commit unexpectedly published.'
    Assert-Test ($headResult.Output -match 'source commit changed') 'A changed source commit did not report the commit-change requirement.'
    Assert-Test ((& git -C $headRoot rev-parse HEAD).Trim() -ne $originalHead) 'The fixture did not change HEAD after dotnet.'
    Assert-Test (-not (Test-Path -LiteralPath (Join-Path $headRoot 'artifacts\publish\build.json'))) 'A changed source commit received provenance.'

    $ignoredRoot = New-PublishFixture
    Write-TestFile (Join-Path $ignoredRoot 'artifacts\ignored-before-publish.txt') "ignored`r`n"
    $ignoredResult = Invoke-FixturePublish $ignoredRoot
    Assert-Test ($ignoredResult.ExitCode -eq 0) "Ignored build output blocked publish: $($ignoredResult.Output)"
    $build = Get-Content -LiteralPath (Join-Path $ignoredRoot 'artifacts\publish\build.json') -Raw | ConvertFrom-Json
    Assert-Test ($build.sourceCommit -eq (& git -C $ignoredRoot rev-parse HEAD).Trim()) 'Successful publish recorded the wrong source commit.'
    Assert-Test (Test-Path -LiteralPath (Join-Path $ignoredRoot 'artifacts\OpenBlade.HdmiFix-1.2.3-win-x64.zip')) 'Successful publish did not create an archive.'

    Write-Output 'Publish provenance regression checks passed.'
}
finally {
    foreach ($fixture in $fixtures) {
        $resolvedFixture = [IO.Path]::GetFullPath($fixture)
        $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        if (-not $resolvedFixture.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
            -not [IO.Path]::GetFileName($resolvedFixture).StartsWith('openblade-publish-', [StringComparison]::Ordinal)) {
            throw 'Refusing to remove a publish fixture outside the intended temporary directory.'
        }
        if (Test-Path -LiteralPath $resolvedFixture) { Remove-Item -LiteralPath $resolvedFixture -Recurse -Force }
    }
}
