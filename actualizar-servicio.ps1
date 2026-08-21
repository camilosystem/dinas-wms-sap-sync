# Republica el binario y reinicia el servicio.
#
# Es el ciclo normal despues de un cambio de codigo: el servicio tiene el .exe
# bloqueado mientras corre, asi que hay que detenerlo, publicar y volver a
# arrancar. La base sap-sync.db NO se toca — configuracion e historial
# sobreviven.
#
# CORRER COMO ADMINISTRADOR.
#
# TODO lo que imprime queda ademas en C:\DinasWmsSapSync\despliegue.log. Es a
# proposito: este script se suele lanzar con `Start-Process -Verb RunAs`, que
# abre una consola nueva y la cierra sola al terminar. Sin el log en disco, un
# fallo se pierde sin dejar rastro y el binario viejo sigue corriendo como si
# nada — que es exactamente lo que paso el 14-ago-2026 y no se detecto hasta
# tres dias despues.
#
# MODO SOLO LECTURA:
#
#   .\actualizar-servicio.ps1 -SoloVerificar
#       Compara el binario instalado contra el HEAD del repo y sale. No detiene
#       el servicio, no publica, no requiere elevacion, no escribe el log.
#
#   .\actualizar-servicio.ps1 -SoloVerificar -ShaEsperado <sha40>
#       Compara contra un SHA explicito. Sirve para verificar contra un commit
#       que no es HEAD, y para probar la propia asercion.

param(
    [switch] $SoloVerificar,
    [string] $ShaEsperado
)

$ErrorActionPreference = 'Stop'

$servicio   = 'DinasWmsSapSync'
$directorio = 'C:\DinasWmsSapSync'
$repo       = 'C:\Users\Financial Advisor\Pictures\dinas-wms-sap-sync'
$log        = Join-Path $directorio 'despliegue.log'
$rutaDll    = Join-Path $directorio 'DinasWms.SapSync.dll'

# --- Identidad del binario ---------------------------------------------------
#
# El SDK de .NET estampa el SHA de git del momento de compilacion en el
# AssemblyInformationalVersion, que sale por VersionInfo.ProductVersion con la
# forma "1.0.0+<sha40>". Lo escribe el compilador, no una mano: es la unica
# afirmacion sobre su propio origen que el binario hace por si mismo.
function Obtener-ShaDelBinario([string]$ruta) {
    if (-not (Test-Path $ruta)) { return $null }

    $pv = (Get-Item $ruta).VersionInfo.ProductVersion

    if ($pv -match '\+([0-9a-fA-F]{40})') { return $Matches[1].ToLower() }

    return $null
}

# Resuelve git. Vive en el LOCALAPPDATA del usuario, asi que en una consola
# elevada de otra cuenta puede no resolver.
function Obtener-Git() {
    $g = (Get-Command git -ErrorAction SilentlyContinue).Source
    if (-not $g) { $g = "$env:LOCALAPPDATA\Programs\Git\cmd\git.exe" }
    if (Test-Path $g) { return $g }
    return $null
}

# --- LA ASERCION -------------------------------------------------------------
#
# Devuelve $true solo si el binario instalado declara EXACTAMENTE el SHA que se
# esperaba. Cualquier otro desenlace —no coincide, el binario no declara nada,
# no se sabe que esperar— es un fallo: un despliegue que no se puede verificar
# no es un despliegue confirmado. Los tres casos se distinguen en el mensaje a
# proposito, porque se arreglan de forma distinta.
function Verificar-Binario([string]$ruta, [string]$esperado) {
    $instalado = Obtener-ShaDelBinario $ruta

    Write-Host ""
    Write-Host "=== Verificacion del binario instalado ===" -ForegroundColor Cyan
    Write-Host "  Archivo:   $ruta"
    Write-Host "  Esperado:  $(if ($esperado) { $esperado } else { '(no se pudo determinar)' })"
    Write-Host "  Declarado: $(if ($instalado) { $instalado } else { '(el binario no declara SHA)' })"

    if (-not $esperado) {
        Write-Host ""
        Write-Host "  NO VERIFICABLE: no se pudo determinar que SHA esperar (git no resolvio)." -ForegroundColor Red
        Write-Host "  El binario puede estar bien o mal. Nadie lo sabe." -ForegroundColor Red
        return $false
    }

    if (-not $instalado) {
        Write-Host ""
        Write-Host "  NO VERIFICABLE: el ensamblado instalado no declara SHA de origen." -ForegroundColor Red
        Write-Host "  Se compilo fuera de un repo git, o el SDK dejo de estampar" -ForegroundColor Red
        Write-Host "  SourceRevisionId. Sin esa marca este script no puede confirmar nada." -ForegroundColor Red
        return $false
    }

    if ($instalado -ne $esperado.ToLower()) {
        Write-Host ""
        Write-Host "  *** NO COINCIDE ***" -ForegroundColor Red
        Write-Host "  El binario que quedo instalado NO es el que se esperaba." -ForegroundColor Red
        Write-Host "  No des el despliegue por hecho: lo que corre es otro codigo." -ForegroundColor Red
        return $false
    }

    Write-Host ""
    Write-Host "  COINCIDE. El binario instalado es exactamente el commit esperado." -ForegroundColor Green
    return $true
}

# --- Modo solo verificar -----------------------------------------------------
#
# Sale antes de tocar nada. No abre el transcript a proposito: despliegue.log es
# el registro de los despliegues, y una verificacion no es un despliegue —
# ensuciarlo con corridas de prueba haria menos legible el unico rastro que
# queda cuando algo falla de madrugada.
if ($SoloVerificar) {
    $esperado = $ShaEsperado

    if (-not $esperado) {
        $git = Obtener-Git
        if ($git) { $esperado = (& $git -C $repo rev-parse HEAD) -join '' }
    }

    if (Verificar-Binario $rutaDll $esperado) { exit 0 } else { exit 1 }
}

# --- Log en disco ------------------------------------------------------------
# Se abre ANTES de cualquier chequeo para que hasta "no eres administrador"
# quede escrito. Si el transcript no se puede abrir, seguimos igual: perder el
# log es malo, no desplegar por eso seria peor.
$transcript = $false
try {
    if (-not (Test-Path $directorio)) { New-Item -ItemType Directory -Path $directorio | Out-Null }
    Start-Transcript -Path $log -Append | Out-Null
    $transcript = $true
} catch {
    Write-Host "AVISO: no se pudo abrir $log — esta corrida no deja rastro." -ForegroundColor Yellow
}

# Todo camino de salida pasa por aca, para no dejar el transcript abierto.
function Salir([int]$codigo, [string]$mensaje, [string]$color) {
    Write-Host ""
    Write-Host $mensaje -ForegroundColor $color
    Write-Host "Log: $log"
    if ($transcript) { try { Stop-Transcript | Out-Null } catch { } }
    exit $codigo
}

Write-Host ""
Write-Host "=== Despliegue $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===" -ForegroundColor Cyan

$principal = New-Object Security.Principal.WindowsPrincipal(
    [Security.Principal.WindowsIdentity]::GetCurrent())

if (-not $principal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
    Salir 1 "Esta consola NO es de administrador. Detener un servicio requiere elevacion." 'Red'
}

# Deja anotado QUE se esta desplegando, y captura el SHA completo que despues
# tiene que declarar el binario. Sin esto no hay contra que verificar.
$commit    = 'desconocido'
$shaDestino = $null
$git = Obtener-Git
if ($git) {
    $commit     = (& $git -C $repo log -1 --pretty='%h %ad %s' --date=short) -join ''
    $shaDestino = (& $git -C $repo rev-parse HEAD) -join ''
}
Write-Host "  Commit a desplegar: $commit"
Write-Host "  SHA esperado:       $(if ($shaDestino) { $shaDestino } else { '(git no resolvio — el despliegue no se podra verificar)' })"

Write-Host "Deteniendo $servicio..."
& sc.exe stop $servicio | Out-Null

for ($i = 0; $i -lt 30 -and (Get-Service $servicio).Status -ne 'Stopped'; $i++) {
    Start-Sleep -Seconds 1
}

if ((Get-Service $servicio).Status -ne 'Stopped') {
    Salir 1 "No se detuvo a tiempo. Abortando para no publicar sobre un binario en uso." 'Red'
}

Write-Host "Publicando..."
Push-Location $repo
& dotnet publish src/DinasWms.SapSync -c Release -o $directorio --nologo -v q
$publico = $LASTEXITCODE
Pop-Location

# EL CHEQUEO QUE FALTABA. `& dotnet` que falla NO lanza excepcion: solo deja un
# $LASTEXITCODE distinto de cero. Sin esto, un publish fallido seguia de largo,
# arrancaba el binario VIEJO y el script cerraba con "Estado: Running" y
# "Pantalla: responde HTTP 200" — verde perfecto sobre un despliegue que no
# ocurrio.
if ($publico -ne 0) {
    Write-Host ""
    Write-Host "PUBLISH FALLO (exit $publico). NO se desplego nada nuevo." -ForegroundColor Red
    Write-Host "Rearrancando el binario ANTERIOR para no dejar la maquina sin servicio..."
    & sc.exe start $servicio | Out-Null
    Start-Sleep -Seconds 5
    Salir 1 "Sigue corriendo el binario viejo. Revisa el error de compilacion arriba." 'Red'
}

Write-Host "Arrancando..."
& sc.exe start $servicio | Out-Null
Start-Sleep -Seconds 10

$svc = Get-Service $servicio
Write-Host ""
Write-Host "=== Resultado ===" -ForegroundColor Cyan
Write-Host "  Estado:   $($svc.Status)"

# El binario recien publicado tiene que ser de HACE UN RATO. Si la fecha es
# vieja, el publish no lo reemplazo aunque haya devuelto 0 — verificar el
# artefacto, no solo el codigo de salida.
$dll   = Get-Item $rutaDll
$edad  = (Get-Date) - $dll.LastWriteTime
Write-Host ("  Binario:  {0:yyyy-MM-dd HH:mm:ss} ({1:N0} min de antiguedad)" -f $dll.LastWriteTime, $edad.TotalMinutes)

if ($edad.TotalMinutes -gt 10) {
    Write-Host "  ADVERTENCIA: el binario NO es nuevo. El despliegue no surtio efecto." -ForegroundColor Red
}

$puerto = Get-NetTCPConnection -State Listen -LocalPort 5280 -ErrorAction SilentlyContinue
Write-Host ("  Puerto:   " + $(if ($puerto) { ($puerto | ForEach-Object { $_.LocalAddress }) -join ', ' } else { 'nadie escuchando' }))

try {
    $r = Invoke-WebRequest -Uri 'http://127.0.0.1:5280/' -UseBasicParsing -TimeoutSec 10
    Write-Host "  Pantalla: responde HTTP $($r.StatusCode)" -ForegroundColor Green
} catch {
    Write-Host "  Pantalla: NO responde" -ForegroundColor Yellow
}

# La fecha del archivo y "Running" dicen que ALGO se publico y arranco. Solo
# esto dice QUE codigo quedo. Es la ultima palabra del script a proposito.
$verificado = Verificar-Binario $rutaDll $shaDestino

if ($svc.Status -eq 'Running' -and $edad.TotalMinutes -le 10 -and $verificado) {
    Salir 0 "Despliegue OK y VERIFICADO — $commit" 'Green'
} else {
    Salir 1 "Despliegue INCOMPLETO o NO VERIFICADO. Ver arriba." 'Red'
}
