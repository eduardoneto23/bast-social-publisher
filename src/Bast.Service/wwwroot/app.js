'use strict';

// Painel do BAST Social Publisher. Sem bibliotecas externas: fala só com a API local.
// Todo texto vindo da API (nomes de pasta, erros das plataformas) entra por textContent, nunca como HTML.

const REFRESH_MS = 5000;
const PLATFORMS = ['YouTube', 'Instagram', 'Facebook', 'Threads', 'TikTok'];
const DAYS = [
  ['Monday', 'Seg'], ['Tuesday', 'Ter'], ['Wednesday', 'Qua'], ['Thursday', 'Qui'],
  ['Friday', 'Sex'], ['Saturday', 'Sáb'], ['Sunday', 'Dom'],
];

const PUB_STATE = {
  Pending: ['Pendente', ''],
  Validating: ['Validando', 'info'],
  Scheduled: ['Agendado', 'info'],
  Uploading: ['Enviando', 'info'],
  Processing: ['Processando', 'info'],
  Published: ['Publicado', 'ok'],
  RetryWait: ['Nova tentativa', 'warn'],
  Failed: ['Falhou', 'bad'],
  Cancelled: ['Cancelado', ''],
  Unknown: ['Sem confirmação', 'warn'],
};

const VIDEO_STATE = {
  Importing: ['Importando', 'info'],
  Ready: ['Na fila', 'info'],
  Publishing: ['Publicando', 'info'],
  Published: ['Publicado', 'ok'],
  Error: ['Com falha', 'bad'],
  Archived: ['Arquivado', ''],
  Missing: ['Pasta não encontrada', 'bad'],
};

const ACCOUNT_STATE = {
  Connected: ['Conectada', 'ok'],
  NeedsReauth: ['Reconectar', 'bad'],
  Disconnected: ['Desconectada', ''],
};

const CONNECT_HELP = {
  YouTube: 'scripts\\connect-youtube.ps1',
  Instagram: 'scripts\\connect-meta.ps1',
  Facebook: 'scripts\\connect-meta.ps1',
  Threads: 'scripts\\connect-threads.ps1',
};

const ISSUE_LABEL = {
  MissingVideo: 'Sem arquivo de vídeo',
  MultipleVideos: 'Mais de um vídeo',
  MissingCaption: 'Sem legenda.txt',
  MultipleCaptions: 'Mais de uma legenda',
  EmptyCaption: 'Legenda vazia',
  UnreadableCaption: 'Legenda ilegível',
  CorruptVideo: 'Vídeo corrompido',
  Duplicate: 'Vídeo repetido',
  ToolUnavailable: 'ffmpeg indisponível',
  MoveFailed: 'Não foi possível mover a pasta',
  MissingThumbnail: 'Sem thumbnail',
  NotHorizontal: 'Vídeo não é horizontal',
};

const NOTIFICATION_KIND = {
  VideoFinished: 'Vídeo finalizado',
  PublicationUnknown: 'Sem confirmação',
  AccountNeedsReauth: 'Reconectar conta',
  ImportIssue: 'Pasta com problema',
  TimeNeeded: 'Aguardando horário',
  Test: 'Teste',
};

const state = {
  tab: 'overview',
  data: null,
  scheduleDirty: false,
  scheduleProblems: [],
  busy: false,
  // Horário sendo escolhido na Fila: rascunho por vídeo ({ date, time }) e qual vídeo está mudando de horário.
  timeDrafts: {},
  changingTime: null,
};

// ---------- utilidades ----------

function h(tag, props, ...children) {
  const el = document.createElement(tag);
  for (const [key, value] of Object.entries(props || {})) {
    if (value === null || value === undefined || value === false) continue;
    if (key === 'class') el.className = value;
    else if (key === 'text') el.textContent = value;
    else if (key.startsWith('on')) el.addEventListener(key.slice(2), value);
    else if (value === true) el.setAttribute(key, '');
    else el.setAttribute(key, value);
  }
  for (const child of children.flat()) {
    if (child === null || child === undefined || child === false) continue;
    el.append(child.nodeType ? child : document.createTextNode(String(child)));
  }
  return el;
}

function pill(label, kind) {
  return h('span', { class: 'pill ' + (kind || ''), text: label });
}

function timeZone() {
  return (state.data && state.data.schedule && state.data.schedule.timeZoneId) || undefined;
}

function fmt(iso, options) {
  if (!iso) return '';
  try {
    return new Intl.DateTimeFormat('pt-BR', { timeZone: timeZone(), ...options }).format(new Date(iso));
  } catch {
    return new Intl.DateTimeFormat('pt-BR', options).format(new Date(iso));
  }
}

/** Data e hora de um instante no fuso da agenda, no formato dos campos do navegador. */
function localParts(iso) {
  const options = { year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' };
  let parts;
  try {
    parts = new Intl.DateTimeFormat('en-CA', { timeZone: timeZone(), ...options }).formatToParts(new Date(iso));
  } catch {
    parts = new Intl.DateTimeFormat('en-CA', options).formatToParts(new Date(iso));
  }
  const get = type => (parts.find(p => p.type === type) || {}).value;
  return { date: `${get('year')}-${get('month')}-${get('day')}`, time: `${get('hour')}:${get('minute')}` };
}

const fmtDateTime = iso => fmt(iso, { weekday: 'short', day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' });
const fmtTime = iso => fmt(iso, { hour: '2-digit', minute: '2-digit', second: '2-digit' });

function relative(iso) {
  if (!iso) return '';
  const minutes = Math.round((new Date(iso) - Date.now()) / 60000);
  if (Math.abs(minutes) < 1) return 'agora';
  const abs = Math.abs(minutes);
  const text = abs < 60 ? `${abs} min` : abs < 1440 ? `${Math.floor(abs / 60)} h ${abs % 60} min` : `${Math.floor(abs / 1440)} d`;
  return minutes > 0 ? `em ${text}` : `há ${text}`;
}

async function api(path, method = 'GET', body) {
  const options = { method, headers: {} };
  if (body !== undefined) {
    options.headers['Content-Type'] = 'application/json';
    options.body = JSON.stringify(body);
  }
  const response = await fetch(path, options);
  let payload = null;
  try { payload = await response.json(); } catch { /* resposta sem corpo */ }
  if (!response.ok) {
    const error = new Error((payload && payload.problems && payload.problems.join(' ')) || `Erro ${response.status}`);
    error.problems = (payload && payload.problems) || [];
    throw error;
  }
  return payload;
}

function banner(message, isError) {
  const el = document.getElementById('banner');
  el.replaceChildren();
  if (!message) { el.hidden = true; return; }
  el.className = 'banner' + (isError ? ' error' : '');
  el.append(h('div', { text: message }));
  el.hidden = false;
}

let bannerTimer = null;
function notice(message, isError) {
  banner(message, isError);
  clearTimeout(bannerTimer);
  bannerTimer = setTimeout(() => banner(null), isError ? 12000 : 5000);
}

async function act(action, successMessage) {
  if (state.busy) return;
  state.busy = true;
  try {
    const result = await action();
    if (successMessage) notice(typeof successMessage === 'function' ? successMessage(result) : successMessage);
    await refresh();
  } catch (error) {
    notice(error.message || 'Não foi possível concluir.', true);
  } finally {
    state.busy = false;
  }
}

// ---------- dados ----------

const FINAL_VIDEO = new Set(['Published', 'Error', 'Archived', 'Missing']);

function derive(data) {
  const videos = data.queue || [];
  const pending = videos.filter(v => !FINAL_VIDEO.has(v.state));
  const done = videos.filter(v => FINAL_VIDEO.has(v.state));
  const lastActivity = v => v.publications.map(p => p.completedAtUtc).filter(Boolean).sort().pop() || v.scheduledAtUtc || '';
  done.sort((a, b) => lastActivity(b).localeCompare(lastActivity(a)));

  const unknown = [];
  const failed = [];
  for (const video of videos) {
    for (const publication of video.publications) {
      if (publication.state === 'Unknown') unknown.push({ video, publication });
      if (publication.state === 'Failed') failed.push({ video, publication });
    }
  }

  const awaitingTime = pending.filter(v => v.lane !== 'YouTubeHorizontal' && v.state === 'Ready' && !v.scheduledAtUtc
    && (v.publications.length === 0 || v.publications.some(p => p.state === 'Scheduled')));
  const reauth = (data.accounts || []).filter(a => a.status === 'NeedsReauth');
  const next = pending.filter(v => v.scheduledAtUtc).sort((a, b) => a.scheduledAtUtc.localeCompare(b.scheduledAtUtc))[0] || null;
  return { pending, done, unknown, failed, reauth, next, awaitingTime, lastActivity, issues: data.issues || [] };
}

async function refresh() {
  try {
    const [health, queue, issues, accounts, schedule, telegram, notifications] = await Promise.all([
      api('/api/health'), api('/api/queue'), api('/api/import-issues'), api('/api/accounts'),
      api('/api/settings/schedule'), api('/api/connect/telegram/status'), api('/api/notifications'),
    ]);
    state.data = { health, queue, issues, accounts, schedule, telegram, notifications };
    state.view = derive(state.data);
    state.offline = false;
    render();
  } catch (error) {
    state.offline = true;
    document.getElementById('health-line').textContent = 'Sem resposta do serviço. Confira se o BAST Social Publisher está em execução.';
    document.getElementById('updated').textContent = '';
  }
}

// ---------- blocos reutilizados ----------

function publicationRow(publication) {
  // TikTok assistido: o vídeo foi entregue pelo Telegram e espera o usuário publicar no app.
  const waitingYou = publication.platform === 'TikTok' && publication.state === 'Processing';
  const [label, kind] = waitingYou ? ['Aguardando você', 'warn'] : PUB_STATE[publication.state] || [publication.state, ''];
  const detail = [];
  if (publication.simulated) detail.push('simulação');
  if (publication.attemptCount > 1) detail.push(`${publication.attemptCount} tentativas`);
  if (publication.state === 'RetryWait' && publication.nextRetryAtUtc) detail.push(`próxima tentativa ${relative(publication.nextRetryAtUtc)}`);
  if (publication.state === 'Published' && publication.completedAtUtc) detail.push(fmtDateTime(publication.completedAtUtc));

  const link = publication.externalUrl && /^https:\/\//i.test(publication.externalUrl)
    ? h('a', { href: publication.externalUrl, target: '_blank', rel: 'noopener noreferrer', text: 'abrir' })
    : null;

  return h('div', { class: 'pub' },
    h('span', { text: publication.platform }),
    pill(label, kind),
    h('span', { class: 'detail' },
      detail.join(' · '), detail.length && link ? ' · ' : '', link,
      publication.lastError && publication.state !== 'Published'
        ? h('div', { class: publication.state === 'Failed' ? 'error-text' : '', text: publication.lastError })
        : null,
      publication.warning ? h('div', { class: 'warn-text', text: '⚠ ' + publication.warning }) : null,
      waitingYou
        ? h('div', { class: 'top-actions' },
          h('span', { class: 'small', text: 'Vídeo e legenda enviados ao seu Telegram.' }),
          h('button', {
            class: 'button small primary', type: 'button', text: 'Publiquei',
            onclick: () => act(() => api(`/api/publications/${publication.id}/manual`, 'POST', { published: true }), r => r.message),
          }),
          confirmButton('skip:' + publication.id, 'Não vou publicar', 'Não publicar no TikTok?',
            () => act(() => api(`/api/publications/${publication.id}/manual`, 'POST', { published: false }), r => r.message)))
        : null));
}

function videoItem(video, options = {}) {
  const awaiting = state.view.awaitingTime.includes(video);
  const [label, kind] = awaiting ? ['Aguardando horário', 'warn'] : VIDEO_STATE[video.state] || [video.state, ''];
  const manual = state.data.schedule.mode === 'Manual';
  const horizontal = video.lane === 'YouTubeHorizontal';
  const when = video.scheduledAtUtc
    ? `${fmtDateTime(video.scheduledAtUtc)}${FINAL_VIDEO.has(video.state) ? '' : ' (' + relative(video.scheduledAtUtc) + ')'}`
    : horizontal && video.state === 'Ready' ? 'Sem horário: aguardando o YouTube estar conectado.'
    : awaiting && manual ? 'Escolha quando publicar.'
      : awaiting ? 'Sem horário. Escolha um horário ou espere a agenda automática.'
        : 'sem horário: nenhuma rede conectada ou habilitada';

  // O horário só pode mudar enquanto nenhuma rede começou a publicar.
  const timeEditable = video.state === 'Ready'
    && (video.publications.length === 0 || video.publications.every(p => ['Scheduled', 'Failed', 'Cancelled'].includes(p.state)))
    && (video.publications.length === 0 || video.publications.some(p => p.state === 'Scheduled'));
  const showPicker = options.cancel && timeEditable && (awaiting || state.changingTime === video.id);
  const canPublishNow = options.cancel && video.state === 'Ready'
    && (video.publications.some(p => p.state === 'Scheduled') || (awaiting && video.publications.length === 0));

  const cancellable = options.cancel && video.publications.some(p => ['Pending', 'Scheduled', 'RetryWait', 'Validating'].includes(p.state));
  return h('div', { class: 'item' },
    h('div', { class: 'row' },
      h('div', {},
        h('div', { class: 'title', text: video.folderName }),
        h('div', { class: 'muted small', text: when })),
      h('div', { class: 'top-actions' },
        horizontal ? pill('YouTube horizontal', 'info') : null,
        pill(label, kind),
        options.cancel && timeEditable && !showPicker && video.scheduledAtUtc
          ? h('button', {
            class: 'button small', type: 'button', text: 'Mudar horário',
            onclick: () => { state.changingTime = video.id; render(true); },
          }) : null,
        canPublishNow
          ? confirmButton('now:' + video.id, 'Publicar agora', 'Publicar agora em todas as redes?',
            () => act(() => api(`/api/videos/${video.id}/publish-now`, 'POST'),
              r => r.publications > 0
                ? 'Envio antecipado. Começa em alguns segundos.'
                : 'Este vídeo não está mais aguardando o horário.')) : null,
        cancellable ? confirmButton('cancel:' + video.id, 'Cancelar', 'Cancelar este vídeo?',
          () => act(() => api(`/api/videos/${video.id}/cancel`, 'POST'),
            r => r.notCancellable > 0
              ? `${r.cancelled} cancelada(s). ${r.notCancellable} não puderam ser canceladas porque já estavam em envio.`
              : 'Publicação cancelada. A pasta vai para ARQUIVADOS.')) : null)),
    showPicker ? timePicker(video) : null,
    video.publications.length
      ? h('div', { class: 'pubs' }, video.publications.map(publicationRow))
      : null);
}

/** Quantos vídeos já estão marcados no dia (fuso da agenda), sem contar o próprio vídeo. */
function videosOnDay(date, exceptId, lane) {
  return (state.data.queue || []).filter(v => v.id !== exceptId && v.scheduledAtUtc && (v.lane || 'Standard') === lane
    && ['Ready', 'Publishing', 'Published'].includes(v.state)
    && localParts(v.scheduledAtUtc).date === date).length;
}

/** Escolha de data e hora de publicação, no fuso da agenda. */
function timePicker(video) {
  const schedule = state.data.schedule;
  const lane = video.lane || 'Standard';
  const limit = lane === 'YouTubeHorizontal' ? schedule.horizontalPerDay : schedule.chosenTimeMaxPerDay;
  const now = localParts(new Date().toISOString());
  const draft = state.timeDrafts[video.id]
    || (video.scheduledAtUtc ? localParts(video.scheduledAtUtc) : { date: now.date, time: '' });

  const dateInput = h('input', { type: 'date', value: draft.date, min: now.date, required: true, 'aria-label': 'Data da publicação' });
  const timeInput = h('input', { type: 'time', value: draft.time, required: true, 'aria-label': 'Hora da publicação' });
  const hint = h('span', { class: 'small muted' });
  const update = () => {
    state.timeDrafts[video.id] = { date: dateInput.value, time: timeInput.value };
    const count = dateInput.value ? videosOnDay(dateInput.value, video.id, lane) : 0;
    hint.textContent = dateInput.value ? `Nesse dia: ${count} de ${limit} vídeo(s).` : '';
    hint.className = 'small ' + (count >= limit ? 'error-text' : 'muted');
  };
  dateInput.addEventListener('input', update);
  timeInput.addEventListener('input', update);
  update();

  const save = () => {
    if (!dateInput.value || !timeInput.value) { notice('Escolha a data e a hora.', true); return; }
    // Sai do campo para o painel voltar a se atualizar depois de salvar.
    if (document.activeElement) document.activeElement.blur();
    act(async () => {
      const result = await api(`/api/videos/${video.id}/time`, 'POST', { localTime: `${dateInput.value}T${timeInput.value}` });
      delete state.timeDrafts[video.id];
      state.changingTime = null;
      return result;
    }, r => r.message);
  };

  return h('div', { class: 'time-picker top-actions' },
    h('span', { class: 'small', text: 'Publicar em' }),
    dateInput, timeInput,
    h('button', { class: 'button small primary', type: 'button', text: 'Agendar', onclick: save }),
    state.changingTime === video.id
      ? h('button', {
        class: 'button small', type: 'button', text: 'Desistir',
        onclick: () => { state.changingTime = null; delete state.timeDrafts[video.id]; render(true); },
      }) : null,
    hint,
    h('span', { class: 'small muted', text: `Fuso: ${schedule.timeZoneId}` }));
}

/** Botão de ação que pede confirmação na própria página, em dois passos. */
function confirmButton(key, label, question, onConfirm) {
  if (state.confirming !== key) {
    return h('button', {
      class: 'button small danger', type: 'button', text: label,
      onclick: () => { state.confirming = key; render(true); },
    });
  }

  return h('span', { class: 'top-actions' },
    h('span', { class: 'small', text: question }),
    h('button', {
      class: 'button small danger', type: 'button', text: 'Sim',
      onclick: () => { state.confirming = null; onConfirm(); },
    }),
    h('button', {
      class: 'button small', type: 'button', text: 'Não',
      onclick: () => { state.confirming = null; render(true); },
    }));
}

function card(title, ...children) {
  return h('div', { class: 'card' }, title ? h('h2', { text: title }) : null, ...children);
}

function emptyText(text) {
  return h('p', { class: 'empty', text });
}

// ---------- seções ----------

function renderOverview() {
  const { data, view } = state;
  const published = view.done.filter(v => v.state === 'Published').length;
  const problems = view.issues.length + view.failed.length + view.unknown.length + view.reauth.length;

  const stats = h('div', { class: 'grid' },
    stat(view.pending.length, 'Na fila'),
    stat(view.next ? fmtDateTime(view.next.scheduledAtUtc) : '—', view.next ? `Próxima publicação (${relative(view.next.scheduledAtUtc)})` : 'Próxima publicação'),
    stat(published, 'Vídeos publicados'),
    stat(problems, 'Problemas em aberto'));

  const accounts = h('div', { class: 'checks' }, data.accounts
    .filter(a => a.integrationImplemented)
    .map(a => {
      const [label, kind] = ACCOUNT_STATE[a.status] || [a.status, ''];
      return h('span', {}, a.platform + ' ', pill(a.displayName ? `${label}: ${a.displayName}` : label, kind));
    }),
    h('span', {}, 'Avisos (Telegram) ', pill(data.telegram.connected ? 'Conectado' : 'Desconectado', data.telegram.connected ? 'ok' : '')));

  const alerts = [];
  if (data.health.simulationMode) alerts.push('Modo de simulação ativo: nada é enviado às redes.');
  if (data.schedule.paused) alerts.push('A fila está pausada: nenhuma publicação nova começa até você retomar.');
  if (!data.health.ffmpeg || !data.health.ffprobe) alerts.push('ffmpeg ou ffprobe não encontrado: os vídeos não podem ser validados.');
  for (const account of view.reauth) alerts.push(`${account.platform}: é preciso conectar a conta de novo.`);
  if (view.awaitingTime.length) alerts.push(`${view.awaitingTime.length} vídeo(s) aguardando você escolher o horário na aba Fila.`);
  if (view.unknown.length) alerts.push(`${view.unknown.length} envio(s) sem confirmação da plataforma. Veja em Problemas.`);

  return [
    stats,
    alerts.length ? card('Atenção', h('div', { class: 'stack' }, alerts.map(text => h('div', { class: 'error-text', text })))) : null,
    card('Contas', accounts),
    card('Próximos na fila', view.pending.length
      ? view.pending.slice(0, 3).map(v => videoItem(v))
      : emptyText('Nada na fila. Coloque uma pasta com video.mp4 e legenda.txt em "01 - ENVIAR VERTICAL", ou um vídeo horizontal com legenda.txt e thumbnail em "01 - ENVIAR YOUTUBE".')),
    card('Últimos finalizados', view.done.length ? view.done.slice(0, 3).map(v => videoItem(v)) : emptyText('Nenhum vídeo finalizado ainda.')),
  ];
}

function stat(value, label) {
  return h('div', { class: 'card stat' }, h('div', { class: 'value', text: String(value) }), h('div', { class: 'label', text: label }));
}

function renderQueue() {
  const { view } = state;
  return [card('Fila de publicação', view.pending.length
    ? view.pending.map(v => videoItem(v, { cancel: true }))
    : emptyText('Nada na fila.'))];
}

function renderHistory() {
  const { view } = state;
  const shown = view.done.slice(0, 100);
  return [card(`Histórico (${view.done.length})`,
    shown.length ? shown.map(v => videoItem(v)) : emptyText('Nenhum vídeo finalizado ainda.'),
    view.done.length > shown.length ? h('p', { class: 'muted small', text: `Mostrando os ${shown.length} mais recentes.` }) : null)];
}

function renderProblems() {
  const { view } = state;
  const blocks = [];

  if (view.reauth.length) {
    blocks.push(card('Contas a reconectar', view.reauth.map(a => h('div', { class: 'item' },
      h('div', { class: 'title', text: a.platform }),
      h('div', { class: 'error-text', text: a.lastError || '' }),
      CONNECT_HELP[a.platform] ? h('div', { class: 'small muted' }, 'Rode: ', h('code', { text: CONNECT_HELP[a.platform] })) : null))));
  }

  if (view.unknown.length) {
    blocks.push(card('Envios sem confirmação',
      h('p', { class: 'muted small', text: 'A plataforma não confirmou o resultado. O sistema continua consultando e não envia de novo sem certeza. Vale conferir a conta.' }),
      view.unknown.map(({ video, publication }) => h('div', { class: 'item' },
        h('div', { class: 'title', text: video.folderName }), publicationRow(publication)))));
  }

  if (view.failed.length) {
    blocks.push(card('Publicações que falharam',
      view.failed.map(({ video, publication }) => h('div', { class: 'item' },
        h('div', { class: 'title', text: video.folderName }), publicationRow(publication)))));
  }

  blocks.push(card('Pastas com problema', view.issues.length
    ? view.issues.map(issue => h('div', { class: 'item' },
      h('div', { class: 'row' },
        h('div', { class: 'title', text: issue.folderName }),
        pill(ISSUE_LABEL[issue.code] || issue.code, 'warn')),
      h('div', { text: issue.message }),
      issue.movedToPath ? h('div', { class: 'small muted' }, 'Movida para: ', h('code', { text: issue.movedToPath })) : null,
      h('div', { class: 'small muted', text: `Visto pela última vez ${relative(issue.lastSeenAtUtc)}` })))
    : emptyText('Nenhuma pasta com problema.')));

  return blocks;
}

function renderAccounts() {
  const { data } = state;
  const order = [...data.accounts].sort((a, b) => PLATFORMS.indexOf(a.platform) - PLATFORMS.indexOf(b.platform));

  const networks = card('Redes sociais', order.map(account => {
    const [label, kind] = ACCOUNT_STATE[account.status] || [account.status, ''];
    const enabled = data.schedule.platforms.includes(account.platform);
    let help = null;
    if (!account.integrationImplemented) {
      help = h('div', { class: 'small muted', text: 'Integração ainda não disponível.' });
    } else if (account.assisted && account.status !== 'Connected') {
      help = h('div', { class: 'small muted', text: 'Modo assistido: conecte o Telegram (abaixo) para receber o vídeo e a legenda na hora de publicar.' });
    } else if (account.assisted && !enabled) {
      help = h('div', { class: 'small muted', text: 'A API do TikTok não permite publicação automática pública. Marque o TikTok na Agenda para receber o vídeo e a legenda pelo Telegram e publicar no app.' });
    } else if (account.assisted) {
      help = h('div', { class: 'small muted', text: 'No horário, o vídeo e a legenda chegam pelo Telegram; você publica no app e toca em Publiquei.' });
    } else if (account.status !== 'Connected') {
      help = h('div', { class: 'small muted' }, 'Para conectar, rode no PowerShell: ', h('code', { text: CONNECT_HELP[account.platform] }));
    } else if (!enabled) {
      help = h('div', { class: 'small muted', text: 'Conectada, mas desmarcada na Agenda: não recebe publicações.' });
    }

    return h('div', { class: 'item' },
      h('div', { class: 'row' },
        h('div', {},
          h('div', { class: 'title', text: account.platform }),
          account.displayName ? h('div', { class: 'small muted', text: account.displayName }) : null),
        account.integrationImplemented ? pill(label, kind) : pill('Não disponível', '')),
      account.lastError ? h('div', { class: 'error-text', text: account.lastError }) : null,
      help);
  }));

  const telegram = card('Avisos pelo Telegram',
    h('div', { class: 'row' },
      h('div', {},
        h('div', { class: 'title', text: data.telegram.connected ? `Bot @${data.telegram.bot}` : 'Não conectado' }),
        data.telegram.connected ? h('div', { class: 'small muted', text: `Conversa de ${data.telegram.chat || 'um usuário'}` }) : null),
      pill(data.telegram.connected ? 'Conectado' : 'Desconectado', data.telegram.connected ? 'ok' : '')),
    data.telegram.connected
      ? h('div', { class: 'form-actions' }, h('button', {
        class: 'button', type: 'button', text: 'Enviar aviso de teste',
        onclick: () => act(async () => {
          const result = await api('/api/connect/telegram/test', 'POST');
          if (!result.ok) throw new Error(result.message);
          return result;
        }, 'Aviso de teste enviado ao Telegram.'),
      }))
      : h('div', { class: 'small muted' }, 'Para conectar, rode no PowerShell: ', h('code', { text: 'scripts\\connect-telegram.ps1' })));

  return [networks, telegram,
    h('p', { class: 'muted small', text: 'Senhas, tokens e chaves nunca aparecem aqui. A conexão é feita pelos scripts, que entregam as credenciais direto ao serviço.' })];
}

function renderSchedule() {
  // Depois de uma recusa do servidor, o formulário mostra o que o usuário digitou, não o que está salvo.
  const s = { ...state.data.schedule, ...(state.scheduleDraft || {}) };
  const connected = new Set(state.data.accounts.filter(a => a.status === 'Connected').map(a => a.platform));

  const field = (label, input) => h('label', { class: 'field' }, h('span', { text: label }), input);
  const number = (name, value, min, max) => h('input', { type: 'number', name, value: String(value), min: String(min), max: String(max), required: true });

  const form = h('form', { id: 'schedule-form', novalidate: true },
    h('div', { class: 'fields' },
      field('Como os vídeos recebem horário', h('select', { name: 'mode' },
        h('option', { value: 'FixedTimes', selected: s.mode === 'FixedTimes', text: 'Horários fixos: um vídeo por horário da lista' }),
        h('option', { value: 'Manual', selected: s.mode === 'Manual', text: 'Manual: eu escolho o horário na Fila' }),
        h('option', { value: 'Automatic', selected: s.mode === 'Automatic', text: 'Intervalo: primeira publicação + intervalo' }))),
      field('Horários dos verticais (horários fixos)', h('input', {
        type: 'text', name: 'verticalSlots', required: true, placeholder: '10:00, 16:00, 18:00',
        value: Array.isArray(s.verticalSlots) ? s.verticalSlots.join(', ') : s.verticalSlots,
      })),
      field('Máximo por dia no modo manual', number('manualMaxPerDay', s.manualMaxPerDay, 1, 100)),
      field('Primeira publicação do dia', h('input', { type: 'time', name: 'startTime', value: s.startTime, required: true })),
      field('Última publicação até', h('input', { type: 'time', name: 'endTime', value: s.endTime, required: true })),
      field('Intervalo entre vídeos (minutos)', number('intervalMinutes', s.intervalMinutes, 1, 1440)),
      field('Máximo de vídeos por dia', number('maxPerDay', s.maxPerDay, 1, 100)),
      field('Tentativas por rede', number('maxAttempts', s.maxAttempts, 1, 20)),
      field('Tolerância de atraso (minutos)', number('missedGraceMinutes', s.missedGraceMinutes, 0, 1440)),
      field('Se perder o horário', h('select', { name: 'missedPolicy' },
        h('option', { value: 'NextAllowedSlot', selected: s.missedPolicy === 'NextAllowedSlot', text: 'Publicar no próximo horário livre' }),
        h('option', { value: 'NextCycle', selected: s.missedPolicy === 'NextCycle', text: 'Esperar o dia seguinte' })))),
    h('div', { class: 'stack' },
      h('fieldset', {}, h('legend', { text: 'Dias da semana' }),
        h('div', { class: 'checks' }, DAYS.map(([value, label]) => h('label', {},
          h('input', { type: 'checkbox', name: 'days', value, checked: s.days.includes(value) }), label)))),
      h('fieldset', {}, h('legend', { text: 'Vídeos horizontais (pasta 01 - ENVIAR YOUTUBE, só YouTube)' }),
        h('div', { class: 'fields' },
          field('Horários do dia', h('input', {
            type: 'text', name: 'horizontalSlots', required: true, placeholder: '08:00, 11:00, 14:00, 17:00, 20:00',
            value: Array.isArray(s.horizontalSlots) ? s.horizontalSlots.join(', ') : s.horizontalSlots,
          }))),
        h('p', { class: 'muted small', text: `${state.data.schedule.horizontalPerDay} vídeo(s) por dia. Cada vídeo pega o próximo horário livre, um por horário, todos os dias; dia cheio vai para o dia seguinte. Pasta precisa de vídeo horizontal, legenda.txt e thumbnail.` })),
      h('fieldset', {}, h('legend', { text: 'Redes que recebem publicações' }),
        h('div', { class: 'checks' }, PLATFORMS.map(platform => h('label', {},
          h('input', { type: 'checkbox', name: 'platforms', value: platform, checked: s.platforms.includes(platform) }),
          platform, connected.has(platform) ? null : h('span', { class: 'muted small', text: '(não conectada)' })))))),
    state.scheduleProblems.length
      ? h('ul', { class: 'problems' }, state.scheduleProblems.map(text => h('li', { text })))
      : null,
    h('div', { class: 'form-actions' },
      h('button', { class: 'button primary', type: 'submit', text: 'Salvar agenda' }),
      h('button', {
        class: 'button', type: 'button', text: 'Descartar alterações',
        onclick: () => { state.scheduleDirty = false; state.scheduleDraft = null; state.scheduleProblems = []; render(true); },
      }),
      h('span', { class: 'muted small', text: `Fuso horário: ${s.timeZoneId}` })));

  form.addEventListener('input', () => { state.scheduleDirty = true; });
  form.addEventListener('submit', event => {
    event.preventDefault();
    const values = new FormData(form);
    const body = {
      startTime: values.get('startTime'),
      endTime: values.get('endTime'),
      intervalMinutes: Number(values.get('intervalMinutes')),
      maxPerDay: Number(values.get('maxPerDay')),
      days: values.getAll('days'),
      timeZoneId: s.timeZoneId,
      platforms: values.getAll('platforms'),
      missedPolicy: values.get('missedPolicy'),
      missedGraceMinutes: Number(values.get('missedGraceMinutes')),
      maxAttempts: Number(values.get('maxAttempts')),
      mode: values.get('mode'),
      manualMaxPerDay: Number(values.get('manualMaxPerDay')),
      verticalSlots: values.get('verticalSlots'),
      horizontalSlots: values.get('horizontalSlots'),
    };
    if (state.busy) return;
    state.busy = true;
    state.scheduleDraft = body;
    api('/api/settings/schedule', 'PUT', body)
      .then(() => { state.scheduleDirty = false; state.scheduleDraft = null; state.scheduleProblems = []; notice('Agenda salva.'); })
      .catch(error => { state.scheduleProblems = error.problems && error.problems.length ? error.problems : [error.message]; })
      .finally(() => { state.busy = false; refresh().then(() => render(true)); });
  });

  return [card('Agenda de publicação',
    h('p', { class: 'muted small', text: 'Cada vídeo ocupa um horário, e todas as redes marcadas recebem o vídeo nesse horário. Dois vídeos nunca saem juntos.' }),
    h('p', { class: 'muted small', text: 'Horários fixos: cada vídeo vertical (pasta 01 - ENVIAR VERTICAL) pega o próximo horário livre da lista, um por horário; com a lista cheia no dia, vai para o dia seguinte. Manual: o vídeo fica aguardando até você escolher a data e a hora na aba Fila. Intervalo: usa primeira publicação, intervalo e máximo por dia. Em qualquer modo dá para mudar o horário de um vídeo que ainda não começou na aba Fila.' }),
    form)];
}

function renderNotifications() {
  const list = state.data.notifications || [];
  return [card('Avisos recentes', list.length
    ? list.map(n => {
      const status = n.sentAtUtc ? ['Enviado ' + fmtDateTime(n.sentAtUtc), 'ok']
        : n.discardedAtUtc ? ['Descartado', '']
        : ['Aguardando', 'warn'];
      return h('div', { class: 'item' },
        h('div', { class: 'row' },
          h('div', { class: 'small muted', text: `${NOTIFICATION_KIND[n.kind] || n.kind} · ${fmtDateTime(n.createdAtUtc)}` }),
          pill(status[0], status[1])),
        h('div', { class: 'pre', text: n.text }),
        n.lastError ? h('div', { class: 'small error-text', text: n.lastError }) : null);
    })
    : emptyText('Nenhum aviso ainda.'))];
}

const RENDERERS = {
  overview: renderOverview,
  queue: renderQueue,
  history: renderHistory,
  problems: renderProblems,
  accounts: renderAccounts,
  schedule: renderSchedule,
  notifications: renderNotifications,
};

// ---------- montagem ----------

function render(force) {
  if (!state.data) return;
  const { data, view } = state;

  const healthLine = document.getElementById('health-line');
  healthLine.textContent = data.health.status === 'ok'
    ? `Serviço em execução · ${data.health.connectedPlatforms.length} rede(s) conectada(s)`
    : 'Serviço em execução com pendências (veja Visão geral)';

  const pauseState = document.getElementById('pause-state');
  pauseState.textContent = data.schedule.paused ? 'Fila pausada' : '';
  pauseState.className = 'pill ' + (data.schedule.paused ? 'warn' : '');
  const pauseButton = document.getElementById('pause-button');
  pauseButton.hidden = false;
  pauseButton.textContent = data.schedule.paused ? 'Retomar fila' : 'Pausar fila';
  pauseButton.className = 'button' + (data.schedule.paused ? ' primary' : '');

  const queueCount = document.getElementById('count-queue');
  queueCount.textContent = view.pending.length ? String(view.pending.length) : '';
  queueCount.className = 'count' + (view.awaitingTime.length ? ' alert' : '');
  const problems = view.issues.length + view.failed.length + view.unknown.length + view.reauth.length;
  const problemCount = document.getElementById('count-problems');
  problemCount.textContent = problems ? String(problems) : '';
  problemCount.className = 'count' + (problems ? ' alert' : '');

  for (const tab of document.querySelectorAll('.tab')) tab.classList.toggle('active', tab.dataset.tab === state.tab);
  for (const panel of document.querySelectorAll('.panel')) panel.hidden = panel.id !== 'tab-' + state.tab;

  // Não redesenha o formulário da agenda enquanto o usuário está editando.
  const pickingTime = document.activeElement && document.activeElement.closest && document.activeElement.closest('.time-picker');
  if ((state.tab !== 'schedule' || !state.scheduleDirty || force) && (!pickingTime || force)) {
    const panel = document.getElementById('tab-' + state.tab);
    const scroll = window.scrollY;
    panel.replaceChildren(...RENDERERS[state.tab]().filter(Boolean));
    window.scrollTo(0, scroll);
  }

  document.getElementById('updated').textContent = `Atualizado às ${fmtTime(new Date().toISOString())}`;
}

function selectTab(name) {
  state.tab = RENDERERS[name] ? name : 'overview';
  if (location.hash.slice(1) !== state.tab) history.replaceState(null, '', '#' + state.tab);
  render(true);
}

document.querySelector('.tabs').addEventListener('click', event => {
  const tab = event.target.closest('.tab');
  if (tab) selectTab(tab.dataset.tab);
});

document.getElementById('pause-button').addEventListener('click', () => {
  const paused = state.data && state.data.schedule.paused;
  act(() => api(paused ? '/api/queue/resume' : '/api/queue/pause', 'POST'), paused ? 'Fila retomada.' : 'Fila pausada: nenhuma publicação nova começa até você retomar.');
});

window.addEventListener('hashchange', () => selectTab(location.hash.slice(1)));

state.tab = RENDERERS[location.hash.slice(1)] ? location.hash.slice(1) : 'overview';
refresh();
setInterval(() => { if (!document.hidden) refresh(); }, REFRESH_MS);
document.addEventListener('visibilitychange', () => { if (!document.hidden) refresh(); });
