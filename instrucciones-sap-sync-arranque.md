# Instrucciones — Sincronizador SAP: Arranque del proyecto y sesión de Service Layer

**Repo nuevo (propuesto):** `dinas-wms-sap-sync` — asumo este nombre por consistencia con la convención `dinas-wms-*` del resto del proyecto; si prefieres otro, es un cambio trivial, dilo y ya.

**Qué es esto:** el componente que corre en el Windows Server (LAN-local a SQL Server/Service Layer), consume los 4 endpoints de `dinas-wms-middleware` (`/admin/sap-sync/*`, contrato v0.18.0), y hace las llamadas reales a SAP Service Layer para `IncomingPayments`/`CreditNotes`.

**Alcance de este documento — solo el arranque:**
1. Esqueleto del proyecto (.NET Worker Service, corriendo como consola por ahora).
2. Módulo de sesión de Service Layer (login/logout, manejo de cookie, certificado autofirmado).
3. Una prueba real de login/logout contra `SUPPORT_DINAS` — nada de `IncomingPayments`/`CreditNotes` todavía, eso viene después por ensayo y error una vez la sesión funcione.

**No cubre:** el scheduler de ventanas, el consumo de los endpoints `/admin/sap-sync/*` del middleware, ni los payloads reales de `IncomingPayments`/`CreditNotes`. Eso es la siguiente fase, después de validar la sesión.

---

## 1. Por qué Worker Service (no un Windows Service tradicional todavía)

Usa el patrón `Microsoft.Extensions.Hosting` (`Worker Service` template de .NET). Es el mismo código en dos modos:

- **Ahora (fase de ensayo-error):** se corre como consola (`dotnet run`), con logs en vivo y breakpoints normales.
- **Más adelante (producción):** el mismo código se instala como Windows Service, sin tocar la lógica de negocio.

No construyas nada específico de "modo consola" vs "modo servicio" todavía — el Worker Service template ya resuelve esto por diseño. Concéntrate en la lógica en sí.

---

## 2. Configuración (no comitear credenciales)

```json
// appsettings.Development.json (NO comitear con credenciales reales — usar
// dotnet user-secrets, o variables de entorno, para Username/Password)
{
  "ServiceLayer": {
    "BaseUrl": "https://192.168.11.200:50000/b1s/v1/",
    "CompanyDB": "SUPPORT_DINAS",
    "UserName": "<pendiente — usuario NUEVO dedicado a esta integración, no el de Attain>",
    "Password": "<pendiente — lo doy yo por fuera del chat>",
    "TrustSelfSignedCertificate": true
  }
}
```

**Nota sobre la URL:** el sincronizador ahora corre en un equipo dedicado dentro de la misma red de oficina, NO en el servidor mismo (decisión tomada para no mezclar carga de desarrollo con el servidor de producción). Por eso la URL usa la IP de LAN del servidor (`192.168.11.200`) en vez de `localhost` — sigue siendo tráfico local (misma red, sin cruzar WAN ni el firewall externo), solo que ya no es la misma máquina física. `https://apisap.dinascorp.com:50000/b1s/v1/` sigue siendo exclusivamente para Attain (acceso externo).

Usa `dotnet user-secrets init` + `dotnet user-secrets set "ServiceLayer:UserName" "..."` para las credenciales reales — nunca en el `.json` versionado.

---

## 3. Certificado autofirmado

Service Layer usa un certificado autofirmado (confirmado). El `HttpClientHandler` necesita confiar en él explícitamente:

```csharp
var handler = new HttpClientHandler
{
    ServerCertificateCustomValidationCallback =
        HttpClientHandler.DangerousAcceptAnyServerCertificateValidation
};
```

Nota de seguridad para dejar explícita en el código (comentario): esto acepta CUALQUIER certificado, no solo el de Service Layer. Es aceptable para este caso porque la conexión es LAN-local a un servidor conocido y controlado por Camilo, pero si el `HttpClient` llegara a usarse para llamar a algo más (no debería), este bypass sería un riesgo real. Mantén este `HttpClientHandler` exclusivo para el cliente de Service Layer, no lo compartas con otros usos.

---

## 4. Módulo de sesión — patrón recomendado: login/logout POR CICLO, no sesión persistente

Dado que la sincronización ya se decidió como **por ventanas/ciclos** (no continua en tiempo real), evita la complejidad de trackear expiración de sesión y renovarla a mitad de camino. En su lugar:

- Al iniciar cada ciclo de trabajo (ventana programada, o "forzar ahora"): `POST {baseUrl}/Login` con `{CompanyDB, UserName, Password}`.
- Guarda la cookie de sesión (`B1SESSION`, y `ROUTEID` si aparece) — el `HttpClient` necesita un `CookieContainer` que las persista automáticamente en las siguientes llamadas del mismo ciclo.
- Al terminar el ciclo (todo procesado, o algo falló de forma no recuperable): `POST {baseUrl}/Logout`. Esto libera el slot de sesión del lado de SAP — importante no dejarlas colgadas, ya que las sesiones activas cuentan contra los límites de licencia/conexión de Service Layer, y Attain ya tiene sus propias sesiones activas.
- Si una llamada devuelve 401 a mitad de un ciclo (sesión expiró antes de lo esperado): reintenta con un login nuevo, una vez. Si vuelve a fallar, aborta el ciclo y repórtalo como error — no reintentes en loop infinito.

Esto es deliberadamente más simple que un manejo de expiración/renovación continuo — encaja con el diseño de ventanas ya decidido, y evita construir algo más complejo de lo que este caso de uso necesita.

---

## 5. Primera prueba real (objetivo de este documento)

1. Login contra `SUPPORT_DINAS` con las credenciales que te voy a dar por fuera del chat.
2. Confirma que la respuesta trae `SessionId`/cookie correctamente.
3. Haz una llamada de lectura simple cualquiera (ej. `GET {baseUrl}/CompanyService_GetCompanyInfo` o el endpoint más simple que tengas a mano de la documentación) para confirmar que la sesión realmente autentica, no solo que el login "parece" exitoso.
4. Logout.
5. Repórtame el resultado — especialmente si `SUPPORT_DINAS` funciona como `CompanyDB` de Service Layer (ver la nota de alerta al inicio: si falla aquí, es la primera señal real de que necesitamos otro nombre de Company DB).

**No avances a payloads de `IncomingPayments`/`CreditNotes` todavía** — eso es la siguiente fase, una vez esta prueba pase.

---

## 6. Qué NO hacer en este bloque

- No construyas el scheduler de ventanas todavía.
- No consumas los endpoints `/admin/sap-sync/*` del middleware todavía — eso es la fase después de esta.
- No comitees credenciales reales en ningún archivo versionado.
- No asumas la forma exacta de los payloads de `IncomingPayments`/`CreditNotes` — eso se define por ensayo y error contra la documentación real de Service Layer que ya tienes, en la siguiente fase.
