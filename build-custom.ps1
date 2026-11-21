param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $revision = & git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) {
        throw 'Cannot read the Git revision.'
    }

    foreach ($project in @('ui', 'libse')) {
        $template = Join-Path $PSScriptRoot "src\$project\Properties\AssemblyInfo.cs.template"
        $output = Join-Path $PSScriptRoot "src\$project\Properties\AssemblyInfo.cs"
        $assemblyInfo = [IO.File]::ReadAllText($template).Replace('[REVNO]', '0').Replace('[GITHASH]', $revision.Trim())
        [IO.File]::WriteAllText($output, $assemblyInfo, (New-Object Text.UTF8Encoding($false)))
    }

    & dotnet restore src\ui\SubtitleEdit.csproj --disable-parallel
    if ($LASTEXITCODE -ne 0) {
        throw 'Package restore failed.'
    }

    & dotnet build src\ui\SubtitleEdit.csproj --no-restore -c $Configuration -m:1 -nodeReuse:false -p:UseSharedCompilation=false "-p:SolutionDir=$PSScriptRoot\"
    if ($LASTEXITCODE -ne 0) {
        throw 'Build failed.'
    }
}
finally {
    Pop-Location
}
