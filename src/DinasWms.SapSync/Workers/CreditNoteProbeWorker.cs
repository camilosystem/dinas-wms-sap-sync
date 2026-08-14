using System.Net;
using System.Text.Json;
using DinasWms.SapSync.Middleware;
using DinasWms.SapSync.ServiceLayer;
using DinasWms.SapSync.Sync;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DinasWms.SapSync.Workers;

/// <summary>
/// Integra UNA tarea real de la cola <c>credit-requests</c>: arma la nota de
/// crédito, la crea en SAP y reporta el resultado.
/// </summary>
/// <remarks>
/// ⚠ En modo real ESCRIBE EN SAP Y ES IRREVERSIBLE: una nota de crédito se
/// ANULA, no se borra, y devuelve inventario.
///
/// Por eso tiene un modo intermedio que las facturas no tuvieron:
/// <c>--Probe:DraftOnly=true</c> arma <b>el mismo payload</b> y lo manda a
/// <c>/Drafts</c>, lo relee y lo borra. Es un ensayo completo contra SAP —
/// incluida la resolución del DocEntry y la validación de montos— sin asentar
/// nada. Conviene correrlo siempre antes del real.
///
/// <para>
/// Este worker es el ARNÉS, no la lógica: armar la nota vive en
/// <see cref="CreditNoteIntegrator"/>, para que lo que se prueba a mano y lo que
/// algún día corra solo no puedan divergir. Acá quedan las banderas de la línea
/// de comandos, traer la tarea de la cola y reportar el resultado — las tres
/// cosas que un ciclo automático haría distinto.
/// </para>
///
/// Uso:
///   --RunMode=CreditNoteProbe --Probe:TaskId=20 --Probe:DraftOnly=true --Probe:Confirm=true
///   --RunMode=CreditNoteProbe --Probe:TaskId=20 --Probe:Confirm=true      (REAL)
/// </remarks>
public sealed class CreditNoteProbeWorker : BackgroundService
{
    private readonly IServiceLayerSessionFactory _sessionFactory;
    private readonly IMiddlewareClient _middleware;
    private readonly CreditNoteIntegrator _integrador;
    private readonly IConfiguration _configuration;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<CreditNoteProbeWorker> _logger;

    public CreditNoteProbeWorker(
        IServiceLayerSessionFactory sessionFactory,
        IMiddlewareClient middleware,
        CreditNoteIntegrator integrador,
        IConfiguration configuration,
        IHostApplicationLifetime lifetime,
        ILogger<CreditNoteProbeWorker> logger)
    {
        _sessionFactory = sessionFactory;
        _middleware = middleware;
        _integrador = integrador;
        _configuration = configuration;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        try
        {
            await CorrerAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Apagado normal.
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            _logger.LogError(ex, "PRUEBA DE NOTA DE CRÉDITO FALLIDA. {Message}", ex.Message);
        }
        finally
        {
            _lifetime.StopApplication();
        }
    }

    private async Task CorrerAsync(CancellationToken cancellationToken)
    {
        var soloBorrador = Bandera("Probe:DraftOnly");
        var confirmado = Bandera("Probe:Confirm");
        var omitirReporte = Bandera("Probe:SkipReport");

        if (!int.TryParse(_configuration["Probe:TaskId"], out var taskId) || taskId <= 0)
        {
            Environment.ExitCode = 1;
            _logger.LogError("Falta Probe:TaskId. Ej: --Probe:TaskId=20");
            return;
        }

        // --- 1. La tarea real -------------------------------------------------
        await _middleware.LoginAsync(cancellationToken).ConfigureAwait(false);

        var tarea = await BuscarTareaAsync(taskId, cancellationToken).ConfigureAwait(false);

        if (tarea?.CreditRequest is null)
        {
            Environment.ExitCode = 1;
            _logger.LogError("La tarea {TaskId} no está pendiente o no trae credit_request.", taskId);
            return;
        }

        // --- 2. Integrar ------------------------------------------------------
        await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var resultado = await _integrador
            .IntegrarAsync(
                session,
                tarea,
                cancellationToken,
                simular: !confirmado,
                soloBorrador: soloBorrador)
            .ConfigureAwait(false);

        // --- 3. Qué hacer con el desenlace ------------------------------------
        if (resultado.Error is not null)
        {
            Environment.ExitCode = 1;
            _logger.LogError(
                "La tarea {TaskId} NO se pudo integrar: {Error}\n" +
                "No se reporta nada al middleware desde este arnés: qué hacer con la tarea lo " +
                "decide un humano.",
                taskId,
                resultado.Error);
            return;
        }

        if (resultado.Simulada)
        {
            _logger.LogWarning(
                "SIMULACIÓN — no se envió nada. {Aviso}",
                soloBorrador
                    ? "Hace falta --Probe:Confirm=true (crea un borrador, que sí se borra)."
                    : "Esta nota es REAL e IRREVERSIBLE. Hace falta --Probe:Confirm=true.");
            return;
        }

        // Un total distinto del aprobado no impide que el documento exista, pero sí
        // tiene que teñir la salida del arnés: es lo que alguien mira para decidir
        // si la corrida sirvió.
        if (resultado.TotalDiscrepante)
        {
            Environment.ExitCode = 1;
        }

        if (resultado.EnsayoEnBorrador)
        {
            if (resultado.Advertencia is not null)
            {
                Environment.ExitCode = 1;
                _logger.LogError("{Advertencia}", resultado.Advertencia);
                return;
            }

            _logger.LogInformation(
                "=== ENSAYO TERMINADO. El payload es válido para SAP y no quedó nada asentado. ===");
            return;
        }

        if (resultado.CreadaSinPoderLeerNumero)
        {
            Environment.ExitCode = 1;
            _logger.LogError(
                "SAP aceptó la nota pero no se pudo leer su DocNum: NO se reporta nada (reportar " +
                "ERROR sería mentir y reintentar la duplicaría). El anti-duplicado del próximo " +
                "ciclo lo resuelve.");
            return;
        }

        if (!resultado.Integrada || resultado.DocNum is null)
        {
            Environment.ExitCode = 1;
            _logger.LogError("Desenlace inesperado del integrador para la tarea {TaskId}.", taskId);
            return;
        }

        // --- 4. Cerrar el ciclo -----------------------------------------------
        // Un duplicado hallado durante un ENSAYO no cierra nada: el ensayo no
        // asienta documentos, así que tampoco puede decidir el estado de la tarea.
        if (soloBorrador)
        {
            _logger.LogWarning(
                "Ensayo en borrador: la nota {DocNum} ya existía en SAP. No se reporta nada.",
                resultado.DocNum);
            return;
        }

        if (omitirReporte)
        {
            _logger.LogWarning("--Probe:SkipReport=true — no se reporta al middleware.");
            return;
        }

        await ReportarAsync(
            taskId, SapSyncResultReport.Integrado(resultado.DocNum.Value), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<SapCreditRequestSyncTask?> BuscarTareaAsync(
        int taskId,
        CancellationToken cancellationToken)
    {
        const string ruta = "admin/sap-sync/credit-requests/pending";

        var (status, body) = await _middleware.GetAsync(ruta, cancellationToken).ConfigureAwait(false);

        if (status != HttpStatusCode.OK)
        {
            throw new MiddlewareException(
                $"El middleware devolvió {(int)status} al pedir la cola de créditos.", status, body);
        }

        var pagina = JsonSerializer.Deserialize<SapCreditRequestSyncTasksPage>(body, SapSyncJson.Options);
        return pagina?.Tasks?.FirstOrDefault(t => t.TaskId == taskId);
    }

    private async Task ReportarAsync(
        int taskId,
        SapSyncResultReport reporte,
        CancellationToken cancellationToken)
    {
        var ruta = $"admin/sap-sync/credit-requests/{taskId}/result";

        var (status, body) = await _middleware
            .PostJsonAsync(ruta, reporte.ToJson(), cancellationToken)
            .ConfigureAwait(false);

        if (status == HttpStatusCode.OK)
        {
            _logger.LogInformation(
                "=== CICLO CERRADO. Tarea {TaskId} reportada como {Estado}. Respuesta:\n{Body}",
                taskId,
                reporte.Status,
                string.IsNullOrWhiteSpace(body) ? "(cuerpo vacío)" : body);

            // Desde v0.32.0 la tarea devuelve sap_reference. Comprobar que quedó
            // guardado el número correcto es distinto de saber que la llamada no
            // falló: lo segundo ya lo sabíamos, lo primero es lo que importa.
            VerificarReferencia(body, reporte.SapReference);
            return;
        }

        Environment.ExitCode = 1;
        _logger.LogError(
            "FALLÓ el reporte del resultado ({Codigo}). La nota está en SAP y el middleware NO lo " +
            "sabe: {Body}",
            (int)status,
            body);
    }

    /// <summary>
    /// Comprueba que el <c>sap_reference</c> que devolvió el middleware sea el
    /// número que se le reportó.
    /// </summary>
    private void VerificarReferencia(string body, string? reportada)
    {
        SapCreditRequestSyncTask? tarea;

        try
        {
            tarea = JsonSerializer.Deserialize<SapCreditRequestSyncTask>(body, SapSyncJson.Options);
        }
        catch (JsonException)
        {
            _logger.LogWarning(
                "No se pudo interpretar la respuesta del reporte, así que no se pudo comprobar el " +
                "sap_reference.");
            return;
        }

        if (string.IsNullOrWhiteSpace(tarea?.SapReference))
        {
            _logger.LogWarning(
                "El middleware no devolvió sap_reference. La tarea quedó en {Estado}, pero desde " +
                "acá no se puede confirmar que el número {Reportada} se haya guardado.",
                tarea?.Status,
                reportada);
            return;
        }

        if (string.Equals(tarea.SapReference, reportada, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "sap_reference CONFIRMADO: el middleware guardó {Referencia}.", tarea.SapReference);
        }
        else
        {
            Environment.ExitCode = 1;
            _logger.LogError(
                "sap_reference NO COINCIDE: se reportó {Reportada} y el middleware guardó " +
                "{Guardada}. El documento de SAP y lo que dice el middleware apuntan a números " +
                "distintos.",
                reportada,
                tarea.SapReference);
        }
    }

    private bool Bandera(string clave) =>
        string.Equals(_configuration[clave], "true", StringComparison.OrdinalIgnoreCase);
}
