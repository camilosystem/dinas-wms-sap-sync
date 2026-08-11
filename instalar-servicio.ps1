# Instala el sincronizador como Windows Service.
#
# CORRER COMO ADMINISTRADOR. Click derecho en PowerShell -> "Ejecutar como
# administrador", y desde ahí:
#
#   cd "C:\Users\Financial Advisor\Pictures\dinas-wms-sap-sync"
#   .\instalar-servicio.ps1
#
# La contraseña de Windows se pide de forma segura y no queda en el historial de
# la consola, ni en un archivo, ni en el repo.

$ErrorActionPreference = 'Stop'

$servicio  = 'DinasWmsSapSync'
$directorio = 'C:\DinasWmsSapSync'
$ejecutable = Join-Path $directorio 'DinasWms.SapSync.exe'

# --- Comprobaciones antes de tocar nada --------------------------------------

$identidad = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identidad)

if (-not $principal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
    Write-Host "Esta consola NO es de administrador." -ForegroundColor Red
    Write-Host "Crear un servicio requiere elevacion. Abri PowerShell como administrador y repeti."
    exit 1
}

if (-not (Test-Path $ejecutable)) {
    Write-Host "No existe $ejecutable" -ForegroundColor Red
    Write-Host "Publica primero, desde la raiz del repo:"
    Write-Host "  dotnet publish src/DinasWms.SapSync -c Release -o $directorio"
    exit 1
}

# Un proceso de consola corriendo desde el mismo directorio pelearia por el
# puerto 5280 y por la cola del middleware.
$sueltos = Get-Process 'DinasWms.SapSync' -ErrorAction SilentlyContinue
if ($sueltos) {
    Write-Host "Hay $($sueltos.Count) proceso(s) del sincronizador corriendo fuera del servicio."
    $r = Read-Host "Detenerlos? (s/n)"
    if ($r -eq 's') { $sueltos | Stop-Process -Force; Start-Sleep -Seconds 2 }
    else { Write-Host "Cancelado: dos instancias a la vez pelearian por el puerto." -ForegroundColor Red; exit 1 }
}

if (Get-Service $servicio -ErrorAction SilentlyContinue) {
    Write-Host "El servicio $servicio ya existe."
    $r = Read-Host "Borrarlo y reinstalarlo? (s/n)"
    if ($r -ne 's') { exit 0 }
    & sc.exe stop $servicio | Out-Null
    Start-Sleep -Seconds 3
    & sc.exe delete $servicio | Out-Null
    Start-Sleep -Seconds 2
}

# --- La cuenta ----------------------------------------------------------------
#
# NO se instala como LocalSystem. Las credenciales de SAP, SQL y del middleware
# viven en `dotnet user-secrets`, que son POR USUARIO: estan en el perfil de
# DINAS\Financial Advisor. Un servicio como LocalSystem las buscaria en
# C:\Windows\System32\config\systemprofile\... — verificado que ahi no estan — y
# abortaria al arrancar con "Configuracion invalida, no se arranca".
#
# Corriendo como ese mismo usuario, funciona sin mover ningun secreto.

$cuenta = 'DINAS\Financial Advisor'
Write-Host ""
Write-Host "El servicio va a correr como: $cuenta"
Write-Host "(es el perfil donde ya viven los user-secrets; no se mueve ningun secreto)"
$clave = Read-Host "Contrasena de Windows de $cuenta" -AsSecureString
$claveTexto = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
    [Runtime.InteropServices.Marshal]::SecureStringToBSTR($clave))

# --- Crear ---------------------------------------------------------------------

Write-Host ""
Write-Host "Creando el servicio..."
$r = & sc.exe create $servicio `
    binPath= $ejecutable `
    start= auto `
    obj= $cuenta `
    password= $claveTexto `
    DisplayName= "Dinas WMS - Sincronizador SAP"
$claveTexto = $null
Write-Host "  $r"

& sc.exe description $servicio "Integra facturas y pagos del WMS con SAP Business One. Interfaz de monitoreo en http://100.126.181.94:5280" | Out-Null

# Recuperacion: si se cae, que vuelva solo a los 5, 10 y 30 segundos. El contador
# se resetea cada 24 horas.
& sc.exe failure $servicio reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null

# --- Arrancar y verificar -------------------------------------------------------

Write-Host ""
Write-Host "Arrancando..."
& sc.exe start $servicio | Out-Null
Start-Sleep -Seconds 8

$svc = Get-Service $servicio
Write-Host ""
Write-Host "=== Resultado ===" -ForegroundColor Cyan
Write-Host "  Estado:      $($svc.Status)"
Write-Host "  Arranque:    $($svc.StartType)   <- tiene que decir Automatic"

$qc = & sc.exe qc $servicio
$cuentaReal = ($qc | Select-String 'SERVICE_START_NAME').ToString().Split(':')[1].Trim()
Write-Host "  Corre como:  $cuentaReal"

try {
    $resp = Invoke-WebRequest -Uri 'http://127.0.0.1:5280/' -UseBasicParsing -TimeoutSec 10
    Write-Host "  Pantalla:    responde HTTP $($resp.StatusCode)" -ForegroundColor Green
} catch {
    Write-Host "  Pantalla:    NO responde" -ForegroundColor Yellow
    Write-Host "               Si el servicio esta Running pero la pantalla no contesta, lo mas"
    Write-Host "               probable es que no encontro las credenciales. Mirar el Visor de"
    Write-Host "               eventos -> Aplicacion, origen $servicio."
}

Write-Host ""
Write-Host "Falta la ultima comprobacion, que solo se puede hacer reiniciando:" -ForegroundColor Cyan
Write-Host "  shutdown /r /t 0"
Write-Host "y al volver, sin tocar nada:"
Write-Host "  sc.exe query $servicio      # tiene que decir RUNNING"
