param(
    [Parameter(Mandatory=$true)][string]$ProjectDirectory,
    [string]$EngineDirectory = 'D:\Program Files (x86)\Flax\Flax_1.13',
    [switch]$Create
)
$ErrorActionPreference='Stop'
$projectPath=[IO.Path]::GetFullPath($ProjectDirectory)
if($Create) {
    if(Test-Path -LiteralPath $projectPath) { throw 'Create requires a new directory.' }
    New-Item -ItemType Directory -Path "$projectPath\Source\Game","$projectPath\Content" -Force | Out-Null
    $name=Split-Path $projectPath -Leaf
    @{Name=$name;Version='1.0';GameTarget='GameTarget';EditorTarget='GameEditorTarget';References=@(@{Name='$(EnginePath)/Flax.flaxproj'});MinEngineVersion='1.13'} | ConvertTo-Json -Depth 5 | Set-Content "$projectPath\$name.flaxproj"
    'using Flax.Build; public class Game : GameModule { public override void Init() { base.Init(); BuildNativeCode=false; } }' | Set-Content "$projectPath\Source\Game\Game.Build.cs"
    'using Flax.Build; public class GameTarget : GameProjectTarget { public override void Init() { base.Init(); Modules.Add("Game"); } }' | Set-Content "$projectPath\Source\GameTarget.Build.cs"
    'using Flax.Build; public class GameEditorTarget : GameProjectEditorTarget { public override void Init() { base.Init(); Modules.Add("Game"); } }' | Set-Content "$projectPath\Source\GameEditorTarget.Build.cs"
}
$projectFiles=@(Get-ChildItem -LiteralPath $projectPath -Filter '*.flaxproj')
if($projectFiles.Count -ne 1) { throw 'Expected exactly one .flaxproj in project directory.' }
$info=Get-Content $projectFiles[0].FullName -Raw | ConvertFrom-Json
$target=Join-Path $projectPath ('Source\'+$info.EditorTarget+'.Build.cs')
if(!(Test-Path -LiteralPath $target)) { throw "Editor target not found: $target" }
$source=Get-Content -LiteralPath $target -Raw
if($source -notmatch 'Modules.Add\("FlaxMcp"\)') {
    if($source -notmatch 'base.Init\(\);') { throw 'Add Modules.Add("FlaxMcp") to the editor target Init method manually.' }
    Copy-Item -LiteralPath $target -Destination ($target+'.backup-'+(Get-Date -Format yyyyMMddHHmmss))
    $source.Replace('base.Init();','base.Init(); Modules.Add("FlaxMcp");') | Set-Content -LiteralPath $target
}
$destination=Join-Path $projectPath 'Source\FlaxMcp'
New-Item -ItemType Directory -Force -Path $destination | Out-Null
foreach($file in Get-ChildItem "$PSScriptRoot\plugin\FlaxMcp" -File) {
    $dest=Join-Path $destination $file.Name
    if(Test-Path -LiteralPath $dest) { Copy-Item -LiteralPath $dest -Destination ($dest+'.backup-'+(Get-Date -Format yyyyMMddHHmmss)) }
    Copy-Item -LiteralPath $file.FullName -Destination $dest
}
& "$EngineDirectory\Binaries\Tools\Flax.Build.exe" -build "-workspace=$projectPath" "-buildtargets=$($info.EditorTarget)" -platform=Windows -arch=x64 -configuration=Release -log
if($LASTEXITCODE -ne 0) { throw 'Flax compilation failed; see output above.' }
Write-Output "Installed. Open the project, and set FLAX_PROJECT=$projectPath for the MCP server."
