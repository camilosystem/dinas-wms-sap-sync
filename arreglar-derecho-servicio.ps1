# Otorga "Log on as a service" y arranca el servicio.
#
# Para el caso puntual en que el servicio YA está instalado con la cuenta y la
# contraseña correctas pero no arranca, con este error en el Visor de eventos:
#
#   Id 7041: Logon failure: the user has not been granted the requested logon
#            type at this computer.
#
# No hace falta reinstalar nada: falta un derecho de directiva local.
#
# CORRER COMO ADMINISTRADOR.

$ErrorActionPreference = 'Stop'

$servicio = 'DinasWmsSapSync'
$cuenta   = 'DINAS\Financial Advisor'

$principal = New-Object Security.Principal.WindowsPrincipal(
    [Security.Principal.WindowsIdentity]::GetCurrent())

if (-not $principal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
    Write-Host "Esta consola NO es de administrador. Abri PowerShell como administrador." -ForegroundColor Red
    exit 1
}

$sid = (New-Object System.Security.Principal.NTAccount($cuenta)).Translate(
    [System.Security.Principal.SecurityIdentifier]).Value

Write-Host "Cuenta: $cuenta"
Write-Host "SID:    $sid"

$tmp = Join-Path $env:TEMP "secpol-$([guid]::NewGuid().ToString('N')).cfg"
& secedit /export /cfg $tmp /areas USER_RIGHTS | Out-Null
$contenido = Get-Content $tmp
$linea = $contenido | Where-Object { $_ -match '^SeServiceLogonRight' }

if ($linea -and $linea -match [regex]::Escape($sid)) {
    Write-Host "El derecho ya estaba otorgado." -ForegroundColor Yellow
} else {
    if ($linea) {
        $contenido = $contenido -replace [regex]::Escape($linea), ($linea + ",*$sid")
    } else {
        # S-1-5-19 = LOCAL SERVICE, S-1-5-20 = NETWORK SERVICE. Si la directiva
        # no existia, hay que recrearla con esas dos o se rompen otros servicios.
        $contenido = $contenido -replace '^\[Privilege Rights\]',
            "[Privilege Rights]`r`nSeServiceLogonRight = *S-1-5-19,*S-1-5-20,*$sid"
    }

    Set-Content $tmp $contenido -Encoding Unicode
    & secedit /configure /db secedit.sdb /cfg $tmp /areas USER_RIGHTS | Out-Null
    Write-Host "Derecho otorgado." -ForegroundColor Green
}

Remove-Item $tmp -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Arrancando el servicio..."
& sc.exe start $servicio | Out-Null
Start-Sleep -Seconds 10

$svc = Get-Service $servicio
Write-Host ""
Write-Host "=== Resultado ===" -ForegroundColor Cyan
Write-Host "  Estado:   $($svc.Status)"
Write-Host "  Arranque: $($svc.StartType)"

try {
    $r = Invoke-WebRequest -Uri 'http://127.0.0.1:5280/' -UseBasicParsing -TimeoutSec 10
    Write-Host "  Pantalla: responde HTTP $($r.StatusCode)" -ForegroundColor Green
} catch {
    Write-Host "  Pantalla: NO responde" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Si quedo Running, falta la ultima comprobacion:" -ForegroundColor Cyan
Write-Host "  shutdown /r /t 0"
Write-Host "y al volver:  sc.exe query $servicio"
