$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$compose = Join-Path $root 'compose.yaml'
$db = 'manifiestos_test_' + [Guid]::NewGuid().ToString('N')
$created = $false
try {
    docker compose -f $compose exec -T postgres createdb -U manifiestos_owner $db
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo crear la base aislada de pruebas.' }
    $created = $true
    & (Join-Path $PSScriptRoot 'Migrate.ps1') -Database $db
    & (Join-Path $PSScriptRoot 'Migrate.ps1') -Database $db
    docker compose -f $compose exec -T postgres psql -X -v ON_ERROR_STOP=1 -U manifiestos_owner -d $db -f /workspace/database/tests/integridad.sql
    if ($LASTEXITCODE -ne 0) { throw 'Falló la validación SQL.' }
    docker compose -f $compose exec -T postgres psql -X -v ON_ERROR_STOP=1 -U manifiestos_owner -d $db -f /workspace/database/tests/acceso_web.sql
    if ($LASTEXITCODE -ne 0) { throw 'Falló la validación de acceso web.' }
    docker compose -f $compose exec -T postgres psql -X -v ON_ERROR_STOP=1 -U manifiestos_owner -d $db -f /workspace/database/tests/ruta.sql
    if ($LASTEXITCODE -ne 0) { throw 'Falló la validación de rutas.' }
    docker compose -f $compose exec -T postgres psql -X -v ON_ERROR_STOP=1 -U manifiestos_owner -d $db -f /workspace/database/tests/clientes.sql
    if ($LASTEXITCODE -ne 0) { throw 'Falló la validación de clientes.' }
    $worker = {
        param($composePath, $databaseName, $file, [string[]]$vars)
        $extra = @(); foreach ($v in $vars) { $extra += @('-v', $v) }
        $result = docker compose -f $composePath exec -T postgres psql -X -v ON_ERROR_STOP=1 @extra -U manifiestos_owner -d $databaseName -f "/workspace/database/tests/$file"
        if ($LASTEXITCODE -ne 0) { throw ($result -join "`n") }
    }
    $casos = @(
        @('concurrencia.sql', @()), @('concurrencia.sql', @()),
        @('acceso_web_concurrencia.sql', @('sujeto=s-conc', 'correo=concurrente@example.invalid')),
        @('acceso_web_concurrencia.sql', @('sujeto=s-conc', 'correo=concurrente@example.invalid')),
        @('acceso_web_concurrencia.sql', @('sujeto=s-x1', 'correo=carrera@example.invalid')),
        @('acceso_web_concurrencia.sql', @('sujeto=s-x2', 'correo=carrera@example.invalid'))
    )
    $jobs = @(foreach ($c in $casos) { Start-Job -ScriptBlock $worker -ArgumentList $compose,$db,$c[0],$c[1] })
    try {
        $jobs | Wait-Job -Timeout 60 | Out-Null
        if (@($jobs | Where-Object State -ne 'Completed').Count) { throw 'Falló o excedió tiempo la prueba concurrente.' }
        $jobs | Receive-Job -ErrorAction Stop | Out-Null
    } finally { $jobs | Stop-Job; $jobs | Remove-Job }
    foreach ($check in 'resultado_concurrencia.sql', 'resultado_acceso_web_concurrencia.sql') {
        docker compose -f $compose exec -T postgres psql -X -v ON_ERROR_STOP=1 -U manifiestos_owner -d $db -f "/workspace/database/tests/$check"
        if ($LASTEXITCODE -ne 0) { throw "Falló la comprobación de concurrencia: $check" }
    }
    Write-Host 'Validación SQL correcta; migraciones aplicadas dos veces sin duplicación.'
} finally {
    if ($created) {
        docker compose -f $compose exec -T postgres dropdb --force -U manifiestos_owner $db
        if ($LASTEXITCODE -ne 0) { Write-Warning "No se pudo retirar la base de prueba $db" }
    }
}

