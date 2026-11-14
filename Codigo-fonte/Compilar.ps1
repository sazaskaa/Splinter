$ErrorActionPreference = 'Stop'

$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw 'O compilador do .NET Framework de 64 bits não foi encontrado.'
}

$buildFolder = Join-Path $PSScriptRoot 'build'
$executable = Join-Path $buildFolder 'Splinter.exe'
[void][IO.Directory]::CreateDirectory($buildFolder)

$compilerArguments = @(
    '/nologo'
    '/optimize+'
    '/platform:x64'
    '/target:winexe'
    '/reference:System.Windows.Forms.dll'
    "/out:$executable"
    "/win32manifest:$(Join-Path $PSScriptRoot 'Splinter.manifest')"
    "/win32icon:$(Join-Path $PSScriptRoot 'Splinter.ico')"
    (Join-Path $PSScriptRoot 'Splinter.cs')
    (Join-Path $PSScriptRoot 'WindowLayout.cs')
    (Join-Path $PSScriptRoot 'MonitorInput.cs')
)

& $compiler @compilerArguments
if ($LASTEXITCODE -ne 0) {
    throw "A compilação falhou (código $LASTEXITCODE)."
}

Write-Output "Executável gerado em $executable"
