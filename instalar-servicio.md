# Instalar el sincronizador como Windows Service

Requiere una consola **como administrador**. Los comandos van tal cual.

## 1. Publicar

Desde la raíz del repo:

```
dotnet publish src/DinasWms.SapSync -c Release -o C:\DinasWmsSapSync
```

El servicio corre desde `C:\DinasWmsSapSync`, **fuera del directorio de
compilación**. Eso es deliberado: mientras el servicio esté vivo, `dotnet build`
sobre el repo no lo toca. Antes, compilar mataba al proceso porque el `.exe`
estaba bloqueado — y así se perdieron 23 horas sin que nadie se enterara.

Republicar sí exige detener el servicio primero (`sc stop DinasWmsSapSync`).

## 2. Crear el servicio

**La cuenta importa.** El sincronizador lee sus credenciales de
`dotnet user-secrets`, que son *por usuario*: viven en el perfil de
`DINAS\Financial Advisor`. Un servicio que corra como `LocalSystem` **no las
encuentra** — buscaría en `C:\Windows\System32\config\systemprofile\...`, que
está vacío — y abortaría al arrancar con "Configuración inválida".

Por eso se instala corriendo **como ese mismo usuario**:

```
sc.exe create DinasWmsSapSync ^
  binPath= "C:\DinasWmsSapSync\DinasWms.SapSync.exe" ^
  start= auto ^
  obj= "DINAS\Financial Advisor" ^
  password= "LA-CONTRASENA-DE-WINDOWS" ^
  DisplayName= "Dinas WMS - Sincronizador SAP"

sc.exe description DinasWmsSapSync "Integra facturas y pagos del WMS con SAP Business One."
```

La contraseña se escribe en esa consola y en ningún otro lado: no va a un
archivo, no va al repo, no va al chat.

*(Alternativa si no se quiere usar esa cuenta: mover las credenciales a
variables de entorno de máquina. Es un cambio de postura de seguridad —
cualquier administrador local podría leerlas— así que conviene decidirlo a
propósito, no por comodidad.)*

## 3. Que se recupere solo si se cae

```
sc.exe failure DinasWmsSapSync reset= 86400 actions= restart/5000/restart/10000/restart/30000
```

Reinicia a los 5, 10 y 30 segundos, y el contador se resetea cada 24 horas.

## 4. Arrancar y verificar

```
sc.exe start DinasWmsSapSync
sc.exe qc DinasWmsSapSync
sc.exe query DinasWmsSapSync
```

`START_TYPE` tiene que decir `AUTO_START` — eso es lo que lo levanta solo
después de un reinicio de la máquina.

La pantalla queda en `http://100.126.181.94:5280` (y en `127.0.0.1:5280` desde
la propia máquina).

## 5. Comprobar que sobrevive a un reinicio

```
shutdown /r /t 0
```

Al volver, sin tocar nada:

```
sc.exe query DinasWmsSapSync
```

Tiene que decir `RUNNING`.

## Desinstalar

```
sc.exe stop DinasWmsSapSync
sc.exe delete DinasWmsSapSync
```

## Dónde mirar si no arranca

Los errores de arranque van al **Visor de eventos → Registros de Windows →
Aplicación**, con origen `DinasWmsSapSync`. El mensaje "Configuración inválida,
no se arranca" con una credencial faltante es el síntoma de que el servicio no
está viendo los user-secrets: revisar con qué cuenta quedó configurado
(`sc qc DinasWmsSapSync`, campo `SERVICE_START_NAME`).

---

## Atajo: el script

En vez de tipear los `sc.exe` a mano —su sintaxis es quisquillosa, el espacio
después de cada `=` es obligatorio— hay un script que hace todo y verifica:

```
.\instalar-servicio.ps1
```

Corre **como administrador**. Pide la contraseña de forma segura (no queda en el
historial de la consola), detecta si ya hay un proceso suelto o un servicio
anterior, configura la recuperación automática, arranca y comprueba que la
pantalla responda.

Si se corre sin elevación se niega y no toca nada.
