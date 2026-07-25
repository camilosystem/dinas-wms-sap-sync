# dinas-wms-sap-sync

Sincronizador que corre en una máquina dedicada de la red de oficina (LAN,
no en el servidor de producción ni en un VPS) y hace las llamadas reales
a SAP Business One Service Layer para integrar documentos generados por
el resto del ecosistema Dinas WMS (`dinas-wms-middleware`,
`dinas-wms-dashboard`, y las apps de vendedores/bodega/driver).

## Ambiente de pruebas

- Service Layer (desde esta máquina, vía LAN): `https://192.168.11.200:50000/b1s/v1/`
  — NO uses `https://apisap.dinascorp.com:50000/b1s/v1/`, esa es exclusiva
  para el acceso externo de Attain (otro WMS, cruza el firewall/WAN).
- Company DB de pruebas: `SUPPORT_DINAS`.
- Certificado SSL autofirmado — el cliente HTTP debe confiar en él
  explícitamente (`ServerCertificateCustomValidationCallback`), pero SOLO
  para el cliente de Service Layer, nunca compartir ese `HttpClientHandler`
  con otro uso.
- Credenciales de SAP: nunca en archivos versionados. Usar
  `dotnet user-secrets` (o variables de entorno) para `UserName`/`Password`.
  Camilo las provee por fuera del chat/código.

## Reglas de arquitectura (no reabrir sin confirmar con el Architect)

- **Patrón de sesión: login/logout POR CICLO**, no sesión persistente con
  renovación. Cada ciclo de trabajo (ventana programada, o "forzar ahora")
  hace su propio `Login`, trabaja, y `Logout` al terminar. No construyas
  tracking de expiración/renovación de sesión — es complejidad que este
  caso de uso no necesita.
- **Worker Service (`Microsoft.Extensions.Hosting`)**, corriendo como
  consola en esta fase de desarrollo/ensayo-error. Se instalará como
  Windows Service más adelante, sin cambiar la lógica de negocio.
- **DocEntry se resuelve localmente vía SQL**, nunca viaja por el
  middleware/contrato — el resto del sistema (dinas-wms-middleware,
  las apps, el Dashboard) solo conoce `DocNum`/`invoice_doc_num`. Antes
  de armar un payload que necesite `DocEntry`, resuélvelo con una consulta
  directa a `SUPPORT_DINAS` (ej. `OINV`).
- **document_type es un identificador libre** en la cola de integración
  del middleware (`SapIntegrationTask`), no un enum de base de datos —
  ver `dinas-wms-middleware` para el contrato completo de esa cola
  (`/admin/sap-sync/*`, contrato v0.18.0+).

## Qué NO hacer sin confirmar primero

- No toques `dinas-wms-middleware` — el corte de `AccountPayment`/
  `CreditRequest` del stub actual hacia la cola real (`SapIntegrationTask`)
  es un paso posterior, deliberadamente separado, y se hace en ese otro
  repo, no en este.
- No asumas la forma exacta de un payload de Service Layer sin validarlo
  con una llamada real contra `SUPPORT_DINAS` — la documentación de
  Service Layer da un punto de partida, pero este proyecto se construye
  por ensayo y error verificado, no por asunción.
- No avances a un tipo de documento nuevo (facturas, voids, etc.) sin
  que se te indique explícitamente — se construyen uno a la vez.

## Roadmap de documentos a integrar (orden, no todos construidos aún)

1. `IncomingPayments` (pagos de cartera) — en progreso.
2. `CreditNotes` (solicitudes de crédito) — pendiente.
3. Órdenes ya Picadas → Facturas — pendiente.
4. Devoluciones de Venta → Voids — pendiente.
5. Retornos de ruta (driver) → documento tipo nota de crédito — pendiente.

## Convenciones generales del proyecto Dinas WMS (heredadas)

- Verificar siempre con pruebas reales contra datos reales — un test en
  verde no es suficiente por sí solo para dar algo por cerrado.
- Si encuentras un vacío o inconsistencia real (de contrato, de diseño,
  de datos), repórtalo — no lo resuelvas improvisando una decisión de
  negocio por tu cuenta.
