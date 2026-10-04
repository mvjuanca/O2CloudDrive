param([string]$Dotnet = (Join-Path $PSScriptRoot '..\.dotnet\sdk\dotnet.exe'))
$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $projectRoot
try {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    New-Item -ItemType Directory -Path artifacts -Force | Out-Null
    function Run-Dotnet([string]$Log, [string[]]$Arguments) {
        & $Dotnet @Arguments *> (Join-Path 'artifacts' $Log)
        if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $Log (exit $LASTEXITCODE)" }
        Get-Content (Join-Path 'artifacts' $Log) -Tail 2
    }
    Run-Dotnet 'fix-tests.log' @('run', '--project', 'tests/OverwriteTests', '-c', 'Release')
    Run-Dotnet 'core-net8-tests.log' @('run', '--project', 'tests/CoreTests', '-c', 'Release')
    Run-Dotnet 'mounted-tests.log' @('run', '--project', 'tests/OverwriteTests', '-c', 'Release', '--', '--mounted')
    Run-Dotnet 'release-publish.log' @('publish', 'src/O2CloudDrive/O2CloudDrive.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', 'artifacts/0.8.4-juanca-test1')
    Run-Dotnet 'real-runner-publish.log' @('publish', 'tests/RealValidation', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', 'artifacts/real-validation-runner')
    & artifacts/0.8.4-juanca-test1/O2CloudDrive.exe --self-test --cache artifacts/self-test-cache *> artifacts/fixed-self-test.log
    if ($LASTEXITCODE -ne 0) { throw 'self-test failed' }
    Copy-Item -LiteralPath LICENSE -Destination artifacts/0.8.4-juanca-test1/LICENSE
    Copy-Item -LiteralPath LICENSE -Destination artifacts/real-validation-runner/LICENSE
    $manifest = foreach ($directory in @('0.8.4-juanca-test1', 'real-validation-runner')) {
        $basePath = Join-Path $projectRoot "artifacts/$directory"
        foreach ($file in Get-ChildItem -LiteralPath $basePath -Recurse -File) {
            [pscustomobject]@{ Artifact=$directory; Path=$file.FullName.Substring($basePath.Length+1); Bytes=$file.Length; SHA256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
        }
    }
    $manifest | Export-Csv artifacts/build-sha256.csv -NoTypeInformation -Encoding UTF8
    $baseCommit = '5369cd42d9e8e43871a41297262e7b998db4b1e0'
    $patch = & git diff --binary $baseCommit HEAD
    [System.IO.File]::WriteAllText((Join-Path $projectRoot 'artifacts/o2clouddrive-overwrite-fix.patch'), (($patch -join "`n") + "`n"), [System.Text.UTF8Encoding]::new($false))
    & git log --reverse --format='%H %s' "$baseCommit..HEAD" | Out-File artifacts/commits.txt -Encoding utf8
    Get-FileHash artifacts/0.8.4-juanca-test1/O2CloudDrive.dll -Algorithm SHA256
} finally { Pop-Location }
