// Interfaz del sincronizador. JS plano, sin build ni dependencias.
//
// El token de sesión se guarda en sessionStorage y viaja en el header
// Authorization, NO en una cookie: así ninguna página ajena puede hacer que el
// navegador lo mande por su cuenta, y el CSRF deja de aplicar.

'use strict';

const CLAVE_TOKEN = 'sap-sync-token';
const MS_ENTRE_REFRESCOS = 3000;

let token = sessionStorage.getItem(CLAVE_TOKEN);
let ultimoIdLog = 0;
let temporizador = null;

const $ = (id) => document.getElementById(id);

async function pedir(ruta, opciones = {}) {
  const cabeceras = Object.assign({ 'Content-Type': 'application/json' }, opciones.headers || {});
  if (token) {
    cabeceras['Authorization'] = 'Bearer ' + token;
  }

  const respuesta = await fetch(ruta, Object.assign({}, opciones, { headers: cabeceras }));

  if (respuesta.status === 401) {
    // La sesión venció o no vale. Se vuelve al login sin dramatizar.
    cerrarSesionLocal();
    throw new Error('Sesión vencida.');
  }

  const cuerpo = await respuesta.json().catch(() => ({}));
  if (!respuesta.ok) {
    throw new Error(cuerpo.error || ('Error ' + respuesta.status));
  }
  return cuerpo;
}

// --- Login -----------------------------------------------------------------

$('form-login').addEventListener('submit', async (evento) => {
  evento.preventDefault();
  $('error-login').textContent = '';

  try {
    const datos = await pedir('/api/login', {
      method: 'POST',
      body: JSON.stringify({ usuario: $('usuario').value, clave: $('clave').value }),
    });

    token = datos.token;
    sessionStorage.setItem(CLAVE_TOKEN, token);
    $('quien').textContent = (datos.nombre || datos.usuario) + ' · ' + datos.usuario;
    $('clave').value = '';
    mostrarPanel();
  } catch (error) {
    $('error-login').textContent = error.message;
  }
});

$('salir').addEventListener('click', async () => {
  try { await pedir('/api/logout', { method: 'POST' }); } catch { /* da igual */ }
  cerrarSesionLocal();
});

function cerrarSesionLocal() {
  token = null;
  sessionStorage.removeItem(CLAVE_TOKEN);
  if (temporizador) { clearInterval(temporizador); temporizador = null; }
  $('panel').classList.add('oculto');
  $('login').classList.remove('oculto');
}

function mostrarPanel() {
  $('login').classList.add('oculto');
  $('panel').classList.remove('oculto');
  refrescar();
  temporizador = setInterval(refrescar, MS_ENTRE_REFRESCOS);
}

// --- Estado ----------------------------------------------------------------

function celda(etiqueta, valor, clase) {
  return '<div class="celda"><span class="etiqueta">' + etiqueta + '</span>' +
         '<span class="valor ' + (clase || '') + '">' + valor + '</span></div>';
}

function hora(iso) {
  return iso ? new Date(iso).toLocaleTimeString('es') : '—';
}

function pintarEstado(e) {
  const enCurso = e.cicloEnCurso;
  const ultimo = e.ultimoResultado;

  let html = '';
  html += celda('Modo', e.modo);
  html += celda('Cadencia', e.cadenciaSegundos + 's');
  html += celda('Sesión SAP', e.sesionSapAbierta ? 'ABIERTA' : 'cerrada',
                e.sesionSapAbierta ? 'atencion' : 'ok');
  html += celda('Ciclo en curso', enCurso ? enCurso.titular : 'ninguno',
                enCurso ? 'atencion' : '');
  html += celda('Sondeos', e.sondeos);
  html += celda('Ciclos', e.ciclos);
  html += celda('Último sondeo', hora(e.ultimoSondeo));
  html += celda('Próximo intento', hora(e.proximoIntento));
  html += celda('Integrados', e.documentosIntegrados, 'ok');
  html += celda('Fallidos', e.documentosFallidos, e.documentosFallidos > 0 ? 'error' : '');
  html += celda('Fallos seguidos', e.fallosConsecutivos,
                e.fallosConsecutivos > 0 ? 'error' : '');
  html += celda('Automáticos', (e.pasosAutomaticos || []).join(', ') || '—');

  if (ultimo) {
    html += celda('Último ciclo',
      hora(ultimo.cuando) + ' · ' + (ultimo.exito ? 'OK' : 'con fallos') +
      ' · ' + ultimo.integrados + ' integrados',
      ultimo.exito ? 'ok' : 'error');
  }

  $('estado').innerHTML = html;

  if (!$('botones-disparo').dataset.listo) {
    pintarBotones(e.tiposDisparables || []);
    $('botones-disparo').dataset.listo = '1';
  }

  pintarUltimoDisparo(e.ultimoDisparoManual);
}

// --- Disparo manual --------------------------------------------------------

function pintarBotones(tipos) {
  $('botones-disparo').innerHTML = tipos
    .map((t) => '<button data-tipo="' + t + '" class="peligro">Disparar ' + t + '</button>')
    .join('');

  $('botones-disparo').querySelectorAll('button').forEach((boton) => {
    boton.addEventListener('click', () => disparar(boton.dataset.tipo));
  });
}

async function disparar(tipo) {
  // Confirmación explícita antes de crear documentos reales. El backend además
  // exige confirmar: true, así que esto no es la única defensa.
  const seguro = window.confirm(
    'Vas a disparar "' + tipo + '" AHORA.\n\n' +
    'Esto crea documentos REALES en SAP, que se anulan pero no se borran.\n\n' +
    '¿Confirmás?');

  if (!seguro) { return; }

  try {
    const disparo = await pedir('/api/disparar', {
      method: 'POST',
      body: JSON.stringify({ tipo: tipo, confirmar: true }),
    });
    pintarUltimoDisparo(disparo);
  } catch (error) {
    $('resultado-disparo').innerHTML = '<p class="error">' + error.message + '</p>';
  }
}

function pintarUltimoDisparo(d) {
  if (!d) { $('resultado-disparo').innerHTML = ''; return; }

  const clase = d.estado === 'OK' ? 'ok' : (d.estado === 'EN_CURSO' ? 'atencion' : 'error');
  $('resultado-disparo').innerHTML =
    '<p class="' + clase + '">Disparo ' + d.id + ' · ' + d.tipo + ' · ' + d.estado +
    ' · por ' + d.usuario + ' a las ' + hora(d.iniciado) +
    (d.terminado ? ' · ' + d.integrados + ' integrados, ' + d.fallidos + ' fallidos' : '') +
    (d.detalle ? '<br><small>' + d.detalle + '</small>' : '') + '</p>';
}

// --- Log -------------------------------------------------------------------

function escapar(texto) {
  const div = document.createElement('div');
  div.textContent = texto;
  return div.innerHTML;
}

function pintarLog(datos) {
  if (datos.lineas.length > 0) {
    const pre = $('log');
    const html = datos.lineas.map((l) =>
      '<span class="nivel-' + l.nivel + '">' + l.hora + ' [' + escapar(l.origen) + '] ' +
      escapar(l.mensaje) + (l.excepcion ? '\n' + escapar(l.excepcion) : '') + '</span>'
    ).join('\n');

    pre.insertAdjacentHTML('beforeend', (pre.innerHTML ? '\n' : '') + html);
    ultimoIdLog = datos.ultimoId;

    if ($('autoscroll').checked) {
      pre.scrollTop = pre.scrollHeight;
    }
  }

  $('info-buffer').textContent =
    datos.descartadas > 0
      ? '· ' + datos.descartadas + ' líneas descartadas (buffer de ' + datos.capacidad + ')'
      : '';
}

// --- Refresco --------------------------------------------------------------

async function refrescar() {
  try {
    const [estado, log] = await Promise.all([
      pedir('/api/estado'),
      pedir('/api/log?desde=' + ultimoIdLog),
    ]);
    pintarEstado(estado);
    pintarLog(log);
  } catch (error) {
    // Si la sesión venció, pedir() ya volvió al login. Cualquier otra cosa se
    // reintenta sola en el próximo tick.
    console.warn('Refresco fallido:', error.message);
  }
}

if (token) { mostrarPanel(); } else { $('login').classList.remove('oculto'); }
