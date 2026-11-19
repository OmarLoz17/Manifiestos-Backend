#requires -Version 7.4
param([ValidateSet('manifiestos','manifiestos_demo','manifiestos_ejemplo')][string]$Database='manifiestos_ejemplo')
$ErrorActionPreference='Stop'
foreach($puerto in 5080,5081) {
    if(Get-NetTCPConnection -LocalPort $puerto -State Listen -ErrorAction SilentlyContinue) { throw "El puerto $puerto ya está en uso. Detén únicamente la API del proyecto antes de continuar." }
}
$local=Join-Path $PSScriptRoot '.local'
$pfx=Join-Path $local 'movil-local.pfx'
if(-not(Test-Path -LiteralPath $pfx)) { throw 'Ejecuta primero android/tools/Preparar-AccesoLocal.ps1.' }
$out=Join-Path $PSScriptRoot '.salida-local-https'
$entorno=@{
    ASPNETCORE_ENVIRONMENT='Development'
    ASPNETCORE_URLS='http://localhost:5080;https://localhost:5081'
    ASPNETCORE_Kestrel__Certificates__Default__Path=$pfx
    ASPNETCORE_Kestrel__Certificates__Default__Password=[IO.File]::ReadAllText((Join-Path $local 'movil-local.password'))
    Manifiestos__BaseDatos=$Database
}
if($Database -eq 'manifiestos_ejemplo') { $entorno.Acceso__OrganizacionId='e1000000-0000-4000-8000-000000000001' }
if($Database -eq 'manifiestos_demo') { $entorno.Acceso__OrganizacionId='de000000-0000-4000-8000-000000000001' }
# Avisos push (Firebase): la clave de servicio vive solo en .local (ignorada por Git); sin ella los avisos quedan desactivados.
$fcm=Join-Path $local 'fcm-servicio.json'
if(Test-Path -LiteralPath $fcm) { $entorno.Push__CredencialesRuta=$fcm }
# Drive en segundo plano: cuenta de servicio y ID de la unidad compartida en .local (ignorada por Git). La carpeta raíz es «manifiestos-local» fuera de producción;
# para otro nombre, definir Drive__CarpetaRaiz. Sin estos archivos, los archivos se quedan en el VPS.
$drive=Join-Path $local 'drive-servicio.json'; $unidad=Join-Path $local 'drive-unidad.txt'
if((Test-Path -LiteralPath $drive) -and (Test-Path -LiteralPath $unidad)) { $entorno.Drive__CredencialesRuta=$drive; $entorno.Drive__UnidadId=([IO.File]::ReadAllText($unidad)).Trim() }
$proyecto=Join-Path $PSScriptRoot 'src/Manifiestos.Api'
$proceso=Start-Process dotnet -ArgumentList @('run','--project',('"{0}"' -f $proyecto),'--no-launch-profile','--artifacts-path',('"{0}"' -f $out)) -WorkingDirectory $PSScriptRoot -Environment $entorno -WindowStyle Hidden -RedirectStandardOutput (Join-Path $local 'api-https.stdout.log') -RedirectStandardError (Join-Path $local 'api-https.stderr.log') -PassThru
Write-Host "API local iniciada (PID $($proceso.Id)); base $Database. Registros en .local; no se abrió un puerto a la red."
