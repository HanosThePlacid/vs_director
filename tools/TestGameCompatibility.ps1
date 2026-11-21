<#
.SYNOPSIS
Build once with 1.22.3, then test that exact DLL with both game installations.
.DESCRIPTION
Requires .NET 10, extracted VSImGui 1.2.7, and desktop OpenGL/OpenAL support.
Each game gets an isolated test tree under .work; logs are kept in artifacts.
Does not install the mod, change its version, or start the game.
#>
param(
    [Parameter(Mandatory)][string]$BaselineGamePath,
    [Parameter(Mandatory)][string]$NewGamePath,
    [Parameter(Mandatory)][string]$VSImGuiPath
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$BaselineGamePath = (Resolve-Path -LiteralPath $BaselineGamePath).Path
$NewGamePath = (Resolve-Path -LiteralPath $NewGamePath).Path
$VSImGuiPath = (Resolve-Path -LiteralPath $VSImGuiPath).Path
$run = Join-Path $repo ('.work/compatibility/' + [Guid]::NewGuid().ToString('N'))
$logs = Join-Path $repo 'artifacts'
$null = New-Item -ItemType Directory -Path $run,$logs -Force
$savedGame = $env:VINTAGE_STORY
function Invoke-LoggedDotnet([string]$LogName, [string[]]$Arguments) {
    $log = Join-Path $logs $LogName
    & dotnet @Arguments > $log 2>&1
    if ($LASTEXITCODE -ne 0) {
        Get-Content -LiteralPath $log -Tail 35
        throw "Compatibility check failed. See $log"
    }
    Get-Content -LiteralPath $log -Tail 5
}
try {
    Invoke-LoggedDotnet 'compat122-build.log' @('build', (Join-Path $repo 'VSDirector.csproj'), '-c', 'Release',
        '-p:PackMod=false', "-p:VintageStoryPath=$BaselineGamePath", "-p:VSImGuiPath=$VSImGuiPath")
    $assembly = Join-Path $repo 'bin/Release/Mods/vsdirectorfork/VSDirectorFork.dll'
    $hash = (Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash
    Invoke-LoggedDotnet 'compat-core.log' @('run', '--project', (Join-Path $repo 'tests/VSDirector.CoreTests'), '-c', 'Release')
    foreach ($game in @(@{Name='122'; Path=$BaselineGamePath}, @{Name='123'; Path=$NewGamePath})) {
        $stage = Join-Path $run $game.Name
        $probe = Join-Path $stage 'tests/VSDirector.PatchProbe'
        $null = New-Item -ItemType Directory -Path $probe -Force
        # Tests load source, translations, fonts and shaders relative to the
        # repository root. Preserve that layout without copying bin/obj trees.
        Copy-Item -LiteralPath (Join-Path $repo 'src'),(Join-Path $repo 'assets') -Destination $stage -Recurse
        Get-ChildItem -LiteralPath (Join-Path $repo 'tests/VSDirector.PatchProbe') -File |
            Where-Object { $_.Extension -in '.cs','.csproj' } | Copy-Item -Destination $probe
        $env:VINTAGE_STORY = $game.Path
        $arguments = @('run', '--project', $probe, '-c', 'Release', "-p:DirectorAssembly=$assembly",
            "-p:VintageStoryPath=$($game.Path)", "-p:VSImGuiPath=$VSImGuiPath")
        # 1.23 retains deprecated mouse/forms APIs so the same binary can run
        # on 1.22. Keep warnings visible in the log; errors still fail the run.
        if ($game.Name -eq '123') { $arguments += '-p:TreatWarningsAsErrors=false' }
        Invoke-LoggedDotnet "compat$($game.Name)-probe.log" $arguments
        if ((Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash -ne $hash) {
            throw 'The baseline DLL changed during compatibility checks.'
        }
    }
    Write-Host "Both game versions passed with SHA256 $hash"
    Write-Host "Isolated test trees: $run"
}
finally { $env:VINTAGE_STORY = $savedGame }
