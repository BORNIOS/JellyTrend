param(
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$testProject = Join-Path $repoRoot 'Tests/Jellyfin.Plugin.JellyTrend.Tests/Jellyfin.Plugin.JellyTrend.Tests.csproj'
$resultsDir = Join-Path $repoRoot 'Tests/TestResults'
$trxPath = Join-Path $resultsDir 'verificacion.trx'
if (-not $OutputPath) { $OutputPath = Join-Path $repoRoot 'docs/VERIFICACION-RESUMEN.md' }

if (Test-Path $trxPath) { Remove-Item $trxPath -Force }

Write-Host 'Ejecutando la suite de verificación...' -ForegroundColor Cyan
& dotnet test $testProject --configuration Release --nologo `
    --logger 'trx;LogFileName=verificacion.trx' --results-directory $resultsDir
$suiteExitCode = $LASTEXITCODE

if (-not (Test-Path $trxPath)) {
    throw "La suite no generó el archivo de resultados: $trxPath"
}

[xml]$trx = Get-Content $trxPath -Raw
$ns = New-Object System.Xml.XmlNamespaceManager($trx.NameTable)
$ns.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')

$counters = $trx.SelectSingleNode('//t:ResultSummary/t:Counters', $ns)
$total = [int]$counters.total
$passed = [int]$counters.passed
$failed = [int]$counters.failed
$skipped = [int]$counters.notExecuted

$results = @($trx.SelectNodes('//t:UnitTestResult', $ns) | ForEach-Object {
        $name = [string]$_.testName
        $short = $name -replace '^Jellyfin\.Plugin\.JellyTrend\.Tests\.', ''
        $class = if ($short.Contains('.')) { $short.Substring(0, $short.LastIndexOf('.')) } else { $short }
        [pscustomobject]@{
            Name    = $short
            Class   = $class
            Outcome = [string]$_.outcome
            Message = [string]$_.Output.ErrorInfo.Message
        }
    })

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('# Resumen de verificación — JellyTrend')
$lines.Add('')
$lines.Add('> Documento generado automáticamente por `Tools/Generar-ResumenEjecutivo.ps1` a partir del')
$lines.Add('> resultado real de la suite (`Tests/TestResults/verificacion.trx`). No se edita a mano.')
$lines.Add('')
$lines.Add('## Resultado')
$lines.Add('')
$lines.Add('| Métrica | Valor |')
$lines.Add('|---|---|')
$lines.Add("| Total | $total |")
$lines.Add("| Correctas | $passed |")
$lines.Add("| Fallidas | $failed |")
$lines.Add("| Omitidas | $skipped |")
$lines.Add("| Estado | $(if ($failed -eq 0) { 'OK' } else { 'FALLO' }) |")
$lines.Add('')

if ($total -gt 0) {
    $lines.Add('## Detalle por clase')
    $lines.Add('')
    $lines.Add('| Clase | Total | Correctas | Fallidas |')
    $lines.Add('|---|---|---|---|')
    foreach ($group in ($results | Group-Object Class | Sort-Object Name)) {
        $groupFailed = @($group.Group | Where-Object { $_.Outcome -ne 'Passed' }).Count
        $groupPassed = @($group.Group | Where-Object { $_.Outcome -eq 'Passed' }).Count
        $lines.Add("| $($group.Name) | $($group.Count) | $groupPassed | $groupFailed |")
    }
    $lines.Add('')
}

$failedTests = @($results | Where-Object { $_.Outcome -ne 'Passed' })
if ($failedTests.Count -gt 0) {
    $lines.Add('## Fallos')
    $lines.Add('')
    foreach ($test in $failedTests) {
        $lines.Add("### $($test.Name)")
        $lines.Add('')
        $lines.Add('```')
        $lines.Add(($test.Message -replace "`r`n", "`n").Trim())
        $lines.Add('```')
        $lines.Add('')
    }
}

$lines.Add('## Cómo reproducirlo')
$lines.Add('')
$lines.Add('```bash')
$lines.Add('dotnet test Tests/Jellyfin.Plugin.JellyTrend.Tests')
$lines.Add('')
$lines.Add('# Suite completa + regeneración de este resumen')
$lines.Add('./Tools/Generar-ResumenEjecutivo.ps1')
$lines.Add('```')

$outputDirectory = Split-Path $OutputPath -Parent
if (-not (Test-Path $outputDirectory)) { New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null }
[IO.File]::WriteAllText($OutputPath, (($lines -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))

Write-Host "Resumen generado: $OutputPath" -ForegroundColor Green
Write-Host ("Resultado: $passed correctas, $failed con error, $skipped omitidas de $total.") -ForegroundColor $(if ($failed -eq 0) { 'Green' } else { 'Red' })

exit $suiteExitCode
