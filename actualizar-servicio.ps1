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
# MODOS:
#
#   .\actualizar-servicio.ps1
#       Despliega. Respalda el binario instalado en .\anterior\, publica,
#       rearranca, y VERIFICA que lo que quedo instalado sea el commit que se
#       quiso desplegar.
#
#   .\actualizar-servicio.ps1 -Revertir
#       Restaura el binario de .\anterior\, rearranca, y verifica que lo
#       restaurado sea exactamente lo que habia respaldado.
#
#   .\actualizar-servicio.ps1 -SoloVerificar [-ShaEsperado <sha40>]
#       Solo lectura. Compara el binario instalado contra el HEAD del repo (o
#       contra un SHA explicito) y sale. No detiene el servicio, no publica, no
#       requiere elevacion, no escribe el log.

param(
    [switch] $SoloVerificar,
    [string] $ShaEsperado,
    [switch] $Revertir,
    # Solo para el arnes de pruebas: define las funciones y vuelve, sin
    # ejecutar nada. Permite probar respaldo y restauracion contra directorios
    # de sandbox, ejercitando ESTAS funciones y no una copia de la logica.
    [switch] $SoloCargarFunciones,
    # Solo para el arnes de pruebas: reemplaza la lista de rutas donde buscar
    # git, para poder comprobar que el script dice NO VERIFICABLE cuando NINGUNA
    # resuelve. Encontrar git no puede ser la unica forma de que un despliegue
    # se de por bueno, y eso hay que poder demostrarlo.
    [string[]] $CandidatosGit
)

$ErrorActionPreference = 'Stop'

$servicio   = 'DinasWmsSapSync'
$directorio = 'C:\DinasWmsSapSync'
$anterior   = Join-Path $directorio 'anterior'
$repo       = 'C:\Users\Financial Advisor\Pictures\dinas-wms-sap-sync'
$log        = Join-Path $directorio 'despliegue.log'
$rutaDll    = Join-Path $directorio 'DinasWms.SapSync.dll'

# Lo que se respalda y se restaura. Lista explicita a proposito: nada de
# comodines, nada de copiar el directorio entero. sap-sync.db NO esta aca y no
# debe estarlo — la base guarda configuracion e historial de ciclos, y volver
# atras el binario no debe volver atras los datos.
$ArchivosDelBinario = @('DinasWms.SapSync.exe', 'DinasWms.SapSync.dll')

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

# Resuelve git. Ojo con las DOS trampas, las dos vistas en la corrida real del
# 21-ago-2026, que dejaron la verificacion en "NO VERIFICABLE" sobre un
# despliegue que en realidad habia salido bien:
#
#   1. La consola elevada corre como DINAS\Administrator, NO como el usuario
#      que instalo git. $env:LOCALAPPDATA apunta entonces al perfil de
#      Administrator, donde no hay git. Por eso la lista incluye la ruta
#      absoluta del perfil que si lo tiene, ademas de la variable.
#   2. git se niega a operar sobre un repo de OTRO usuario ("detected dubious
#      ownership"). Se resuelve en Invocar-Git.
function Obtener-CandidatosGit() {
    # El arnes de pruebas puede reemplazar la lista entera.
    if ($CandidatosGit -and $CandidatosGit.Count -gt 0) { return $CandidatosGit }

    $lista = @()

    $enPath = (Get-Command git -ErrorAction SilentlyContinue).Source
    if ($enPath) { $lista += $enPath }

    $lista += "$env:LOCALAPPDATA\Programs\Git\cmd\git.exe"
    $lista += 'C:\Users\Financial Advisor\AppData\Local\Programs\Git\cmd\git.exe'
    $lista += 'C:\Program Files\Git\cmd\git.exe'

    return $lista
}

function Obtener-Git() {
    foreach ($c in (Obtener-CandidatosGit)) {
        if ($c -and (Test-Path $c)) { return $c }
    }
    return $null
}

# Corre git contra el repo y devuelve la salida, o $null si no se pudo. Que
# devuelva $null es una respuesta legitima: quien llama tiene que tratarla como
# "no se sabe", nunca como "esta bien".
#
# safe.directory se pasa acotado a ESTE repo —no con comodin— porque el proceso
# elevado corre como Administrator y el repo es del usuario Financial Advisor;
# sin eso git aborta por "dubious ownership" aunque el ejecutable si resuelva.
function Invocar-Git([string[]]$argumentos) {
    $git = Obtener-Git
    if (-not $git) { return $null }

    try {
        $salida = & $git -c "safe.directory=$repo" -C $repo @argumentos 2>&1

        if ($LASTEXITCODE -ne 0) {
            Write-Host "  git respondio error (exit $LASTEXITCODE): $(($salida -join ' ').Trim())" -ForegroundColor Yellow
            return $null
        }

        return ($salida -join '').Trim()
    } catch {
        Write-Host "  git lanzo una excepcion: $($_.Exception.Message)" -ForegroundColor Yellow
        return $null
    }
}

# --- LA ASERCION -------------------------------------------------------------
#
# Devuelve $true solo si el binario instalado declara EXACTAMENTE el SHA que se
# esperaba. Cualquier otro desenlace —no coincide, el binario no declara nada,
# no se sabe que esperar— es un fallo: un despliegue que no se puede verificar
# no es un despliegue confirmado. Los tres casos se distinguen en el mensaje a
# proposito, porque se arreglan de forma distinta.
#
# La usan por igual el despliegue y el repliegue: si el discriminador vale para
# confirmar que quedo instalado el codigo nuevo, vale para confirmar que quedo
# instalado el viejo.
function Verificar-Binario([string]$ruta, [string]$esperado) {
    $instalado = Obtener-ShaDelBinario $ruta

    Write-Host ""
    Write-Host "=== Verificacion del binario instalado ===" -ForegroundColor Cyan
    Write-Host "  Archivo:   $ruta"
    Write-Host "  Esperado:  $(if ($esperado) { $esperado } else { '(no se pudo determinar)' })"
    Write-Host "  Declarado: $(if ($instalado) { $instalado } else { '(el binario no declara SHA)' })"

    if (-not $esperado) {
        Write-Host ""
        Write-Host "  NO VERIFICABLE: no se pudo determinar que SHA esperar." -ForegroundColor Red
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
        Write-Host "  No des la operacion por hecha: lo que corre es otro codigo." -ForegroundColor Red
        return $false
    }

    Write-Host ""
    Write-Host "  COINCIDE. El binario instalado es exactamente el commit esperado." -ForegroundColor Green
    return $true
}

# --- Respaldo ----------------------------------------------------------------

# Mira que hay en la carpeta de respaldo SIN tocar nada. Devuelve un objeto con
# Completo/Faltantes/Sha. Esto es lo que se consulta ANTES de detener el
# servicio: un respaldo inservible tiene que descubrirse mientras el servicio
# todavia esta arriba.
function Inspeccionar-Respaldo([string]$carpeta) {
    $faltantes = @()

    if (-not (Test-Path $carpeta)) {
        $faltantes = $ArchivosDelBinario
    } else {
        foreach ($a in $ArchivosDelBinario) {
            if (-not (Test-Path (Join-Path $carpeta $a))) { $faltantes += $a }
        }
    }

    $sha = $null
    if ($faltantes.Count -eq 0) {
        $sha = Obtener-ShaDelBinario (Join-Path $carpeta 'DinasWms.SapSync.dll')
    }

    return [pscustomobject]@{
        Carpeta   = $carpeta
        Existe    = (Test-Path $carpeta)
        Faltantes = $faltantes
        # Completo exige los archivos Y que el DLL declare SHA: un respaldo que
        # no se puede verificar despues de restaurarlo no sirve como respaldo.
        Completo  = ($faltantes.Count -eq 0 -and $sha)
        Sha       = $sha
    }
}

# Copia los archivos del binario de un lado a otro y COMPRUEBA cada copia por
# hash. Una copia interrumpida a la mitad devuelve $false en vez de pasar por
# buena.
function Copiar-Binario([string]$desde, [string]$hacia) {
    if (-not (Test-Path $hacia)) { New-Item -ItemType Directory -Path $hacia | Out-Null }

    $ok = $true

    foreach ($a in $ArchivosDelBinario) {
        $origen  = Join-Path $desde $a
        $destino = Join-Path $hacia $a

        if (-not (Test-Path $origen)) {
            Write-Host "  FALTA en el origen: $a" -ForegroundColor Red
            $ok = $false
            continue
        }

        try {
            Copy-Item -Path $origen -Destination $destino -Force
        } catch {
            Write-Host "  NO SE PUDO COPIAR $a : $($_.Exception.Message)" -ForegroundColor Red
            $ok = $false
            continue
        }

        $hOrigen  = (Get-FileHash $origen  -Algorithm SHA256).Hash
        $hDestino = (Get-FileHash $destino -Algorithm SHA256).Hash

        if ($hOrigen -ne $hDestino) {
            Write-Host "  COPIA CORRUPTA: $a no coincide por hash." -ForegroundColor Red
            $ok = $false
        } else {
            Write-Host ("  {0,-26} {1}" -f $a, $hDestino.Substring(0, 16) + "...")
        }
    }

    return $ok
}

if ($SoloCargarFunciones) { return }

# --- Modo solo verificar -----------------------------------------------------
#
# Sale antes de tocar nada. No abre el transcript a proposito: despliegue.log es
# el registro de los despliegues, y una verificacion no es un despliegue —
# ensuciarlo con corridas de prueba haria menos legible el unico rastro que
# queda cuando algo falla de madrugada.
if ($SoloVerificar) {
    $esperado = $ShaEsperado

    if (-not $esperado) {
        Write-Host "  git: $(if (Obtener-Git) { Obtener-Git } else { 'NO RESUELVE en ninguna ruta conocida' })"
        $esperado = Invocar-Git @('rev-parse', 'HEAD')
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

function Detener-Servicio() {
    Write-Host "Deteniendo $servicio..."
    & sc.exe stop $servicio | Out-Null

    for ($i = 0; $i -lt 30 -and (Get-Service $servicio).Status -ne 'Stopped'; $i++) {
        Start-Sleep -Seconds 1
    }

    return ((Get-Service $servicio).Status -eq 'Stopped')
}

Write-Host ""
Write-Host "=== $(if ($Revertir) { 'REPLIEGUE' } else { 'Despliegue' }) $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===" -ForegroundColor Cyan

$principal = New-Object Security.Principal.WindowsPrincipal(
    [Security.Principal.WindowsIdentity]::GetCurrent())

if (-not $principal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
    Salir 1 "Esta consola NO es de administrador. Detener un servicio requiere elevacion." 'Red'
}

# --- Repliegue ---------------------------------------------------------------
if ($Revertir) {
    Write-Host "Inspeccionando el respaldo en $anterior..."
    $insp = Inspeccionar-Respaldo $anterior

    # EL ORDEN IMPORTA. Esto corre ANTES de detener el servicio, para que un
    # respaldo ausente o incompleto —la primera vez, o si alguien borro la
    # carpeta— falle con el servicio todavia ARRIBA. Nunca dejar la maquina
    # detenida creyendo que restauro algo.
    if (-not $insp.Completo) {
        Write-Host ""
        Write-Host "  NO HAY UN RESPALDO UTILIZABLE. No se toco el servicio." -ForegroundColor Red
        if (-not $insp.Existe) {
            Write-Host "  La carpeta $anterior no existe." -ForegroundColor Red
            Write-Host "  Se crea en el primer despliegue: si nunca desplegaste con" -ForegroundColor Red
            Write-Host "  este script, no hay nada a lo que volver." -ForegroundColor Red
        } elseif ($insp.Faltantes.Count -gt 0) {
            Write-Host "  Faltan archivos: $($insp.Faltantes -join ', ')" -ForegroundColor Red
        } else {
            Write-Host "  El DLL respaldado no declara SHA: no habria forma de" -ForegroundColor Red
            Write-Host "  confirmar que la restauracion quedo bien." -ForegroundColor Red
        }
        Write-Host ""
        Write-Host "  El servicio sigue como estaba: $((Get-Service $servicio).Status)" -ForegroundColor Yellow
        Salir 1 "Repliegue ABORTADO. Nada que restaurar." 'Red'
    }

    Write-Host "  Respaldo completo. SHA respaldado: $($insp.Sha)"

    if (-not (Detener-Servicio)) {
        Salir 1 "No se detuvo a tiempo. Abortando para no restaurar sobre un binario en uso." 'Red'
    }

    Write-Host "Restaurando..."
    $copiado = Copiar-Binario $anterior $directorio

    # Se rearranca pase lo que pase con la copia: dejar el servicio abajo es
    # peor que dejarlo arriba con un binario dudoso, y la asercion de abajo va
    # a decir exactamente cual quedo.
    Write-Host "Arrancando..."
    & sc.exe start $servicio | Out-Null
    Start-Sleep -Seconds 10

    $svc = Get-Service $servicio
    Write-Host "  Estado:   $($svc.Status)"

    if (-not $copiado) {
        Write-Host "  LA COPIA FALLO O QUEDO A MEDIAS." -ForegroundColor Red
    }

    # La misma asercion que el despliegue, esperando el SHA del binario
    # respaldado. Una restauracion a medias no se puede dar por buena.
    $verificado = Verificar-Binario $rutaDll $insp.Sha

    if ($svc.Status -eq 'Running' -and $copiado -and $verificado) {
        Salir 0 "Repliegue OK y VERIFICADO — corriendo $($insp.Sha)" 'Green'
    } else {
        Salir 1 "Repliegue INCOMPLETO o NO VERIFICADO. Ver arriba." 'Red'
    }
}

# --- Despliegue --------------------------------------------------------------

# Deja anotado QUE se esta desplegando, y captura el SHA completo que despues
# tiene que declarar el binario. Sin esto no hay contra que verificar.
$git = Obtener-Git
Write-Host "  git:                $(if ($git) { $git } else { 'NO RESUELVE en ninguna ruta conocida' })"

$shaDestino = Invocar-Git @('rev-parse', 'HEAD')
$commit     = Invocar-Git @('log', '-1', '--pretty=%h %ad %s', '--date=short')
if (-not $commit) { $commit = 'desconocido' }
Write-Host "  Commit a desplegar: $commit"
Write-Host "  SHA esperado:       $(if ($shaDestino) { $shaDestino } else { '(git no resolvio — el despliegue no se podra verificar)' })"

if (-not (Detener-Servicio)) {
    Salir 1 "No se detuvo a tiempo. Abortando para no publicar sobre un binario en uso." 'Red'
}

# Respaldo ANTES de publicar. `dotnet publish -o` sobrescribe en el sitio: si no
# se copia ahora, el binario anterior deja de existir y no hay camino de vuelta.
Write-Host "Respaldando el binario actual en $anterior..."
if (-not (Copiar-Binario $directorio $anterior)) {
    Write-Host ""
    Write-Host "EL RESPALDO FALLO. No se publica: un despliegue del que no se puede" -ForegroundColor Red
    Write-Host "volver no vale el riesgo." -ForegroundColor Red
    Write-Host "Rearrancando el binario actual, que sigue intacto..."
    & sc.exe start $servicio | Out-Null
    Start-Sleep -Seconds 5
    Salir 1 "Despliegue ABORTADO antes de tocar nada. Sigue corriendo el binario de siempre." 'Red'
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
    Write-Host ""
    Write-Host "  Para volver atras: .\actualizar-servicio.ps1 -Revertir" -ForegroundColor Cyan
    Salir 0 "Despliegue OK y VERIFICADO — $commit" 'Green'
} else {
    Write-Host ""
    Write-Host "  Para volver atras: .\actualizar-servicio.ps1 -Revertir" -ForegroundColor Cyan
    Salir 1 "Despliegue INCOMPLETO o NO VERIFICADO. Ver arriba." 'Red'
}
