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
  // Verticais publicados no automático que esperam você postar à mão nas outras redes (mais antigos primeiro).
  const manual = videos.filter(v => v.awaitingManual)
    .sort((a, b) => (a.manualPendingSinceUtc || '').localeCompare(b.manualPendingSinceUtc || ''));
  return { pending, done, unknown, failed, reauth, next, awaitingTime, manual, lastActivity, issues: data.issues || [] };
}

async function refresh() {
  try {
    const [health, queue, issues, accounts, schedule, telegram, notifications, production, radar] = await Promise.all([
      api('/api/health'), api('/api/queue'), api('/api/import-issues'), api('/api/accounts'),
      api('/api/settings/schedule'), api('/api/connect/telegram/status'), api('/api/notifications'),
      api('/api/production'), api('/api/radar'),
    ]);
    state.data = { health, queue, issues, accounts, schedule, telegram, notifications, production, radar };
    if (state.openLogs) {
      await Promise.all(Object.keys(state.openLogs).map(async id => {
        try { state.openLogs[id] = await api(`/api/production/jobs/${id}/logs`); } catch { /* job sumiu */ }
      }));
    }
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
  const retryable = video.state === 'Error' && video.publications.some(p => p.state === 'Failed' && p.attemptCount > 0);
  const toManual = video.state === 'Error' && video.lane !== 'YouTubeHorizontal' && video.publications.some(p => p.state === 'Published');
  return h('div', { class: 'item' },
    h('div', { class: 'row' },
      h('div', {},
        h('div', { class: 'title', text: video.folderName }),
        h('div', { class: 'muted small', text: when })),
      h('div', { class: 'top-actions' },
        horizontal ? pill('YouTube horizontal', 'info') : null,
        pill(label, kind),
        video.awaitingManual ? pill('Pendente manual', 'warn') : null,
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
        retryable
          ? confirmButton('retry:' + video.id, 'Tentar de novo', 'Enviar de novo nas redes que falharam?',
            () => act(() => api(`/api/videos/${video.id}/retry`, 'POST'),
              r => r.publications > 0
                ? `${r.publications} rede(s) vão tentar de novo agora. A pasta volta para 02 - AGENDADOS.`
                : 'Nenhuma rede para tentar de novo (as que falharam estão desmarcadas na Agenda).')) : null,
        toManual
          ? confirmButton('manual:' + video.id, 'Postar manual', 'Postar à mão nas redes que falharam? O vídeo vai para PENDENTE MANUAL.',
            () => act(() => api(`/api/videos/${video.id}/to-manual`, 'POST'), 'O vídeo foi para 07 - PENDENTE MANUAL.')) : null,
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
function confirmButton(key, label, question, onConfirm, kind = 'danger') {
  if (state.confirming !== key) {
    return h('button', {
      class: 'button small ' + kind, type: 'button', text: label,
      onclick: () => { state.confirming = key; render(true); },
    });
  }

  return h('span', { class: 'top-actions' },
    h('span', { class: 'small', text: question }),
    h('button', {
      class: 'button small ' + kind, type: 'button', text: 'Sim',
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
    data.schedule.manualPosting || view.manual.length ? manualCard() : null,
    agendaCard(),
    card('Contas', accounts),
    card('Próximos na fila', view.pending.length
      ? view.pending.slice(0, 3).map(v => videoItem(v))
      : emptyText('Nada na fila. Coloque uma pasta com video.mp4 e legenda.txt em "01 - ENVIAR VERTICAL", ou um vídeo horizontal com legenda.txt e thumbnail em "01 - ENVIAR YOUTUBE".')),
    card('Últimos finalizados', view.done.length ? view.done.slice(0, 3).map(v => videoItem(v)) : emptyText('Nenhum vídeo finalizado ainda.')),
  ];
}

// ---------- envio manual ----------

/** Redes que não estão no automático: é nelas que o vídeo precisa ser postado à mão. */
function manualNetworks() {
  const automatic = new Set(state.data.schedule.platforms || []);
  return ['Instagram', 'Facebook', 'TikTok', 'Threads'].filter(p => !automatic.has(p));
}

function manualCard() {
  const { manual } = state.view;
  const networks = manualNetworks();
  const where = networks.length ? networks.join(', ') : 'as outras redes';
  const items = manual.map(video => h('div', { class: 'item' },
    h('div', { class: 'row' },
      h('div', {},
        h('div', { class: 'title', text: video.folderName }),
        h('div', { class: 'muted small', text: `Saiu no automático ${relative(video.manualPendingSinceUtc)} · pasta 07 - PENDENTE MANUAL` })),
      h('div', { class: 'top-actions' },
        confirmButton('manual:' + video.id, 'Já postei', `Já postou em ${where}?`,
          () => act(() => api(`/api/videos/${video.id}/manual-done`, 'POST'), 'Envio manual confirmado. A pasta vai para 04 - ENVIADOS.'),
          'primary')))));
  return card(`Pendente manual${manual.length ? ` (${manual.length})` : ''}`,
    h('p', { class: 'muted small', text: `Verticais que já saíram no automático e esperam você postar à mão em ${where}. Pegue o vídeo e a legenda na pasta 07 - PENDENTE MANUAL; depois de postar, clique em "Já postei" e a pasta vai para 04 - ENVIADOS.` }),
    manual.length ? items : emptyText('Nada pendente. Quando um vertical sair no automático, ele aparece aqui.'));
}

// ---------- agenda dos próximos dias (gráfico) ----------

const AGENDA_DAYS = 8;
const WEEKDAY_NAMES = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];
const WEEKDAY_SHORT = ['dom', 'seg', 'ter', 'qua', 'qui', 'sex', 'sáb'];

// Quão cheio está o dia: a cor nunca aparece sozinha (há o número "4/5" e o nome na legenda e no detalhe).
const FILL = {
  full: { label: 'Completo', hint: 'todos os horários do dia têm vídeo' },
  most: { label: 'Faltam poucos', hint: 'metade ou mais dos horários têm vídeo' },
  few: { label: 'Faltam muitos', hint: 'menos da metade dos horários têm vídeo' },
  empty: { label: 'Vazio', hint: 'nenhum vídeo marcado' },
  off: { label: 'Sem publicação', hint: 'dia desmarcado na Agenda' },
};

function fillLevel(count, capacity) {
  if (capacity === 0) return 'off';
  if (count >= capacity) return 'full';
  if (count === 0) return 'empty';
  return count / capacity >= 0.5 ? 'most' : 'few';
}

/** Os próximos dias no fuso da agenda: { date: 'AAAA-MM-DD', weekday, label: 'sáb 21/11' }. */
function nextDays(count) {
  const today = localParts(new Date().toISOString()).date;
  const [y, m, d] = today.split('-').map(Number);
  return Array.from({ length: count }, (_, i) => {
    const day = new Date(Date.UTC(y, m - 1, d + i, 12));
    const date = day.toISOString().slice(0, 10);
    const dd = date.slice(8, 10), mm = date.slice(5, 7);
    return { date, weekday: day.getUTCDay(), label: i === 0 ? 'hoje' : i === 1 ? 'amanhã' : WEEKDAY_SHORT[day.getUTCDay()], short: `${dd}/${mm}` };
  });
}

/** Vídeos de uma fila por dia e os horários de cada dia (marcados e livres). */
function laneAgenda(lane, days) {
  const schedule = state.data.schedule;
  const horizontal = lane === 'YouTubeHorizontal';
  const slots = horizontal ? schedule.horizontalSlots || [] : schedule.mode === 'FixedTimes' ? schedule.verticalSlots || [] : [];
  const perDay = horizontal ? schedule.horizontalPerDay : schedule.chosenTimeMaxPerDay;
  const nowParts = localParts(new Date().toISOString());

  const videos = (state.data.queue || [])
    .filter(v => (v.lane || 'Standard') === lane && v.scheduledAtUtc && ['Ready', 'Publishing', 'Published'].includes(v.state))
    .map(v => ({ video: v, ...localParts(v.scheduledAtUtc) }));

  return days.map(day => {
    const enabled = horizontal || (schedule.days || []).includes(WEEKDAY_NAMES[day.weekday]);
    const onDay = videos.filter(v => v.date === day.date).sort((a, b) => a.time.localeCompare(b.time));
    const capacity = enabled ? perDay : 0;
    // Linhas do detalhe: cada horário da lista (com o vídeo dele ou livre) e os vídeos marcados fora da lista.
    const rows = onDay.map(v => ({ time: v.time, video: v.video }));
    if (enabled) {
      for (const slot of slots) {
        if (onDay.some(v => v.time === slot)) continue;
        const past = day.date === nowParts.date && slot <= nowParts.time;
        rows.push({ time: slot, free: true, past });
      }
    }
    rows.sort((a, b) => a.time.localeCompare(b.time));
    const count = onDay.length;
    return { day, count, capacity, level: fillLevel(count, capacity), rows };
  });
}

/** Primeiro horário livre (a partir de agora) da fila, para o resumo. */
function nextFreeSlot(agenda) {
  for (const entry of agenda) {
    if (entry.capacity === 0 || entry.count >= entry.capacity) continue;
    const free = entry.rows.find(r => r.free && !r.past);
    if (free) return `${entry.day.label === 'hoje' || entry.day.label === 'amanhã' ? entry.day.label : entry.day.label + ' ' + entry.day.short} às ${free.time}`;
  }
  return null;
}

const VIDEO_MARK = {
  Published: ['✓', 'publicado'],
  Publishing: ['↑', 'publicando agora'],
  Ready: ['•', 'agendado'],
};

function agendaChart(lane, title, subtitle) {
  const days = nextDays(AGENDA_DAYS);
  const agenda = laneAgenda(lane, days);
  const used = agenda.reduce((sum, e) => sum + Math.min(e.count, e.capacity), 0);
  const total = agenda.reduce((sum, e) => sum + e.capacity, 0);
  const free = nextFreeSlot(agenda);

  const tooltip = h('div', { class: 'agenda-tip', role: 'tooltip', hidden: true });
  const plot = h('div', { class: 'agenda-plot' });
  const chart = h('div', { class: 'agenda-chart' }, plot, tooltip);

  const show = (column, entry) => {
    const name = entry.day.label === 'hoje' || entry.day.label === 'amanhã'
      ? `${entry.day.label[0].toUpperCase()}${entry.day.label.slice(1)}, ${entry.day.short}` : `${entry.day.label} ${entry.day.short}`;
    const head = entry.capacity === 0
      ? h('div', { class: 'tip-head' }, h('strong', { text: name }), h('span', { class: 'muted', text: ' · dia desmarcado na Agenda' }))
      : h('div', { class: 'tip-head' }, h('strong', { text: `${entry.count} de ${entry.capacity}` }), h('span', { text: ` horários · ${name}` }),
        h('div', { class: 'tip-level' }, h('span', { class: 'swatch ' + entry.level }), FILL[entry.level].label));
    const rows = entry.rows.length
      ? entry.rows.map(row => row.free
        ? h('div', { class: 'tip-row free' }, h('span', { class: 'tip-time', text: row.time }), h('span', { text: row.past ? 'passou sem vídeo' : 'livre' }))
        : h('div', { class: 'tip-row' },
          h('span', { class: 'tip-time', text: row.time }),
          h('span', { class: 'tip-mark', title: (VIDEO_MARK[row.video.state] || ['', ''])[1], text: (VIDEO_MARK[row.video.state] || ['•'])[0] }),
          h('span', { class: 'tip-name', text: row.video.folderName })))
      : [h('div', { class: 'tip-row free', text: 'Nenhum vídeo neste dia.' })];
    tooltip.replaceChildren(head, ...rows);
    tooltip.hidden = false;
    // Centraliza o detalhe sobre a coluna, sem sair do gráfico.
    const box = chart.getBoundingClientRect();
    const col = column.getBoundingClientRect();
    const width = tooltip.offsetWidth;
    const left = Math.max(0, Math.min(box.width - width, col.left - box.left + col.width / 2 - width / 2));
    tooltip.style.left = `${left}px`;
  };
  const hide = () => { tooltip.hidden = true; };

  for (const entry of agenda) {
    const percent = entry.capacity ? Math.min(100, Math.round(100 * entry.count / entry.capacity)) : 0;
    const label = entry.capacity === 0 ? '—' : `${entry.count}/${entry.capacity}`;
    // Altura pelo CSSOM: a política de segurança do painel não aceita atributo style.
    const bar = h('span', { class: 'agenda-bar ' + entry.level });
    bar.style.height = `${percent}%`;
    const column = h('button', {
      type: 'button', class: 'agenda-col' + (entry.day.label === 'hoje' ? ' today' : ''),
      'aria-label': `${entry.day.label} ${entry.day.short}: ${entry.capacity ? `${entry.count} de ${entry.capacity} horários, ${FILL[entry.level].label}` : 'dia desmarcado'}`,
    },
      h('span', { class: 'agenda-value', text: label }),
      h('span', { class: 'agenda-track' }, bar),
      h('span', { class: 'agenda-day', text: entry.day.label }),
      h('span', { class: 'agenda-date', text: entry.day.short }));
    column.addEventListener('pointerenter', () => show(column, entry));
    column.addEventListener('focus', () => show(column, entry));
    column.addEventListener('pointerleave', hide);
    column.addEventListener('blur', hide);
    plot.append(column);
  }

  const summary = total
    ? `${used} de ${total} horários ocupados nos próximos ${AGENDA_DAYS} dias${free ? ` · próximo horário livre: ${free}` : ' · todos ocupados'}`
    : 'Nenhum horário de publicação nos próximos dias.';
  return h('section', { class: 'agenda' },
    h('div', { class: 'agenda-head' },
      h('h3', { text: title }),
      h('span', { class: 'muted small', text: subtitle })),
    h('p', { class: 'small', text: summary }),
    chart);
}

function agendaCard() {
  const schedule = state.data.schedule;
  const verticalRule = schedule.mode === 'FixedTimes'
    ? `${(schedule.verticalSlots || []).join(', ')}`
    : schedule.mode === 'Manual' ? `até ${schedule.chosenTimeMaxPerDay} por dia, horários escolhidos por você` : `até ${schedule.chosenTimeMaxPerDay} por dia`;
  const legend = h('div', { class: 'agenda-legend' },
    ['full', 'most', 'few', 'empty'].map(level => h('span', { title: FILL[level].hint }, h('span', { class: 'swatch ' + level }), FILL[level].label)));
  return card('Agenda dos próximos dias',
    h('p', { class: 'muted small', text: 'Cada barra é um dia: a altura mostra quantos horários já têm vídeo. Passe o mouse (ou toque) em um dia para ver os vídeos e os horários livres.' }),
    legend,
    h('div', { class: 'agenda-grid' },
      agendaChart('Standard', 'Verticais', `todas as redes · ${verticalRule}`),
      agendaChart('YouTubeHorizontal', 'Horizontais', `YouTube · ${(schedule.horizontalSlots || []).join(', ')}`)));
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
          platform, connected.has(platform) ? null : h('span', { class: 'muted small', text: '(não conectada)' })))),
        h('p', { class: 'muted small', text: 'Rede desmarcada não recebe mais nada: as publicações que ainda não começaram são canceladas.' })),
      h('fieldset', {}, h('legend', { text: 'Envio manual' }),
        h('label', { class: 'checks' },
          h('input', { type: 'checkbox', name: 'manualPosting', checked: s.manualPosting }),
          'Depois do automático, deixar os verticais em 07 - PENDENTE MANUAL até eu confirmar que postei nas outras redes')),
      h('fieldset', {}, h('legend', { text: 'Produção automática' }),
        h('label', { class: 'checks' },
          h('input', { type: 'checkbox', name: 'autoPublishApproved', checked: s.autoPublishApproved }),
          'Publicar automaticamente o que for APROVADO'),
        h('p', { class: 'muted small', text: 'Desligado: cada vídeo produzido só entra na agenda quando você clica em "Liberar". Ligado: só o que passou em todas as verificações de qualidade e segurança entra sozinho; qualquer alerta, cena sensível, problema de áudio, blur incerto ou revisão pendente bloqueia.' }))),
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
      manualPosting: values.get('manualPosting') === 'on',
      autoPublishApproved: values.get('autoPublishApproved') === 'on',
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

// ---------- produção (gravações do OBS viram vídeos) ----------

const PRODUCTION_STATUS = {
  Queued: ['Na fila', ''],
  Analyzing: ['Analisando', 'info'],
  SelectingHighlights: ['Selecionando momentos', 'info'],
  EditingHorizontal: ['Editando horizontal', 'info'],
  EditingVertical: ['Editando verticais', 'info'],
  GeneratingThumbnails: ['Criando thumbnails', 'info'],
  Rendering: ['Renderizando', 'info'],
  QualityCheck: ['Revisão de qualidade', 'info'],
  Fixing: ['Corrigindo', 'warn'],
  WaitingApproval: ['Aguardando aprovação', 'warn'],
  Approved: ['Aprovado', 'ok'],
  ReadyToPublish: ['Liberado para publicar', 'ok'],
  Publishing: ['Publicando', 'info'],
  Published: ['Publicado', 'ok'],
  Failed: ['Falhou', 'bad'],
  Cancelled: ['Cancelado', ''],
  NeedsHumanReview: ['Precisa de revisão humana', 'warn'],
};

const STEP_STATE = {
  Pending: ['pendente', ''],
  Running: ['rodando', 'info'],
  Done: ['concluída', 'ok'],
  Failed: ['falhou', 'bad'],
  Skipped: ['pulada', ''],
};

const RECORDING_STATE = {
  'Job criado': 'ok',
  Gravando: 'info',
  Estabilizando: 'info',
  Aguardando: 'warn',
  Ignorado: '',
  Ilegível: 'bad',
};

function duration(seconds) {
  const s = Math.round(seconds);
  const hours = Math.floor(s / 3600);
  const minutes = Math.floor((s % 3600) / 60);
  return hours ? `${hours}h${String(minutes).padStart(2, '0')}min` : `${minutes}min${String(s % 60).padStart(2, '0')}s`;
}

function progressBar(percent) {
  const fill = h('div', { class: 'progress-fill' });
  fill.style.width = `${Math.max(0, Math.min(100, percent))}%`; // CSSOM: o CSP bloqueia style inline
  return h('div', { class: 'progress', role: 'progressbar', 'aria-valuenow': String(percent), 'aria-valuemin': '0', 'aria-valuemax': '100' }, fill);
}

function productionJob(job, runningId) {
  const [label, kind] = PRODUCTION_STATUS[job.status] || [job.status, ''];
  const finished = ['Published', 'Cancelled', 'Failed'].includes(job.status);
  const logs = state.openLogs && state.openLogs[job.id];
  const toggleLogs = () => {
    state.openLogs = state.openLogs || {};
    if (state.openLogs[job.id]) { delete state.openLogs[job.id]; render(true); return; }
    api(`/api/production/jobs/${job.id}/logs`).then(list => { state.openLogs[job.id] = list; render(true); })
      .catch(error => notice(error.message, true));
  };
  const call = (action, message) => act(() => api(`/api/production/jobs/${job.id}/${action}`, 'POST'), r => r.message || message);
  const files = state.openFiles && state.openFiles[job.id];
  const toggleFiles = () => {
    state.openFiles = state.openFiles || {};
    if (state.openFiles[job.id]) { delete state.openFiles[job.id]; render(true); return; }
    api(`/api/production/jobs/${job.id}/files`).then(list => { state.openFiles[job.id] = list; render(true); })
      .catch(error => notice(error.message, true));
  };

  return h('div', { class: 'item' },
    h('div', { class: 'row' },
      h('div', {},
        h('div', { class: 'title', text: job.sourceFileName }),
        h('div', { class: 'muted small', text: `${duration(job.durationSeconds)} · ${job.width}x${job.height} · ${Math.round(job.fps)} qps · ${job.sizeGb} GB · ${job.audioTracks} faixa(s) de áudio · recebida ${fmtDateTime(job.receivedAtUtc)}` })),
      h('div', { class: 'top-actions' },
        pill(label, kind),
        job.id === runningId ? pill('Etapa rodando agora', 'info') : null,
        job.paused ? pill('Pausado', 'warn') : null,
        !job.authorized && !finished
          ? confirmButton('authorize:' + job.id, 'Autorizar', 'Autorizar as etapas de produção deste job (só deste arquivo, sem publicar nada)?',
            () => call('authorize'), 'primary') : null,
        job.authorized && !finished && !job.paused ? h('button', { class: 'button small', type: 'button', text: 'Pausar', onclick: () => call('pause') }) : null,
        job.paused && !finished ? h('button', { class: 'button small primary', type: 'button', text: 'Retomar', onclick: () => call('resume') }) : null,
        job.status === 'NeedsHumanReview'
          ? confirmButton('continue:' + job.id, 'Revisei, continuar', 'Você conferiu o motivo da revisão e quer que o job continue?', () => call('continue'), 'primary') : null,
        !finished ? confirmButton('cancel-job:' + job.id, 'Cancelar', 'Cancelar este job? O original e os arquivos já gerados ficam preservados.', () => call('cancel')) : null,
        job.workFolder ? h('button', { class: 'button small', type: 'button', text: files ? 'Fechar entregas' : 'Ver entregas', onclick: toggleFiles }) : null,
        h('button', { class: 'button small', type: 'button', text: logs ? 'Fechar registro' : 'Ver registro', onclick: toggleLogs }))),
    h('div', { class: 'small', text: job.currentStep }),
    job.authorized && !finished && job.progress > 0 ? progressBar(job.progress) : null,
    job.blockedReason ? h('div', { class: 'small warn-text', text: job.blockedReason }) : null,
    job.lastError ? h('div', { class: 'small error-text', text: job.lastError }) : null,
    h('div', { class: 'steps' }, job.steps.map(step => {
      const [stepLabel, stepKind] = STEP_STATE[step.state] || [step.state, ''];
      return h('div', { class: 'step' },
        pill(stepLabel, stepKind),
        h('span', { class: 'small', text: step.name }),
        step.output ? h('span', { class: 'small muted', text: step.output }) : null,
        step.error ? h('span', { class: 'small error-text', text: step.error }) : null);
    })),
    job.workFolder ? h('div', { class: 'small muted', text: `Pasta de trabalho: ${job.workFolder}` }) : null,
    files ? deliverables(job, files) : null,
    logs ? h('div', { class: 'log' }, logs.length ? logs.map(l => h('div', { class: 'log-line ' + (l.level || '').toLowerCase() },
      h('span', { class: 'muted', text: fmtDateTime(l.atUtc) }),
      h('span', { text: ` [${l.step}] ${l.message}` }),
      l.program ? h('span', { class: 'muted', text: ` · ${l.program}` }) : null)) : emptyText('Nada registrado.')) : null);
}

const DELIVERY_FOLDER = {
  '2 - horizontal': 'Vídeo horizontal',
  '3 - verticais': 'Verticais',
  '4 - thumbnails': 'Thumbnails',
  '6 - relatorio': 'Relatório e validação',
};

/** Entregas do job agrupadas por pasta; "anterior" (versão substituída) aparece marcada para comparar. */
function deliverables(job, files) {
  if (!files.length) return h('div', { class: 'small muted', text: 'Nenhuma entrega ainda.' });
  const url = f => `/api/production/jobs/${job.id}/file?path=${encodeURIComponent(f.path)}`;
  const groups = {};
  for (const f of files) {
    const top = f.path.split('/')[0];
    (groups[top] = groups[top] || []).push(f);
  }
  return h('div', { class: 'deliverables' }, Object.keys(groups).map(top => h('div', { class: 'delivery-group' },
    h('div', { class: 'title small', text: DELIVERY_FOLDER[top] || top }),
    h('div', { class: 'delivery-grid' }, groups[top].map(f => {
      const name = f.path.slice(top.length + 1);
      const tag = name.startsWith('anterior/') ? pill('Anterior', '') : name.startsWith('conceitos/') ? pill('Conceito', 'info') : null;
      const media = f.kind === 'video'
        ? h('video', { controls: true, preload: 'metadata', src: url(f), class: 'delivery-media' })
        : f.kind === 'image'
          ? h('a', { href: url(f), target: '_blank', rel: 'noopener' }, h('img', { src: url(f), loading: 'lazy', alt: name, class: 'delivery-media' }))
          : h('a', { href: url(f), target: '_blank', rel: 'noopener', class: 'button small', text: 'Abrir' });
      return h('div', { class: 'delivery' }, media, h('div', { class: 'small' }, tag, h('span', { text: ` ${name}` })),
        h('div', { class: 'small muted', text: `${f.sizeKb >= 1024 ? (f.sizeKb / 1024).toFixed(1) + ' MB' : f.sizeKb + ' KB'}` }));
    })))));
}

function renderProduction() {
  const p = state.data.production;
  if (!p) return [card('Produção', emptyText('Sem resposta da produção.'))];

  const control = p.stopped
    ? h('div', { class: 'row' },
      h('div', { class: 'error-text', text: `Produção parada desde ${fmtDateTime(p.stoppedAtUtc)}: ${p.stopReason || ''}. Nenhuma etapa começa até você liberar.` }),
      confirmButton('release-production', 'Liberar produção', 'Liberar a produção? Os jobs autorizados continuam de onde pararam.',
        () => act(() => api('/api/production/release', 'POST'), r => r.message), 'primary'))
    : h('div', { class: 'row' },
      h('div', { class: 'muted small', text: p.runningJobId ? 'Uma etapa está rodando agora.' : 'Nenhuma etapa rodando agora.' }),
      h('button', {
        class: 'button danger', type: 'button', text: 'PARAR AGORA',
        onclick: () => act(() => api('/api/production/stop', 'POST', { reason: 'Parada de emergência pelo painel' }), r => r.message),
      }));

  const disk = p.disk.configured
    ? `Disco de trabalho: ${p.disk.workFolder} · ${p.disk.freeGb === null ? 'indisponível' : p.disk.freeGb + ' GB livres'} · ${p.disk.reserveGb} GB ficam sempre livres`
    : 'Disco de trabalho não configurado (Bast:ProductionWorkFolder): os jobs ficam esperando.';

  const recordings = p.recordings || [];
  return [
    card('Produção',
      h('p', { class: 'muted small', text: `O OBS grava em ${p.recordingsFolder}. Quando uma gravação termina (o OBS larga o arquivo, ele para de mudar por 2 min, abre do começo ao fim e tem mais de 5 min), ela vira um job. Nada roda sem a sua autorização e o original nunca é movido nem alterado.` }),
      h('p', { class: 'small', text: disk }),
      control),
    card('Gravações na pasta', recordings.length
      ? recordings.map(r => h('div', { class: 'item' },
        h('div', { class: 'row' },
          h('div', {}, h('div', { class: 'title', text: r.fileName }), h('div', { class: 'muted small', text: r.detail })),
          pill(r.state, RECORDING_STATE[r.state] || ''))))
      : emptyText('Nenhum arquivo na pasta de gravações.')),
    card('Jobs de produção', p.jobs.length ? p.jobs.map(job => productionJob(job, p.runningJobId)) : emptyText('Nenhuma gravação recebida ainda.')),
  ];
}

// ---------- Radar de Conteúdo ----------

const RUN_STATUS = { Running: ['Pesquisando', 'info'], Completed: ['Concluída', 'ok'], Partial: ['Parcial', 'warn'], Failed: ['Falhou', 'bad'] };
const SUGGESTION_STATUS = { Received: ['Nova', 'info'], Saved: ['Salva', 'ok'], Ignored: ['Ignorada', ''], InProduction: ['Em produção', 'ok'] };
const OPPORTUNITY_KIND = { alta: 'ok', 'média': 'warn', media: 'warn', baixa: '' };
const PLAN_LABEL = {
  ideia: 'Ideia central', objetivo: 'Objetivo do vídeo', gancho: 'Gancho inicial', roteiro: 'Roteiro', cenas: 'Sequência de cenas',
  gravar: 'O que gravar', gameplay: 'O que mostrar na gameplay', falas: 'Falas / narração', textos_tela: 'Textos na tela',
  edicao: 'Sugestão de edição', titulo: 'Título', thumbnail: 'Thumbnail', legenda: 'Legenda e descrição', hashtags: 'Hashtags', duracao: 'Duração',
};
const ANALYSIS_LABEL = {
  o_que_acontece: 'O que está acontecendo', por_que_atencao: 'Por que está chamando atenção', interessante: 'O que torna a ideia interessante',
  por_que_agora: 'Por que produzir agora', concorrencia: 'Concorrência', versao_bast: 'Versão original para o Bast',
};

const num = n => (n === null || n === undefined) ? 'não disponível' : Number(n).toLocaleString('pt-BR');

function valueBlock(value) {
  if (Array.isArray(value)) return h('ul', { class: 'plan-list' }, value.map(v => h('li', { text: typeof v === 'string' ? v : JSON.stringify(v) })));
  if (value && typeof value === 'object') return h('div', { class: 'small', text: JSON.stringify(value) });
  return h('div', { class: 'small', text: value === null || value === undefined ? '' : String(value) });
}

function labeledBlocks(obj, labels) {
  if (!obj) return [];
  const keys = [...Object.keys(labels).filter(k => k in obj), ...Object.keys(obj).filter(k => !(k in labels))];
  return keys.map(k => h('div', { class: 'plan-item' }, h('div', { class: 'small title', text: labels[k] || k.replace(/_/g, ' ') }), valueBlock(obj[k])));
}

function sourceLine(src) {
  const metrics = src.platform === 'YouTube' || src.views !== undefined
    ? ` · ${num(src.views)} views · ${num(src.likes)} curtidas · ${num(src.comments)} comentários · ${src.viewsPerHour ? num(Math.round(src.viewsPerHour)) + ' views/h' : 'views/h não disponível'}`
    : '';
  return h('li', {},
    h('a', { href: src.url, target: '_blank', rel: 'noopener noreferrer', text: src.title || src.url }),
    h('span', { class: 'small muted', text: `${src.platform ? ' · ' + src.platform : ''}${src.channel ? ' · ' + src.channel : ''}${metrics}${src.publishedAtUtc ? ' · publicado ' + fmtDateTime(src.publishedAtUtc) : ''}${src.consultedAtUtc ? ' · consultado ' + fmtDateTime(src.consultedAtUtc) : ''}` }));
}

function suggestionCard(s) {
  const open = state.openSuggestion === s.id || state.openSuggestion === s.shortId;
  const [statusLabel, statusKind] = SUGGESTION_STATUS[s.status] || [s.status, ''];
  const call = (action, msg) => act(() => api(`/api/radar/suggestions/${s.id}/${action}`, 'POST'), r => r.message || msg);
  return h('div', { class: 'item radar-card' + (open ? ' open' : ''), id: 'sugestao-' + s.shortId },
    h('div', { class: 'row' },
      h('div', {},
        h('div', { class: 'title', text: `${s.format === 'Horizontal' ? 'H' : 'V'}${s.rank}. ${s.title}` }),
        h('div', { class: 'top-actions' }, pill(s.category, 'info'), pill(s.tagName, ''), pill(s.market, ''),
          pill('Oportunidade ' + (s.opportunity || '?'), OPPORTUNITY_KIND[(s.opportunity || '').toLowerCase()] || ''), pill(statusLabel, statusKind))),
      h('div', { class: 'top-actions' },
        h('button', { class: 'button small', type: 'button', text: open ? 'Fechar' : 'Ver plano', onclick: () => { state.openSuggestion = open ? null : s.id; render(true); } }),
        s.status === 'Received' ? h('button', { class: 'button small', type: 'button', text: 'Salvar', onclick: () => call('save') }) : null,
        s.status === 'Received' ? h('button', { class: 'button small', type: 'button', text: 'Ignorar', onclick: () => call('ignore') }) : null,
        s.status !== 'InProduction'
          ? confirmButton('start:' + s.id, 'Iniciar produção', 'Criar a produção no Estúdio de Ideias, com pasta e roteiro?', () => call('start'), 'primary')
          : h('button', { class: 'button small', type: 'button', text: 'Ver no Estúdio', onclick: () => selectTab('studio') }))),
    s.analysis && (s.analysis.por_que_agora || s.analysis.o_que_acontece)
      ? h('div', { class: 'small muted', text: s.analysis.por_que_agora || s.analysis.o_que_acontece }) : null,
    open ? h('div', { class: 'radar-detail' },
      h('div', { class: 'title small', text: 'Análise da oportunidade' }), ...labeledBlocks(s.analysis, ANALYSIS_LABEL),
      h('div', { class: 'title small', text: 'Plano de produção' }), ...labeledBlocks(s.plan, PLAN_LABEL),
      h('div', { class: 'title small', text: 'Fontes' }), h('ul', { class: 'plan-list' }, (s.sources || []).map(sourceLine)),
      h('div', { class: 'small muted', text: `Descoberta em ${fmtDateTime(s.discoveredAtUtc)}` })) : null);
}

function radarFilters(list) {
  const f = state.radarFilter || {};
  const options = (key, label) => {
    const values = [...new Set(list.map(s => s[key]).filter(Boolean))].sort();
    return h('label', { class: 'field' }, h('span', { text: label }),
      h('select', { onchange: e => { state.radarFilter = { ...f, [key]: e.target.value }; render(true); } },
        h('option', { value: '', text: 'Todos', selected: !f[key] }), values.map(v => h('option', { value: v, text: v, selected: f[key] === v }))));
  };
  return h('div', { class: 'fields radar-filters' }, options('tagName', 'Tag'), options('category', 'Categoria'), options('market', 'Mercado'));
}

function renderRadar() {
  const r = state.data.radar;
  if (!r) return [card('Radar de Conteúdo', emptyText('Sem resposta do Radar.'))];
  const viewing = state.radarRun;
  const list = viewing ? viewing.suggestions : r.suggestions;
  const f = state.radarFilter || {};
  const shown = list.filter(s => (!f.tagName || s.tagName === f.tagName) && (!f.category || s.category === f.category) && (!f.market || s.market === f.market));
  const latest = viewing || r.latest;
  const [runLabel, runKind] = latest ? (RUN_STATUS[latest.status] || [latest.status, '']) : ['Sem pesquisa ainda', 'warn'];
  const lastFailed = r.runs[0] && r.runs[0].status === 'Failed' ? r.runs[0] : null;

  const settingsForm = h('form', { class: 'row', onsubmit: e => {
    e.preventDefault();
    const v = new FormData(e.target);
    act(() => api('/api/radar/settings', 'PUT', { dailyTime: v.get('dailyTime'), youTubeSearchBudget: Number(v.get('budget')) }), r2 => r2.message);
  } },
    h('label', { class: 'field' }, h('span', { text: 'Horário diário' }), h('input', { type: 'time', name: 'dailyTime', value: r.settings.dailyTime })),
    h('label', { class: 'field' }, h('span', { text: 'Buscas no YouTube por dia' }), h('input', { type: 'number', name: 'budget', min: '0', max: '60', value: String(r.settings.youTubeSearchBudget) })),
    h('button', { class: 'button small', type: 'submit', text: 'Salvar' }));

  return [
    card('Radar de Conteúdo',
      h('div', { class: 'row' },
        h('div', {},
          h('div', { class: 'top-actions' }, pill(runLabel, runKind), r.youtubeConnected ? pill('YouTube conectado', 'ok') : pill('YouTube desconectado', 'bad')),
          h('div', { class: 'small', text: latest ? `${viewing ? 'Vendo a pesquisa de' : 'Última pesquisa:'} ${fmtDateTime(latest.startedAtUtc)}` : 'Nenhuma pesquisa feita ainda.' }),
          h('div', { class: 'small muted', text: `Pesquisa diária às ${r.settings.dailyTime} (${r.settings.timeZone}) · ${r.due.reason} · ${r.due.searchesLeft} busca(s) no YouTube restantes hoje` })),
        h('div', { class: 'top-actions' },
          viewing ? h('button', { class: 'button small', type: 'button', text: 'Voltar à de hoje', onclick: () => { state.radarRun = null; render(true); } }) : null,
          h('button', { class: 'button primary small', type: 'button', text: 'Atualizar pesquisa agora', onclick: () => act(() => api('/api/radar/refresh', 'POST'), x => x.message) }))),
      lastFailed && !viewing ? h('div', { class: 'small error-text', text: `A pesquisa de ${fmtDateTime(lastFailed.startedAtUtc)} falhou: ${lastFailed.error || ''}. As sugestões abaixo são da última pesquisa concluída.` }) : null,
      latest && latest.summary ? h('p', { class: 'small', text: latest.summary }) : null,
      latest && latest.shortfall ? h('p', { class: 'small warn-text', text: 'Menos oportunidades reais que o esperado: ' + latest.shortfall }) : null,
      h('p', { class: 'small muted', text: 'A pesquisa é feita pela tarefa agendada do Claude (app do Claude aberto e PC ligado): primeiro criadores e fontes internacionais, depois o Brasil. Números só da API oficial do YouTube; o que ela não informa aparece como "não disponível".' }),
      settingsForm),
    list.length ? card('Filtros', radarFilters(list)) : null,
    card('Horizontais (YouTube)', shown.filter(s => s.format === 'Horizontal').length
      ? shown.filter(s => s.format === 'Horizontal').map(suggestionCard) : emptyText('Nenhuma sugestão horizontal.')),
    card('Verticais (Shorts, Reels, TikTok)', shown.filter(s => s.format === 'Vertical').length
      ? shown.filter(s => s.format === 'Vertical').map(suggestionCard) : emptyText('Nenhuma sugestão vertical.')),
    r.saved.length ? card('Sugestões salvas', r.saved.map(suggestionCard)) : null,
    tagsCard(r),
    card('Histórico de pesquisas', r.runs.length ? r.runs.map(run => {
      const [label, kind] = RUN_STATUS[run.status] || [run.status, ''];
      return h('div', { class: 'item' }, h('div', { class: 'row' },
        h('div', {}, h('div', { class: 'small', text: `${fmtDateTime(run.startedAtUtc)} · ${run.trigger}` }),
          run.error ? h('div', { class: 'small error-text', text: run.error }) : null,
          run.shortfall ? h('div', { class: 'small muted', text: run.shortfall }) : null),
        h('div', { class: 'top-actions' }, pill(label, kind),
          run.status !== 'Failed' && run.status !== 'Running'
            ? h('button', { class: 'button small', type: 'button', text: 'Ver sugestões', onclick: () => api(`/api/radar/runs/${run.id}`).then(x => { state.radarRun = x; render(true); window.scrollTo(0, 0); }) })
            : null)));
    }) : emptyText('Nenhuma pesquisa ainda.')),
  ].filter(Boolean);
}

function tagsCard(r) {
  const editing = state.editingTag;
  const form = (tag) => h('form', { class: 'tag-form', onsubmit: e => {
    e.preventDefault();
    const v = new FormData(e.target);
    const body = { name: v.get('name'), synonyms: v.get('synonyms'), includeTerms: v.get('include'), excludeTerms: v.get('exclude'), active: v.get('active') === 'on' };
    act(() => api(tag ? `/api/radar/tags/${tag.id}` : '/api/radar/tags', tag ? 'PUT' : 'POST', body), x => { state.editingTag = null; return x.message; });
  } },
    h('div', { class: 'fields' },
      h('label', { class: 'field' }, h('span', { text: 'Nome' }), h('input', { name: 'name', required: true, value: tag ? tag.name : '' })),
      h('label', { class: 'field' }, h('span', { text: 'Sinônimos (separe com ;)' }), h('input', { name: 'synonyms', value: tag ? tag.synonyms : '' })),
      h('label', { class: 'field' }, h('span', { text: 'Incluir termos' }), h('input', { name: 'include', value: tag ? tag.includeTerms : '' })),
      h('label', { class: 'field' }, h('span', { text: 'Excluir termos' }), h('input', { name: 'exclude', value: tag ? tag.excludeTerms : '' }))),
    h('label', { class: 'checks' }, h('input', { type: 'checkbox', name: 'active', checked: tag ? tag.active : true }), 'Ativa (gera pesquisa diária própria)'),
    h('div', { class: 'form-actions' }, h('button', { class: 'button primary small', type: 'submit', text: 'Salvar tag' }),
      h('button', { class: 'button small', type: 'button', text: 'Cancelar', onclick: () => { state.editingTag = null; render(true); } })));

  return card('Tags do Radar',
    h('p', { class: 'small muted', text: 'Cada tag ativa tem a própria pesquisa diária. Sinônimos evitam pesquisas duplicadas (ex.: GTA VI = GTA 6 = Grand Theft Auto VI). Jogos sem tag ativa não são pesquisados.' }),
    r.tags.map(t => editing === t.id ? form(t) : h('div', { class: 'item' }, h('div', { class: 'row' },
      h('div', {}, h('div', { class: 'title', text: t.name }), h('div', { class: 'small muted', text: `${t.names.join(' · ')} · ${t.suggestions} sugestão(ões) no histórico` }),
        t.excludeTerms ? h('div', { class: 'small muted', text: 'Exclui: ' + t.excludeTerms }) : null),
      h('div', { class: 'top-actions' }, pill(t.active ? 'Ativa' : 'Inativa', t.active ? 'ok' : ''),
        h('button', { class: 'button small', type: 'button', text: 'Ver sugestões', onclick: () => { state.radarFilter = { tagName: t.name }; window.scrollTo(0, 0); render(true); } }),
        h('button', { class: 'button small', type: 'button', text: 'Editar', onclick: () => { state.editingTag = t.id; render(true); } }),
        confirmButton('deltag:' + t.id, 'Excluir', `Excluir a tag ${t.name}? O histórico de sugestões continua.`,
          () => act(() => api(`/api/radar/tags/${t.id}`, 'DELETE'), x => x.message)))))),
    editing === 'new' ? form(null) : h('button', { class: 'button small', type: 'button', text: 'Nova tag', onclick: () => { state.editingTag = 'new'; render(true); } }));
}

// ---------- Estúdio de Ideias ----------

const STUDIO_STATUS = {
  SuggestionReceived: ['Sugestão recebida', ''], Saved: ['Salva', ''], AwaitingRecording: ['Aguardando gravação', 'warn'],
  RecordingDetected: ['Gravação detectada', 'info'], AnalyzingFiles: ['Analisando arquivos', 'info'], Editing: ['Editando', 'info'],
  Rendering: ['Renderizando', 'info'], Validating: ['Validando', 'info'], AwaitingUserReview: ['Aguardando sua análise', 'warn'],
  Approved: ['Aprovado', 'ok'], ReleasedForPublishing: ['Liberado para publicação', 'ok'], Published: ['Publicado', 'ok'],
  Error: ['Erro', 'bad'], NeedsReview: ['Precisa de revisão', 'warn'],
};

function renderStudio() {
  const r = state.data.radar;
  if (!r) return [card('Estúdio de Ideias', emptyText('Sem resposta.'))];
  if (!r.studio.enabled) return [card('Estúdio de Ideias', emptyText('A pasta do Estúdio não está configurada (Bast:StudioFolder).'))];
  const item = p => {
    const [label, kind] = STUDIO_STATUS[p.status] || [p.status, ''];
    const call = (action) => act(() => api(`/api/studio/${p.id}/${action}`, 'POST'), x => x.message);
    return h('div', { class: 'item' },
      h('div', { class: 'row' },
        h('div', {}, h('div', { class: 'title', text: `${p.code} · ${p.title}` }),
          h('div', { class: 'small muted', text: `${p.tagName} · ${p.format === 'Horizontal' ? 'Horizontal YouTube' : 'Vertical (BAST_VERTICAL_V1)'} · criada ${fmtDateTime(p.createdAtUtc)}` })),
        h('div', { class: 'top-actions' }, pill(label, kind),
          ['AwaitingRecording', 'RecordingDetected'].includes(p.status) && !p.recordingFinishedAtUtc
            ? confirmButton('finished:' + p.id, 'Terminei de gravar', 'Marcar que terminou de gravar? A análise começa quando os arquivos pararem de mudar.', () => call('finished'), 'primary') : null,
          p.status === 'Approved'
            ? confirmButton('release:' + p.id, 'Liberar publicação', 'Liberar este vídeo para publicação?', () => call('release'), 'primary') : null)),
      p.statusDetail ? h('div', { class: 'small', text: p.statusDetail }) : null,
      p.missingScenes ? h('div', { class: 'small warn-text', text: 'Cenas faltando: ' + p.missingScenes }) : null,
      h('div', { class: 'small muted', text: `Pasta: ${p.folder}` }),
      p.files && p.files.length ? h('ul', { class: 'plan-list' }, p.files.map(f => h('li', { class: 'small',
        text: `${f.arquivo} · ${f.mb} MB${f.duracao_s ? ' · ' + duration(f.duracao_s) : ''}${f.resolucao ? ' · ' + f.resolucao : ''}` }))) : null);
  };
  const groups = [
    ['Aguardando gravação', ['AwaitingRecording', 'RecordingDetected']],
    ['Em produção', ['AnalyzingFiles', 'Editing', 'Rendering', 'Validating']],
    ['Aguardando sua aprovação', ['AwaitingUserReview', 'Approved']],
    ['Revisão e erros', ['NeedsReview', 'Error']],
    ['Liberados e publicados', ['ReleasedForPublishing', 'Published']],
  ];
  return [
    card('Estúdio de Ideias',
      h('p', { class: 'small', text: `Grave as cenas do roteiro e coloque os arquivos na pasta da produção, em ${r.studio.folder}. Quando terminar, clique em "Terminei de gravar" (ou crie PRONTO.txt na pasta). A pasta é reconhecida pelo código no roteiro.json, mesmo se você renomeá-la.` }),
      h('p', { class: 'small muted', text: 'Fluxo separado das lives. Nenhum vídeo do Estúdio é publicado sem você clicar em "Liberar publicação" nele; a publicação automática da Agenda não vale aqui.' })),
    ...groups.map(([title, statuses]) => {
      const list = r.productions.filter(p => statuses.includes(p.status));
      return list.length ? card(title, list.map(item)) : null;
    }).filter(Boolean),
    r.productions.length ? null : card('Produções', emptyText('Nenhuma produção ainda. Escolha uma sugestão no Radar e clique em "Iniciar produção".')),
  ].filter(Boolean);
}

const RENDERERS = {
  overview: renderOverview,
  radar: renderRadar,
  studio: renderStudio,
  production: renderProduction,
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
  // Radar: sugestões novas da última pesquisa; Estúdio: produções que esperam você (gravar, aprovar, revisar).
  const radarData = data.radar;
  const newSuggestions = radarData ? radarData.suggestions.filter(s => s.status === 'Received').length : 0;
  const radarCount = document.getElementById('count-radar');
  radarCount.textContent = newSuggestions ? String(newSuggestions) : '';
  radarCount.className = 'count' + (radarData && radarData.runs[0] && radarData.runs[0].status === 'Failed' ? ' alert' : '');
  const studioWaiting = radarData ? radarData.productions.filter(p => ['AwaitingUserReview', 'NeedsReview', 'Error', 'Approved'].includes(p.status)).length : 0;
  const studioCount = document.getElementById('count-studio');
  studioCount.textContent = studioWaiting ? String(studioWaiting) : '';
  studioCount.className = 'count' + (studioWaiting ? ' alert' : '');

  // Produção: jobs que esperam você (autorizar, revisar, aprovar) ou que estão parados por algum motivo.
  const productionJobs = (data.production && data.production.jobs) || [];
  const waitingYou = productionJobs.filter(j => (!j.authorized && !['Cancelled', 'Failed', 'Published'].includes(j.status))
    || ['NeedsHumanReview', 'WaitingApproval', 'Failed'].includes(j.status) || j.blockedReason).length;
  const productionCount = document.getElementById('count-production');
  productionCount.textContent = waitingYou ? String(waitingYou) : '';
  productionCount.className = 'count' + (waitingYou || (data.production && data.production.stopped) ? ' alert' : '');

  const problems = view.issues.length + view.failed.length + view.unknown.length + view.reauth.length;
  const problemCount = document.getElementById('count-problems');
  problemCount.textContent = problems ? String(problems) : '';
  problemCount.className = 'count' + (problems ? ' alert' : '');

  for (const tab of document.querySelectorAll('.tab')) tab.classList.toggle('active', tab.dataset.tab === state.tab);
  for (const panel of document.querySelectorAll('.panel')) panel.hidden = panel.id !== 'tab-' + state.tab;

  // Não redesenha o formulário da agenda enquanto o usuário está editando.
  const pickingTime = document.activeElement && document.activeElement.closest && document.activeElement.closest('.time-picker');
  // Nem enquanto o detalhe de um dia do gráfico está aberto (mouse em cima ou foco).
  const readingChart = document.querySelector('.agenda-chart:hover, .agenda-col:focus');
  // Nem enquanto um vídeo de entrega toca ou o mouse está sobre as entregas (redesenhar reiniciaria o vídeo).
  const watching = document.querySelector('.deliverables:hover')
    || [...document.querySelectorAll('.deliverables video')].some(v => !v.paused)
    // Nem enquanto o usuário digita nos formulários do Radar (tags, horário).
    || (document.activeElement && document.activeElement.closest && document.activeElement.closest('#tab-radar form'));
  if ((state.tab !== 'schedule' || !state.scheduleDirty || force) && (!pickingTime || force) && (!readingChart || force) && (!watching || force)) {
    const panel = document.getElementById('tab-' + state.tab);
    const scroll = window.scrollY;
    panel.replaceChildren(...RENDERERS[state.tab]().filter(Boolean));
    window.scrollTo(0, scroll);
  }

  document.getElementById('updated').textContent = `Atualizado às ${fmtTime(new Date().toISOString())}`;
}

function selectTab(name) {
  // Links do Telegram: #radar/<id curto> abre o Radar com a sugestão aberta.
  const [tab, sub] = String(name || '').split('/');
  state.tab = RENDERERS[tab] ? tab : 'overview';
  if (state.tab === 'radar' && sub) state.openSuggestion = sub;
  if (location.hash.slice(1) !== name) history.replaceState(null, '', '#' + (sub && state.tab === 'radar' ? `radar/${sub}` : state.tab));
  render(true);
  if (sub && state.tab === 'radar') setTimeout(() => { const el = document.getElementById('sugestao-' + sub); if (el) el.scrollIntoView({ block: 'start' }); }, 50);
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

{
  const [tab, sub] = location.hash.slice(1).split('/');
  state.tab = RENDERERS[tab] ? tab : 'overview';
  if (state.tab === 'radar' && sub) state.openSuggestion = sub;
}
refresh().then(() => {
  const sub = location.hash.slice(1).split('/')[1];
  if (sub && state.tab === 'radar') { const el = document.getElementById('sugestao-' + sub); if (el) el.scrollIntoView({ block: 'start' }); }
});
setInterval(() => { if (!document.hidden) refresh(); }, REFRESH_MS);
document.addEventListener('visibilitychange', () => { if (!document.hidden) refresh(); });
