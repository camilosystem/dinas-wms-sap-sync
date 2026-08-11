# Republica el binario y reinicia el servicio.
#
# Es el ciclo normal despues de un cambio de codigo: el servicio tiene el .exe
# bloqueado mientras corre, asi que hay que detenerlo, publicar y volver a
# arrancar. La base sap-sync.db NO se toca — configuracion e historial
# sobreviven.
#
# CORRER COMO ADMINISTRADOR.

$ErrorActionPreference = 'Stop'

$servicio   = 'DinasWmsSapSync'
$directorio = 'C:\DinasWmsSapSync'
$repo       = 'C:\Users\Financial Advisor\Pictures\dinas-wms-sap-sync'

$principal = New-Object Security.Principal.WindowsPrincipal(
    [Security.Principal.WindowsIdentity]::GetCurrent())

if (-not $principal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
    Write-Host "Esta consola NO es de administrador. Detener un servicio requiere elevacion." -ForegroundColor Red
    exit 1
}

Write-Host "Deteniendo $servicio..."
& sc.exe stop $servicio | Out-Null

for ($i = 0; $i -lt 30 -and (Get-Service $servicio).Status -ne 'Stopped'; $i++) {
    Start-Sleep -Seconds 1
}

if ((Get-Service $servicio).Status -ne 'Stopped') {
    Write-Host "No se detuvo a tiempo. Abortando para no publicar sobre un binario en uso." -ForegroundColor Red
    exit 1
}

Write-Host "Publicando..."
Push-Location $repo
& dotnet publish src/DinasWms.SapSync -c Release -o $directorio --nologo -v q
Pop-Location

Write-Host "Arrancando..."
& sc.exe start $servicio | Out-Null
Start-Sleep -Seconds 10

$svc = Get-Service $servicio
Write-Host ""
Write-Host "=== Resultado ===" -ForegroundColor Cyan
Write-Host "  Estado:   $($svc.Status)"

$puerto = Get-NetTCPConnection -State Listen -LocalPort 5280 -ErrorAction SilentlyContinue
Write-Host ("  Puerto:   " + $(if ($puerto) { ($puerto | ForEach-Object { $_.LocalAddress }) -join ', ' } else { 'nadie escuchando' }))

try {
    $r = Invoke-WebRequest -Uri 'http://127.0.0.1:5280/' -UseBasicParsing -TimeoutSec 10
    Write-Host "  Pantalla: responde HTTP $($r.StatusCode)" -ForegroundColor Green
} catch {
    Write-Host "  Pantalla: NO responde" -ForegroundColor Yellow
}
