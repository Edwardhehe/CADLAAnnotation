$ErrorActionPreference = 'Stop'
$builds = @(
    @{ Project='src\LAAnnotation.csproj'; Output='bin\v0.3.3\ZWCAD' },
    @{ Project='src\LAAnnotation.AutoCAD.csproj'; Output='bin\v0.3.3\AutoCAD' }
)
foreach ($build in $builds) {
    $name=$build.Project
    $output=Join-Path $PSScriptRoot $build.Output
    dotnet build (Join-Path $PSScriptRoot $name) -c Release -p:OutputPath="$output\"
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $name ($LASTEXITCODE)" }
}
Write-Host "ZWCAD DLL:  $PSScriptRoot\bin\v0.3.3\ZWCAD\LAAnnotation.ZWCAD.dll"
Write-Host "AutoCAD DLL: $PSScriptRoot\bin\v0.3.3\AutoCAD\LAAnnotation.AutoCAD.dll"
