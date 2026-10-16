import { authorizeCreation } from './server-auth.js';
'use strict';
import { rememberRoom } from './recent-rooms.js';

const $ = (id) => document.getElementById(id);
const SVGNS = 'http://www.w3.org/2000/svg';
function icon(id, cls) {
  const s = document.createElementNS(SVGNS, 'svg'); s.setAttribute('viewBox', '0 0 24 24'); s.setAttribute('aria-hidden', 'true'); s.setAttribute('class', 'ic' + (cls ? ' ' + cls : ''));
  const u = document.createElementNS(SVGNS, 'use'); u.setAttribute('href', '#' + id); s.appendChild(u); return s;
}
const enc = new TextEncoder();
const dec = new TextDecoder();
const params = new URLSearchParams(location.search);
const invited = location.hash.length > 1;
const storage = {
  get(key) { try { return localStorage.getItem(key); } catch { return null; } },
  set(key, value) { try { localStorage.setItem(key, value); } catch {} },
};
const forcePlain = params.get('plain') === '1';
let forceRelay = params.get('relay') === '1';
try { if (localStorage.getItem('audio.transport') === 'relay') forceRelay = true; } catch (e) {}
const debug = params.get('debug') === '1';
const desktopApp = !!window.chrome?.webview;
if (desktopApp) $('appsBtn').hidden = true;

function b64urlEnc(b) { return btoa(String.fromCharCode(...b)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, ''); }
function b64urlDec(s) { s = s.replace(/-/g, '+').replace(/_/g, '/'); return Uint8Array.from(atob(s), (c) => c.charCodeAt(0)); }
function b64enc(b) { return btoa(String.fromCharCode(...b)); }
function b64dec(s) { return Uint8Array.from(atob(s), (c) => c.charCodeAt(0)); }
function base32(bytes) {
  const A = 'abcdefghijklmnopqrstuvwxyz234567'; let out = '', bits = 0, val = 0;
  for (const b of bytes) { val = (val << 8) | b; bits += 8; while (bits >= 5) { out += A[(val >>> (bits - 5)) & 31]; bits -= 5; } }
  if (bits > 0) out += A[(val << (5 - bits)) & 31];
  return out;
}

let K = null, badLink = false;
(function initKey() {
  const h = location.hash.replace(/^#/, '');
  if (h.length > 0) {
    try { const d = b64urlDec(h); if (d.length === 16) K = d; } catch {}
    if (!K || K.length < 16) badLink = true;
  }
  if (!K || K.length < 16) K = crypto.getRandomValues(new Uint8Array(16));
})();

async function hkdf(keyBytes, info, len) {
  const base = await crypto.subtle.importKey('raw', keyBytes, 'HKDF', false, ['deriveBits']);
  const bits = await crypto.subtle.deriveBits({ name: 'HKDF', hash: 'SHA-256', salt: new Uint8Array(0), info: enc.encode(info) }, base, len * 8);
  return new Uint8Array(bits);
}

const hasTransform = 'RTCRtpScriptTransform' in window;
const hasLegacy = window.RTCRtpSender && 'createEncodedStreams' in RTCRtpSender.prototype;
const e2eeOK = !forcePlain && !!K && (hasTransform || hasLegacy);
let worker = null, chatKey = null;

async function initCrypto() {
  if (K) chatKey = await crypto.subtle.importKey('raw', await hkdf(K, '\x7a\x76\x6f\x6e\x6f\x6b/chat/v1', 16), 'AES-GCM', false, ['encrypt', 'decrypt']);
  if (!e2eeOK) return;
  worker = new Worker(new URL('e2ee-worker.js', import.meta.url), { type: 'module', name: 'e2ee' });
  worker.postMessage({ type: 'key', key: await hkdf(K, '\x7a\x76\x6f\x6e\x6f\x6b/media/v1', 16) });
}

function attachTransform(endpoint, op) {
  if (!e2eeOK || !worker) return;
  try {
    if (hasTransform) endpoint.transform = new RTCRtpScriptTransform(worker, { op });
    else { const { readable, writable } = endpoint.createEncodedStreams(); worker.postMessage({ type: 'stream', op, readable, writable }, [readable, writable]); }
  } catch (e) { throw new Error('Не удалось включить шифрование медиа', { cause: e }); }
}

let ws = null, roomId = '', selfId = '', myName = '', isHost = false, roomLocked = false, joined = false, left = false;
let pub = null, sub = null;
let localStream = null, micTrack = null, camTrack = null, previewStream = null;
let micTr = null, camTr = null, screenTr = null, screenTrack = null, screenStream = null, screenAudioTr = null;
let micOn = true, camOn = true;
let iceServers = [];
let reconnectAttempt = 0, reconnectTimer = 0;
const peers = new Map();
const trackMap = new Map();
const pendIce = { pub: [], sub: [] };
let startedAt = 0, timerIv = 0;
let unread = 0, chatOpen = false;
let joining = false, joinTimer = 0, previewBusy = false, previewRequest = 0;
const dbgCands = [];

const pcConfig = () => {
  const c = { bundlePolicy: 'max-bundle', rtcpMuxPolicy: 'require', iceServers };
  if (forceRelay) c.iceTransportPolicy = 'relay';
  if (e2eeOK && !hasTransform) c.encodedInsertableStreams = true;
  return c;
};

const devicePickers = ['camSel', 'micSel'].map(id => {
  const select = $(id), button = $(id + 'Button'), list = $(id + 'List');
  let active = 0, search = '', searchAt = 0;
  const close = () => {
    list.hidden = true; button.setAttribute('aria-expanded', 'false');
    button.removeAttribute('aria-activedescendant'); search = '';
  };
  const highlight = index => {
    active = Math.max(0, Math.min(index, list.children.length - 1));
    [...list.children].forEach((option, i) => option.classList.toggle('active', i === active));
    const option = list.children[active];
    if (option) { button.setAttribute('aria-activedescendant', option.id); option.scrollIntoView({ block: 'nearest' }); }
  };
  const sync = () => {
    button.firstElementChild.textContent = select.selectedOptions[0]?.textContent || 'По умолчанию';
    list.replaceChildren();
    [...select.options].forEach((option, index) => {
      const item = document.createElement('div');
      item.id = `${id}Option${index}`; item.className = 'device-option'; item.setAttribute('role', 'option');
      item.setAttribute('aria-selected', String(option.selected));
      item.innerHTML = '<span></span><svg viewBox="0 0 24 24" aria-hidden="true" class="ic"><use href="#i-check"/></svg>';
      item.firstElementChild.textContent = option.textContent;
      item.onpointerdown = event => event.preventDefault();
      item.onclick = () => choose(index);
      list.appendChild(item);
    });
    if (!list.hidden) highlight(select.selectedIndex);
  };
  const open = () => {
    if (button.disabled) return;
    devicePickers.forEach(picker => picker.close());
    list.hidden = false; button.setAttribute('aria-expanded', 'true');
    highlight(select.selectedIndex);
  };
  const choose = index => {
    const changed = select.selectedIndex !== index;
    select.selectedIndex = index; close(); sync(); button.focus();
    if (changed) select.dispatchEvent(new Event('change', { bubbles: true }));
  };
  button.onclick = () => list.hidden ? open() : close();
  button.onkeydown = event => {
    const key = event.key;
    if (key === 'Escape' || key === 'Tab') { close(); if (key === 'Escape') event.preventDefault(); return; }
    if ((key === 'Enter' || key === ' ') && !list.hidden) { event.preventDefault(); choose(active); return; }
    if (['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(key)) {
      event.preventDefault();
      const wasClosed = list.hidden;
      if (wasClosed) open();
      if (key === 'Home') highlight(0);
      else if (key === 'End') highlight(list.children.length - 1);
      else if (!wasClosed) highlight(active + (key === 'ArrowDown' ? 1 : -1));
    } else if (key.length === 1 && key !== ' ' && !event.ctrlKey && !event.metaKey && !event.altKey) {
      event.preventDefault(); if (list.hidden) open();
      search = Date.now() - searchAt > 700 ? key : search + key; searchAt = Date.now();
      const index = [...select.options].findIndex(option => option.textContent.toLocaleLowerCase().startsWith(search.toLocaleLowerCase()));
      if (index >= 0) highlight(index);
    }
  };
  button.onblur = close;
  document.addEventListener('pointerdown', event => { if (!button.parentElement.contains(event.target)) close(); });
  sync();
  return { close, sync, button };
});
$('deviceSettings').addEventListener('toggle', () => { if (!$('deviceSettings').open) devicePickers.forEach(picker => picker.close()); });

async function listDevices() {
  let devs = []; try { devs = await navigator.mediaDevices.enumerateDevices(); } catch {}
  const fill = (sel, kind, label) => {
    const cur = sel.value; sel.replaceChildren(new Option('По умолчанию', ''));
    devs.filter((d) => d.kind === kind).forEach((d, i) => {
      const o = document.createElement('option'); o.value = d.deviceId; o.textContent = d.label || `${label} ${i + 1}`; sel.appendChild(o);
    });

    if (cur && [...sel.options].some(o => o.value === cur)) sel.value = cur;
  };
  fill($('camSel'), 'videoinput', 'Камера'); fill($('micSel'), 'audioinput', 'Микрофон');
  devicePickers.forEach(picker => picker.sync());
}

function setPreviewState(state, title, description = '') {
  const preview = $('preview').parentElement;
  preview.dataset.state = state;
  $('previewLabel').textContent = title;
  $('deviceHelp').textContent = description;
  $('deviceHelp').hidden = !description;
  const ready = state === 'ready';
  if (!ready) devicePickers.forEach(picker => picker.close());
  $('previewOff').hidden = ready && camOn && !!previewStream?.getVideoTracks().length;
  $('previewControls').hidden = !ready;
  $('deviceSettings').hidden = !ready;
  $('enableDevices').hidden = ready;
  $('permissionOptions').hidden = ready;
  $('enableDevices').disabled = state === 'pending';
  $('enableDevices').firstChild.textContent = state === 'pending' ? 'Ждём вашего решения… ' : state === 'error' ? 'Попробовать снова ' : 'Разрешить доступ ';
  $('audioOnly').hidden = state === 'pending';
  $('skipDevices').textContent = state === 'pending' ? 'Продолжить без устройств' : 'Без устройств';
  $('join').disabled = joining || previewBusy || !ready;
}

const previewTrackDevices = new WeakMap();
let pendingPreviewChange = null;
const livePreviewTrack = kind => previewStream?.getTracks().find(track => track.kind === kind && track.readyState === 'live');
function previewStatus(message = '') {
  $('deviceStatusText').textContent = message;
  $('deviceStatus').hidden = !message;
  $('cancelDevice').hidden = !previewBusy || $('preview').parentElement.dataset.state !== 'ready';
}
function syncPreviewControls() {
  for (const id of ['pjMic', 'pjCam', 'camSel', 'micSel', 'camSelButton', 'micSelButton']) $(id).disabled = previewBusy || joining;
  $('previewControls').setAttribute('aria-busy', String(previewBusy));
  $('join').disabled = previewBusy || joining || $('preview').parentElement.dataset.state !== 'ready';
  setToggle($('pjMic'), micOn); setToggle($('pjCam'), camOn);
}
function renderReadyPreview() {
  setPreviewState('ready', 'Камера выключена', 'Вы можете включить её в любой момент.');
  syncPreviewControls();
}
function watchPreviewTrack(track, deviceId) {
  previewTrackDevices.set(track, deviceId);
  track.addEventListener('ended', () => {
    if (joined) {
      if (track !== micTrack && track !== camTrack) return;
      const audio = track.kind === 'audio', source = audio ? 'mic' : 'cam';
      if (audio) micOn = false; else camOn = false;
      setCtrl($(source), false); send({ type: 'mute', source, muted: true });
      const me = peers.get(selfId);
      if (me) { me.mutes[source] = true; relabel(me); renderVideoTile(me, 'cam'); renderPeople(); }
      banner(audio ? 'Микрофон отключён. Подключите его и включите снова.' : 'Камера отключена. Подключите её и включите снова.');
      return;
    }
    if (!previewStream?.getTracks().includes(track)) return;
    if (track.kind === 'audio') micOn = false; else camOn = false;
    renderReadyPreview();
    previewStatus(track.kind === 'audio' ? 'Микрофон отключён. Проверьте устройство и включите его снова.' : 'Камера отключена. Проверьте устройство и включите её снова.');
  });
}
function deviceConstraints(kind) {
  const id = $(kind === 'video' ? 'camSel' : 'micSel').value;
  return kind === 'video'
    ? { deviceId: id ? { exact: id } : undefined, width: { ideal: 1280 }, height: { ideal: 720 }, frameRate: { ideal: 30 } }
    : { deviceId: id ? { exact: id } : undefined, echoCancellation: true, noiseSuppression: navigator.mediaDevices.getSupportedConstraints().noiseSuppression ? { exact: noiseFilter } : undefined, autoGainControl: false, channelCount: 1 };
}

async function updatePreviewDevice(kind, changeDevice = false) {
  if (previewBusy || joining) return;
  const isVideo = kind === 'video';
  const enabled = isVideo ? camOn : micOn;
  const select = $(isVideo ? 'camSel' : 'micSel');
  const oldTrack = livePreviewTrack(kind);
  if (changeDevice && !enabled) { previewStatus(); return; }
  const nextEnabled = changeDevice || !enabled;
  if (!nextEnabled || (oldTrack && previewTrackDevices.get(oldTrack) === select.value)) {
    if (oldTrack) oldTrack.enabled = nextEnabled;
    if (isVideo) camOn = nextEnabled; else micOn = nextEnabled;
    previewStatus(); renderReadyPreview(); return;
  }
  const request = ++previewRequest;
  previewBusy = true; pendingPreviewChange = changeDevice ? { select, previous: oldTrack ? previewTrackDevices.get(oldTrack) || '' : '' } : null; syncPreviewControls();
  previewStatus(isVideo ? 'Подключаем камеру…' : 'Подключаем микрофон…');
  let stream;
  try {
    stream = await navigator.mediaDevices.getUserMedia({ [kind]: deviceConstraints(kind) });
    if (request !== previewRequest || joined) { stopStream(stream); return; }
    const track = stream.getTracks().find(track => track.kind === kind && track.readyState === 'live');
    if (!track) throw new Error('Device returned no track');
    watchPreviewTrack(track, select.value);
    previewStream.getTracks().filter(track => track.kind === kind).forEach(track => { previewStream.removeTrack(track); track.stop(); });
    previewStream.addTrack(track);
    if (isVideo) { camOn = true; $('preview').play().catch(() => {}); }
    else { micOn = true; meter(previewStream); }
    previewStatus(); listDevices();
  } catch (error) {
    stopStream(stream);
    if (request !== previewRequest) return;
    if (changeDevice && oldTrack) { select.value = previewTrackDevices.get(oldTrack) || ''; devicePickers.forEach(picker => picker.sync()); }
    const denied = ['NotAllowedError', 'SecurityError'].includes(error.name);
    previewStatus(denied
      ? (isVideo ? 'Разрешите камеру в настройках сайта и нажмите её кнопку снова.' : 'Разрешите микрофон в настройках сайта и нажмите его кнопку снова.')
      : (isVideo ? 'Не удалось включить камеру. Проверьте устройство и попробуйте снова.' : 'Не удалось включить микрофон. Проверьте устройство и попробуйте снова.'));
  } finally {
    if (request === previewRequest) { previewBusy = false; pendingPreviewChange = null; $('cancelDevice').hidden = true; renderReadyPreview(); }
  }
}

async function openPreview() {
  if (previewBusy) return;
  const request = ++previewRequest;
  previewBusy = true;
  setPreviewState('pending', 'Разрешите доступ в браузере', 'Нажмите «Разрешить» в запросе камеры и микрофона рядом с адресной строкой.');
  $('pjError').hidden = true; previewStatus();
  stopStream(previewStream); previewStream = null;
  $('preview').srcObject = null;
  cancelAnimationFrame(meterRAF);
  $('meter').firstElementChild.style.transform = 'scaleX(0)';
  const audio = micOn ? deviceConstraints('audio') : false;
  const video = camOn ? deviceConstraints('video') : false;
  let stream = null;
  try {
    if (!audio && !video) stream = new MediaStream();
    else {
      try { stream = await navigator.mediaDevices.getUserMedia({ audio, video }); }
      catch (e) {
        if (request !== previewRequest) return;

        if (!audio || !video || !['NotFoundError', 'OverconstrainedError', 'NotReadableError'].includes(e.name)) throw e;
        stream = await navigator.mediaDevices.getUserMedia({ audio, video: false });
        if (request === previewRequest) { camOn = false; setToggle($('pjCam'), false); }
      }
    }

    if (request !== previewRequest) { stopStream(stream); return; }
    previewStream = stream;
    micOn = !!livePreviewTrack('audio'); camOn = !!livePreviewTrack('video');
    previewStream.getTracks().forEach(track => watchPreviewTrack(track, $(track.kind === 'video' ? 'camSel' : 'micSel').value));
    $('preview').srcObject = previewStream;
    renderReadyPreview();
    if (meterCtx?.state === 'suspended') meterCtx.resume().catch(() => {});
    listDevices(); meter(previewStream);
  } catch (e) {
    if (request !== previewRequest) { stopStream(stream); return; }
    const denied = ['NotAllowedError', 'SecurityError'].includes(e.name);
    setPreviewState('error', denied ? 'Доступ пока закрыт' : 'Устройство недоступно', denied ? (desktopApp ? 'Проверьте доступ к камере и микрофону через меню «Доступ к устройствам».' : 'Разрешите камеру и микрофон в настройках сайта рядом с адресной строкой.') : 'Проверьте подключение устройств или продолжите только с микрофоном.');
    showPjError(denied ? 'После изменения разрешений нажмите «Попробовать снова». Можно также продолжить без устройств.' : 'Не удалось подключить камеру или микрофон. Проверьте, не заняты ли они другим приложением, и попробуйте снова.');
  } finally {
    if (request === previewRequest) { previewBusy = false; $('enableDevices').disabled = false; syncPreviewControls(); }
  }
}

function skipDevices() {
  previewRequest++; previewBusy = false;
  stopStream(previewStream); previewStream = new MediaStream();
  $('preview').srcObject = previewStream;
  micOn = camOn = false;
  setToggle($('pjMic'), false); setToggle($('pjCam'), false);
  cancelAnimationFrame(meterRAF); $('meter').firstElementChild.style.transform = 'scaleX(0)';
  $('pjError').hidden = true;
  previewStatus(); renderReadyPreview();
  $('join').focus();
}

let meterCtx = null, meterRAF = 0, meterSource = null;
function meter(stream) {
  cancelAnimationFrame(meterRAF); meterSource?.disconnect(); meterSource = null;
  $('meter').firstElementChild.style.transform = 'scaleX(0)';
  if (!stream) return;
  const at = stream.getAudioTracks()[0]; if (!at) return;
  try {
    meterCtx = meterCtx || new (window.AudioContext || window.webkitAudioContext)();
    const an = meterCtx.createAnalyser(); an.fftSize = 512;
    meterSource = meterCtx.createMediaStreamSource(new MediaStream([at])); meterSource.connect(an);
    const data = new Uint8Array(an.frequencyBinCount); const bar = $('meter').firstElementChild;
    const tick = () => { an.getByteFrequencyData(data); let s = 0; for (const v of data) s += v; bar.style.transform = `scaleX(${at.enabled && at.readyState === 'live' ? Math.min(1, (s / data.length) * .015) : 0})`; meterRAF = requestAnimationFrame(tick); };
    tick();
  } catch {}
}

function stopStream(s) { if (s) s.getTracks().forEach((t) => t.stop()); }
function setToggle(btn, on) { btn.classList.toggle('on', on); btn.setAttribute('aria-pressed', String(on)); }
function showPjError(msg) { const e = $('pjError'); e.textContent = msg; e.hidden = false; }

$('cancelDevice').onclick = () => {
  previewRequest++; previewBusy = false;
  if (pendingPreviewChange) { pendingPreviewChange.select.value = pendingPreviewChange.previous; devicePickers.forEach(picker => picker.sync()); }
  pendingPreviewChange = null; previewStatus(); renderReadyPreview();
};
$('pjMic').onclick = () => updatePreviewDevice('audio');
$('pjCam').onclick = () => updatePreviewDevice('video');
$('camSel').onchange = () => updatePreviewDevice('video', true);
$('micSel').onchange = () => updatePreviewDevice('audio', true);
$('name').value = storage.get('audio.name') || storage.get('\x7a\x76\x6f\x6e\x6f\x6b.name') || '';
$('joinForm').onsubmit = (e) => { e.preventDefault(); join(); };
$('enableDevices').onclick = openPreview;
$('skipDevices').onclick = skipDevices;
$('audioOnly').onclick = () => { if (previewBusy) return; camOn = false; micOn = true; setToggle($('pjCam'), false); setToggle($('pjMic'), true); openPreview(); };
$('connMode').value = forceRelay ? 'relay' : 'auto';
$('connMode').onchange = (e) => { forceRelay = e.target.value === 'relay'; try { localStorage.setItem('audio.transport', forceRelay ? 'relay' : 'auto'); } catch (err) {} };
document.querySelector('.skip-link').onclick = (e) => { e.preventDefault(); (document.documentElement.classList.contains('room') ? $('name') : $('createRoom')).focus(); };
$('name').oninput = () => { $('previewName').textContent = $('name').value.trim() || 'Вы'; };
$('name').oninput();
let noticeTimer;
new MutationObserver(() => { clearTimeout(noticeTimer); if ($('notice').textContent) noticeTimer = setTimeout(() => { $('notice').textContent = ''; }, 5000); }).observe($('notice'), { childList: true });
const defaultJoinLabel = 'Войти в звонок';
$('joinLabel').textContent = defaultJoinLabel;
function showPreparation(focus = false) {
  document.documentElement.classList.add('room');
  $('landingIntro').hidden = true; $('landingActions').hidden = true;
  $('appsBtn').hidden = true;
  $('preparation').hidden = false; $('entryMain').classList.add('room-entry');
  $('joinTitle').textContent = 'Перед звонком';
  $('joinDescription').textContent = 'Проверьте имя, камеру и звук. Вас ещё не видно и не слышно.';

  document.querySelector('.skip-link').textContent = 'К подготовке звонка';
  document.title = 'Подготовка к звонку — audio.lgbt';
  if (focus) { window.scrollTo(0, 0); $('joinTitle').focus({ preventScroll: true }); }
  maybeAutoPreview();
}

async function maybeAutoPreview() {
  if (previewStream || previewBusy || !navigator.permissions || !navigator.permissions.query) return;
  const q = async (name) => { try { const s = await navigator.permissions.query({ name }); return !!s && s.state === 'granted'; } catch (e) { return false; } };
  const camG = await q('camera'), micG = await q('microphone');
  if ((!camG && !micG) || previewStream || previewBusy) return;
  camOn = camG; micOn = micG;
  setToggle($('pjCam'), camG); setToggle($('pjMic'), micG);
  openPreview();
}
$('createRoom').onclick = async () => {
  $('createRoom').disabled = true;
  try {
    if (!await authorizeCreation()) return;
    history.pushState(null, '', '#' + b64urlEnc(K));
    showPreparation(true);
  } catch (error) { $('inviteError').textContent = error.message; $('inviteError').hidden = false; }
  finally { $('createRoom').disabled = false; }
};
if (invited && !badLink) showPreparation();
else document.querySelector('.skip-link').textContent = 'Создать звонок';

window.addEventListener('popstate', () => location.reload());
window.addEventListener('hashchange', () => location.reload());
function applyTheme(theme) {
  document.documentElement.dataset.theme = theme;
  $('callTheme').value = theme;
  $('themeToggle').setAttribute('aria-label', theme === 'dark' ? 'Включить светлую тему' : 'Включить тёмную тему');
  document.querySelector('meta[name="theme-color"]').content = theme === 'dark' ? '#101010' : '#f8f8f8';
}
try { applyTheme(localStorage.getItem('audio.theme') === 'light' ? 'light' : 'dark'); } catch {}
$('themeToggle').onclick = () => {
  const theme = document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark';
  applyTheme(theme); try { localStorage.setItem('audio.theme', theme); } catch {}
};
$('inviteForm').onsubmit = (e) => {
  e.preventDefault(); $('inviteError').hidden = true;
  try {
    const url = new URL($('inviteUrl').value.trim());
    const key = b64urlDec(url.hash.slice(1));
    if (url.origin !== location.origin || key.length !== 16) throw new Error();
    location.assign(url.href);

    if (url.pathname === location.pathname && url.search === location.search) location.reload();
  } catch {
    $('inviteError').textContent = 'Вставьте полную ссылку на звонок с этого сайта, включая часть после #.';
    $('inviteError').hidden = false;
  }
};
function setJoining(active) {
  joining = active; $('join').disabled = active || $('preview').parentElement.dataset.state !== 'ready';
  $('join').setAttribute('aria-busy', String(active));
  $('joinLabel').textContent = active ? 'Подключаемся…' : defaultJoinLabel;
  $('enableDevices').disabled = active; syncPreviewControls();
  if (active) devicePickers.forEach(picker => picker.close());
}
function failJoin(message) {
  clearTimeout(joinTimer);
  if (ws) { ws.onclose = null; ws.close(); ws = null; }
  if (worker) { worker.terminate(); worker = null; }
  if (localStream) { previewStream = localStream; localStream = null; }
  setJoining(false); showPjError(message);
}

function send(m) {
  if (!ws || ws.readyState !== WebSocket.OPEN) return false;
  ws.send(JSON.stringify(m)); return true;
}

async function join() {
  if (joining || previewBusy) return;
  if (!e2eeOK && !forcePlain) { showPjError('Этот браузер не поддерживает шифрование звонка. Откройте ссылку в актуальном Chrome, Edge или Firefox.'); return; }
  if (!previewStream) { await openPreview(); return; }
  setJoining(true); $('pjError').hidden = true;
  try {
    myName = ($('name').value || '').trim() || 'Гость';
    storage.set('audio.name', myName);
    localStream = previewStream; previewStream = null;
    micTrack = localStream.getAudioTracks()[0] || null;
    camTrack = localStream.getVideoTracks()[0] || null;
    if (micTrack) micTrack.enabled = micOn;
    if (camTrack) camTrack.enabled = camOn;
    if (worker) worker.terminate();
    await initCrypto();
    roomId = base32(await hkdf(K, '\x7a\x76\x6f\x6e\x6f\x6b/room/v1', 16)).slice(0, 26);
    joinTimer = setTimeout(() => failJoin('Сервер не отвечает. Проверьте подключение и попробуйте снова.'), 12000);
    connect();
  } catch (e) { failJoin('Не удалось войти в звонок. Обновите страницу и попробуйте снова.'); }
}

function connect() {
  const proto = location.protocol === 'https:' ? 'wss' : 'ws';
  ws = new WebSocket(`${proto}://${location.host}/ws`);
  ws.onopen = () => send({ type: 'join', room: roomId, name: myName });
  const socket = ws;
  ws.onmessage = async (ev) => {
    if (socket !== ws || left) return;
    try { await onSignal(JSON.parse(ev.data)); }
    catch (error) {
      console.error('Сбой соединения', error);
      if (left || socket !== ws) return;
      if (joined) endCall('Не удалось установить защищённое соединение');
      else failJoin('Не удалось установить соединение. Попробуйте ещё раз.');
    }
  };
  ws.onclose = () => { if (!left) { if (!joined) failJoin('Не удалось подключиться к серверу звонков. Попробуйте ещё раз.'); else scheduleReconnect(); } };
  ws.onerror = () => {};
}

function scheduleReconnect() {
  if (left || reconnectTimer) return;
  banner('Соединение потеряно. Переподключаемся… Сообщения пока не отправляются.');
  $('connChip').hidden = true;
  const delay = Math.min(7000, [0, 500, 1500, 3000, 5000, 7000][reconnectAttempt] ?? 7000);
  reconnectAttempt++;
  reconnectTimer = setTimeout(() => { reconnectTimer = 0; teardownPeers(); connect(); }, delay);
}

function teardownPeers() {
  try { pub && pub.close(); } catch {} try { sub && sub.close(); } catch {}
  pub = sub = null; screenTr = screenAudioTr = null;
  for (const id of [...peers.keys()]) removePeer(id);
  selfId = '';
  trackMap.clear(); pendIce.pub = []; pendIce.sub = [];
}

async function onSignal(m) {
  switch (m.type) {
    case 'welcome': return onWelcome(m);
    case 'publish-answer': await pub.setRemoteDescription({ type: 'answer', sdp: m.sdp }); flushIce('pub'); break;
    case 'subscribe-offer': return onSubscribeOffer(m);
    case 'ice': return onIce(m);
    case 'peer-joined': if (m.peer) { addPeer(m.peer); renderPeople(); } break;
    case 'peer-left': removePeer(m.peerId); renderPeople(); break;
    case 'peer-updated': { const p = peers.get(m.peer && m.peer.id); if (p) { p.name = m.peer.name; relabel(p); renderPeople(); } break; }
    case 'mute': onPeerMute(m); break;
    case 'mute-request': if (m.source === 'mic') muteMic(); sysChat('Организатор выключил микрофоны'); break;
    case 'speaking': { const p = peers.get(m.peerId); if (p) setSpeaking(p, !!m.speaking); break; }
    case 'chat': onChat(m); break;
    case 'host': onHostChange(m.peerId); break;
    case 'locked': roomLocked = !!m.locked; $('lockState').hidden = !roomLocked; { const t = $('lockRoom').querySelector('.t'); if (t) t.textContent = roomLocked ? 'Открыть комнату' : 'Закрыть комнату'; } break;
    case 'removed': left = true; endCall('Вас удалили из звонка'); break;
    case 'ended': left = true; endCall('Организатор завершил звонок'); break;
    case 'error':
      m.error = ({ 'room is full': 'В комнате больше нет свободных мест.', 'room is locked': 'Организатор закрыл вход. Попросите его открыть комнату.', 'server is at capacity': 'Сервер занят. Попробуйте немного позже.', 'unknown room': 'Комната уже закрыта. Попробуйте войти ещё раз.', 'a valid token is required to create a room': 'Организатор ещё не открыл комнату. Если вы организатор, войдите в аккаунт и подключитесь снова.' })[m.error] || m.error;

      if (joined) { left = true; endCall(m.error || 'Соединение отклонено'); }
      else failJoin(m.error || 'Не удалось войти в комнату. Попробуйте позже.');
      break;
  }
}

async function onWelcome(m) {
  clearTimeout(joinTimer); joining = false;
  selfId = m.selfId; iceServers = m.iceServers || []; isHost = !!m.host;
  roomLocked = !!m.locked; $('lockState').hidden = !roomLocked;
  reconnectAttempt = 0;
  if (!joined) {
    joined = true; showCall(); refreshCallDevices();
    rememberRoom(b64urlEnc(K), isHost);
    cancelAnimationFrame(meterRAF);
    startedAt = Date.now(); timerIv = setInterval(updateTimer, 1000); startSpeakingDetection();
  }
  clearBanner();
  ensureSelfTile();
  (m.peers || []).forEach(addPeer);
  applyHostUI(); renderPeople(); updateCount();
  if (!e2eeOK) { const chip = $('e2eeChip'); chip.classList.remove('tone-live'); chip.lastChild.textContent = ' Без шифрования'; }
  ensureSub();
  await publish();
  send({ type: 'mute', source: 'mic', muted: !micOn });
  send({ type: 'mute', source: 'cam', muted: !camOn });

  if (screenTrack && screenTrack.readyState === 'live') {
    await addScreenToPub();
    if (screenTrack && !left && peers.has(selfId)) {
      const vt = ensureVideoTile(peers.get(selfId), 'screen');
      vt.video.srcObject = new MediaStream([screenTrack]); vt.hasVideo = true; renderVideoTile(peers.get(selfId), 'screen'); refreshShareTile();
    }
  }
}

function ensureSub() {
  if (sub) return;
  sub = new RTCPeerConnection(pcConfig());
  sub.onicecandidate = (e) => { if (e.candidate) sendIce('sub', e.candidate); };
  sub.ontrack = onRemoteTrack;
  sub.onconnectionstatechange = () => watchState(sub);
}

async function publish() {
  pub = new RTCPeerConnection(pcConfig());
  pub.onicecandidate = (e) => { if (e.candidate) sendIce('pub', e.candidate); };
  pub.onconnectionstatechange = () => watchState(pub);
  micTr = camTr = null;
  if (micTrack) { micPipeline = micPipeline || await makeMicPipeline(micTrack); micTr = pub.addTransceiver(micPipeline.track, { direction: 'sendonly', streams: [localStream] }); attachTransform(micTr.sender, 'encrypt'); }
  if (camTrack) { camTr = pub.addTransceiver(camTrack, { direction: 'sendonly', streams: [localStream] }); attachTransform(camTr.sender, 'encrypt'); }
  if (micTrack || camTrack) await republish();
}

function pubLabels() {
  const out = [];
  if (micTr && micTr.mid) out.push({ mid: micTr.mid, source: 'mic' });
  if (camTr && camTr.mid) out.push({ mid: camTr.mid, source: 'cam' });
  if (screenTr && screenTr.mid) out.push({ mid: screenTr.mid, source: 'screen' });
  if (screenAudioTr && screenAudioTr.mid) out.push({ mid: screenAudioTr.mid, source: 'screen-audio' });
  return out;
}

let publishQueue = Promise.resolve();
function republish() {
  const connection = pub;
  const task = publishQueue.catch(() => {}).then(async () => {
    if (!connection || connection !== pub || connection.signalingState === 'closed') return;
    const offer = await connection.createOffer(); await connection.setLocalDescription(offer);

    await new Promise((resolve, reject) => {
      const cleanup = () => { clearTimeout(timeout); connection.removeEventListener('signalingstatechange', changed); };
      const changed = () => {
        if (connection.signalingState === 'stable') { cleanup(); resolve(); }
        else if (connection.signalingState === 'closed') { cleanup(); reject(new Error('Connection closed')); }
      };
      const timeout = setTimeout(() => { cleanup(); reject(new Error('Publish answer timed out')); }, 10000);
      connection.addEventListener('signalingstatechange', changed);
      send({ type: 'publish-offer', sdp: connection.localDescription.sdp, tracks: pubLabels() });
    });
  });
  publishQueue = task;
  return task;
}

async function onSubscribeOffer(m) {
  ensureSub();
  trackMap.clear();
  (m.tracks || []).forEach((t) => trackMap.set(t.mid, t));
  await sub.setRemoteDescription({ type: 'offer', sdp: m.sdp });
  flushIce('sub');
  const answer = await sub.createAnswer(); await sub.setLocalDescription(answer);
  send({ type: 'subscribe-answer', sdp: sub.localDescription.sdp });
  reconcileVideoTiles();
}

function onRemoteTrack(e) {
  attachTransform(e.receiver, 'decrypt');
  const info = trackMap.get(e.transceiver.mid); if (!info) return;
  const peer = peers.get(info.peerId); if (!peer) return;
  const stream = new MediaStream([e.track]);
  if (info.kind === 'video') { const vt = ensureVideoTile(peer, info.source); vt.video.srcObject = stream; vt.video.play().catch(() => {}); vt.hasVideo = true; renderVideoTile(peer, info.source); }
  else if (info.source === 'screen-audio') { peer.screenAudioEl.srcObject = stream; peer.screenAudioEl.play().catch(() => {}); applyScreenVolume(peer); updateScreenAudioControl(peer); }
  else { peer.gainSrc?.disconnect(); peer.gain?.disconnect(); peer.gain = peer.gainSrc = null; peer.audioEl.srcObject = stream; peer.audioEl.play().catch(() => {}); applyPeerVolume(peer); }
}

function sendIce(target, c) { send({ type: 'ice', target, candidate: { candidate: c.candidate, sdpMid: c.sdpMid, sdpMLineIndex: c.sdpMLineIndex, usernameFragment: c.usernameFragment } }); }
async function onIce(m) {
  const pc = m.target === 'pub' ? pub : sub;
  if (!pc || !m.candidate || !m.candidate.candidate) return;
  if (debug) dbgCands.push(m.target + ' ' + m.candidate.candidate);
  if (!pc.remoteDescription) { pendIce[m.target].push(m.candidate); return; }
  try { await pc.addIceCandidate(m.candidate); } catch (e) { console.warn('addIce', e); }
}
async function flushIce(target) { const pc = target === 'pub' ? pub : sub; const q = pendIce[target]; pendIce[target] = []; for (const c of q) { try { await pc.addIceCandidate(c); } catch {} } }

function watchState(pc) {
  const s = pc.connectionState;
  if (s === 'connected') clearBanner();
  else if (s === 'disconnected') banner('Нестабильное соединение…');
  else if (s === 'failed') { banner('Переподключение…'); try { pc.restartIce(); } catch {} }
}

if (!navigator.mediaDevices?.getDisplayMedia) $('share').hidden = true;
else $('share').hidden = false;
$('share').onclick = openShareSettings;
let shareBusy = false, shareEpoch = 0;
let shareConfig = { height: 1080, fps: 30, content: 'detail', audio: true };
function shareSelection() { return { height: Number($('shareQuality').value), fps: Number($('shareFps').value), content: $('shareContent').value, audio: $('shareAudio').checked }; }
function shareError(text) { $('shareError').textContent = text; $('shareError').hidden = !text; }
function openShareSettings() {
  if (!pub || left) return;
  $('shareQuality').value = String(shareConfig.height); $('shareFps').value = String(shareConfig.fps);
  $('shareContent').value = shareConfig.content; $('shareAudio').checked = shareConfig.audio;
  $('shareStart').textContent = screenTrack ? 'Применить' : 'Выбрать источник';
  $('shareStop').hidden = $('shareSource').hidden = !screenTrack;
  shareError(''); if (!$('shareDialog').open) $('shareDialog').showModal();
}
function closeShareSettings() { $('shareDialog').close(); $('share').focus(); }
$('shareClose').onclick = closeShareSettings;
$('shareSource').onclick = () => startShare(shareSelection());
$('shareStop').onclick = () => { stopShare(); closeShareSettings(); };
$('shareForm').onsubmit = e => { e.preventDefault(); const config = shareSelection(); if (!screenTrack || (config.audio && !screenStream?.getAudioTracks().some(t => t.readyState === 'live'))) startShare(config); else updateShare(config); };
function setShareBusy(busy) { shareBusy = busy; for (const id of ['shareStart', 'shareSource', 'shareQuality', 'shareFps', 'shareContent', 'shareAudio']) $(id).disabled = busy; }
function screenConstraints(config) { return { width: { ideal: Math.round(config.height * 16 / 9), max: Math.round(config.height * 16 / 9) }, height: { ideal: config.height, max: config.height }, frameRate: { ideal: config.fps, max: config.fps } }; }
async function tuneScreenSender(config) {
  if (!screenTr?.sender.track) return;
  const params = screenTr.sender.getParameters(); if (!params.encodings?.length) return;
  const rates = { 720: 3000000, 1080: 6000000, 1440: 10000000, 2160: 18000000 };
  params.encodings[0].maxBitrate = Math.round(rates[config.height] * (config.fps === 60 ? 1.6 : 1));
  params.encodings[0].maxFramerate = config.fps;
  params.degradationPreference = config.content === 'motion' ? 'maintain-framerate' : 'maintain-resolution';
  await screenTr.sender.setParameters(params);
  if (screenAudioTr?.sender.track) {
    const audio = screenAudioTr.sender.getParameters();
    if (audio.encodings?.length) { audio.encodings[0].maxBitrate = 192000; await screenAudioTr.sender.setParameters(audio); }
  }
}
async function startShare(config) {
  if (shareBusy || !pub || left) return;
  const connection = pub, epoch = ++shareEpoch;
  setShareBusy(true); shareError(''); let ds;
  try {

    ds = await navigator.mediaDevices.getDisplayMedia({
      video: screenConstraints(config),
      audio: config.audio ? { echoCancellation: false, noiseSuppression: false, autoGainControl: false, channelCount: 2, restrictOwnAudio: true } : false,
      systemAudio: config.audio ? 'include' : 'exclude', windowAudio: 'window', selfBrowserSurface: 'exclude', surfaceSwitching: 'include',
    });
    if (left || epoch !== shareEpoch || connection !== pub || connection.signalingState === 'closed') { stopStream(ds); return; }
    const track = ds.getVideoTracks()[0]; if (!track || track.readyState !== 'live') throw new Error('No capture');
    await track.applyConstraints(screenConstraints(config));
    if (left || epoch !== shareEpoch || connection !== pub) { stopStream(ds); return; }

    stopShare(false, false);
    shareConfig = config; screenStream = ds; screenTrack = track;
    track.contentHint = config.content; track.onended = () => { if (screenTrack === track) stopShare(); };
    for (const audio of ds.getAudioTracks()) audio.onended = () => { if (screenStream === ds) { send({ type: 'mute', source: 'screen-audio', muted: true }); refreshShareTile(); } };
    await addScreenToPub();
    if (left || epoch !== shareEpoch || screenStream !== ds) return;
    $('share').classList.add('active'); $('share').setAttribute('aria-pressed', 'true');
    const me = peers.get(selfId);
    if (me) { const vt = ensureVideoTile(me, 'screen'); vt.video.srcObject = new MediaStream([track]); vt.hasVideo = true; renderVideoTile(me, 'screen'); }
    refreshShareTile(); closeShareSettings();
  } catch (error) {
    stopStream(ds); if (screenStream === ds) stopShare();
    if (error.name !== 'NotAllowedError' && !left) shareError('Не удалось начать показ. Выберите источник ещё раз или уменьшите качество.');
  } finally { if (epoch === shareEpoch || !screenTrack) setShareBusy(false); }
}
async function updateShare(config) {
  if (shareBusy || !screenTrack || left) return;
  const track = screenTrack, previous = shareConfig, epoch = shareEpoch;
  setShareBusy(true); shareError('');
  try {
    await track.applyConstraints(screenConstraints(config));
    if (left || track !== screenTrack || epoch !== shareEpoch) return;
    track.contentHint = config.content; await tuneScreenSender(config);
    if (left || track !== screenTrack || epoch !== shareEpoch) return;
    for (const audio of screenStream.getAudioTracks()) audio.enabled = config.audio;
    shareConfig = config;
    send({ type: 'mute', source: 'screen-audio', muted: !hasShareAudio() }); refreshShareTile(); closeShareSettings();
  } catch {
    if (track === screenTrack) { await track.applyConstraints(screenConstraints(previous)).catch(() => {}); await tuneScreenSender(previous).catch(() => {}); track.contentHint = previous.content; }
    if (!left) shareError('Не удалось применить качество. Попробуйте меньшее разрешение или частоту кадров.');
  } finally { setShareBusy(false); }
}
function hasShareAudio() { return !!screenStream?.getAudioTracks().some(t => t.readyState === 'live' && t.enabled); }
async function addScreenToPub() {
  if (!pub || !screenTrack) return;
  const connection = pub, stream = screenStream, epoch = shareEpoch;
  screenTr = pub.addTransceiver(screenTrack, { direction: 'sendonly', streams: [screenStream] }); attachTransform(screenTr.sender, 'encrypt');
  const audio = screenStream.getAudioTracks().find(t => t.readyState === 'live');
  if (audio) { screenAudioTr = pub.addTransceiver(audio, { direction: 'sendonly', streams: [screenStream] }); attachTransform(screenAudioTr.sender, 'encrypt'); }
  await republish();
  if (left || connection !== pub || stream !== screenStream || epoch !== shareEpoch) return;
  await tuneScreenSender(shareConfig);
  if (left || connection !== pub || stream !== screenStream || epoch !== shareEpoch) return;
  send({ type: 'mute', source: 'screen', muted: false }); send({ type: 'mute', source: 'screen-audio', muted: !hasShareAudio() });
}
function stopShare(negotiate = true, invalidate = true) {
  if (invalidate) { shareEpoch++; setShareBusy(false); }
  const active = screenStream; screenTrack = null; screenStream = null;
  stopStream(active);
  for (const tr of [screenTr, screenAudioTr]) if (tr) { try { tr.stop(); } catch {} }
  screenTr = screenAudioTr = null;
  if (active && negotiate && pub && !left) republish().catch(() => { if (!left) banner('Не удалось завершить показ. Переподключитесь к звонку.'); });
  if (active) { send({ type: 'mute', source: 'screen', muted: true }); send({ type: 'mute', source: 'screen-audio', muted: true }); }
  $('share').classList.remove('active'); $('share').setAttribute('aria-pressed', 'false');
  const me = peers.get(selfId); if (me) removeVideoTile(me, 'screen');
}
function refreshShareTile() {
  const me = peers.get(selfId), vt = me?.vtiles.get('screen'); if (!vt) return;
  const settings = screenTrack?.getSettings() || {};
  vt.quality.textContent = `${settings.width || '?'} × ${settings.height || '?'} · ${Math.round(settings.frameRate || shareConfig.fps)} FPS${hasShareAudio() ? ' · со звуком' : ' · без звука'}`;
}

const stage = $('stage');

function mkVideoTile(peer, source, self) {
  const tile = document.createElement('div');
  tile.className = 'tile' + (self ? ' self' : '');
  tile.dataset.peer = peer.id; tile.dataset.source = source;
  const video = document.createElement('video');
  video.autoplay = true; video.playsInline = true; video.muted = true;
  const avatar = document.createElement('div'); avatar.className = 'avatar';
  const label = document.createElement('div'); label.className = 'label';
  const nm = document.createElement('span'); nm.className = 'nm'; label.appendChild(nm);
  tile.append(video, avatar, label);
  let quality = null;
  if (source === 'screen') {
    const actions = document.createElement('div'); actions.className = 'screen-actions';
    const expand = document.createElement('button'); expand.type = 'button'; expand.className = 'screen-expand'; expand.append(icon('i-expand'));
    expand.title = expand.ariaLabel = 'На весь экран'; expand.onclick = () => toggleScreenFullscreen(tile);
    video.ondblclick = () => toggleScreenFullscreen(tile);
    actions.append(expand);
    if (self) { const settings = document.createElement('button'); settings.type = 'button'; settings.title = settings.ariaLabel = 'Настройки демонстрации'; settings.append(icon('i-sliders')); settings.onclick = openShareSettings; actions.append(settings); }
    else {
      const sound = document.createElement('label'); sound.className = 'screen-volume'; sound.hidden = true;
      const caption = document.createElement('span'); caption.textContent = 'Звук';
      const range = document.createElement('input'); range.type = 'range'; range.min = '0'; range.max = '100'; range.value = String(peer.screenVolume * 100); range.ariaLabel = `Громкость демонстрации: ${peer.name}`;
      range.oninput = () => { peer.screenVolume = Number(range.value) / 100; applyScreenVolume(peer); };
      sound.append(caption, range); actions.prepend(sound);
    }
    quality = document.createElement('span'); quality.className = 'screen-quality';
    video.addEventListener('resize', () => { if (!self) quality.textContent = `${video.videoWidth} × ${video.videoHeight}`; else refreshShareTile(); });
    tile.append(actions, quality);
  }
  if (source === 'cam') {
    const resize = () => { tile.dataset.portrait = String(video.videoHeight > video.videoWidth && video.videoWidth > 0); layoutStage(); };
    video.addEventListener('resize', resize); video.addEventListener('loadedmetadata', resize);
  }
  stage.appendChild(tile);
  return { tile, video, avatar, nm, label, quality, hasVideo: false, source };
}

function ensureVideoTile(peer, source) {
  let vt = peer.vtiles.get(source);
  if (!vt) { vt = mkVideoTile(peer, source, peer.self); peer.vtiles.set(source, vt); renderVideoTile(peer, source); if (source === 'screen') updateScreenAudioControl(peer); layoutStage(); }
  return vt;
}
function removeVideoTile(peer, source) {
  const vt = peer.vtiles.get(source); if (!vt) return;
  if (document.fullscreenElement === vt.tile) document.exitFullscreen().catch(() => {});
  if (expandedScreen === vt.tile) closeExpandedScreen();
  vt.video.srcObject = null; vt.tile.remove(); peer.vtiles.delete(source); layoutStage();
}
let expandedScreen = null, screenFocus = null;
async function toggleScreenFullscreen(tile) {
  if (document.fullscreenElement === tile) { await document.exitFullscreen().catch(() => {}); return; }
  if (expandedScreen === tile) { closeExpandedScreen(); return; }
  screenFocus = document.activeElement;
  if (tile.requestFullscreen && document.fullscreenEnabled) {
    try { await tile.requestFullscreen(); tile.querySelector('.screen-expand').focus(); return; } catch {}
  }
  closeExpandedScreen(false); expandedScreen = tile; tile.classList.add('screen-expanded');
  tile.setAttribute('role', 'dialog'); tile.setAttribute('aria-modal', 'true'); tile.setAttribute('aria-label', 'Просмотр демонстрации');
  tile.querySelector('.screen-expand').focus(); syncFullscreenButtons();
}
function closeExpandedScreen(restore = true) {
  if (!expandedScreen) return;
  expandedScreen.classList.remove('screen-expanded'); expandedScreen.removeAttribute('role'); expandedScreen.removeAttribute('aria-modal'); expandedScreen.removeAttribute('aria-label'); expandedScreen = null;
  if (restore && screenFocus?.isConnected) screenFocus.focus(); syncFullscreenButtons();
}
function syncFullscreenButtons() {
  document.querySelectorAll('.screen-expand').forEach(b => { b.title = b.ariaLabel = (document.fullscreenElement === b.closest('.tile') || expandedScreen === b.closest('.tile')) ? 'Выйти из полного экрана' : 'На весь экран'; });
}
document.addEventListener('fullscreenchange', () => { syncFullscreenButtons(); if (!document.fullscreenElement && screenFocus?.isConnected) screenFocus.focus(); });
document.addEventListener('keydown', e => {
  if (!expandedScreen) return;
  if (e.key === 'Escape') { e.preventDefault(); closeExpandedScreen(); }
  if (e.key === 'Tab') {
    const buttons = [...expandedScreen.querySelectorAll('button, input')].filter(el => !el.closest('[hidden]'));
    const first = buttons[0], last = buttons.at(-1);
    if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
    else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
  }
});
function applyScreenVolume(p) { if (p.screenAudioEl) { p.screenAudioEl.muted = !!p.mutes?.['screen-audio']; p.screenAudioEl.volume = (p.screenVolume ?? 1) * speakerLevel; } }
function updateScreenAudioControl(p) {
  const control = p.vtiles.get('screen')?.tile.querySelector('.screen-volume');
  if (control) control.hidden = !p.screenAudioEl?.srcObject?.getAudioTracks().some(t => t.readyState === 'live');
}

function ensureSelfTile() {
  let me = peers.get(selfId);
  if (!me) { me = newPeerObj({ id: selfId, name: myName, host: isHost }, true); peers.set(selfId, me); }
  const vt = ensureVideoTile(me, 'cam'); vt.video.srcObject = localStream; vt.hasVideo = !!camTrack; renderVideoTile(me, 'cam');
}

let playCtx = null;
let outputDevice = '', speakerLevel = 1, micLevel = 1, noiseFilter = true;
let micPipeline = null;
function playbackCtx() {
  try { playCtx = playCtx || new (window.AudioContext || window.webkitAudioContext)({ sinkId: outputDevice || undefined }); if (playCtx.state === 'suspended') playCtx.resume().catch(() => {}); } catch (e) { playCtx = null; }
  return playCtx;
}
let outputTest = null, outputTestEpoch = 0;
function stopOutputTest() {
  outputTestEpoch++;
  if (outputTest) { for (const oscillator of outputTest.oscillators) { try { oscillator.stop(); } catch {} oscillator.disconnect(); } outputTest.gain.disconnect(); outputTest = null; }
  $('testOutput').textContent = 'Проверить звук'; $('testOutput').setAttribute('aria-pressed', 'false');
}
$('testOutput').onclick = async () => {
  if ($('testOutput').getAttribute('aria-pressed') === 'true') { stopOutputTest(); return; }
  stopOutputTest(); const epoch = outputTestEpoch;
  $('testOutput').textContent = 'Остановить проверку'; $('testOutput').setAttribute('aria-pressed', 'true'); settingsError('');
  try {
    const ac = playbackCtx(); if (!ac) throw new Error('Звук недоступен в этом браузере.');
    await ac.resume();
    if (epoch !== outputTestEpoch) return;
    if (ac.setSinkId && ac.sinkId !== outputDevice) await ac.setSinkId(outputDevice);
    if (epoch !== outputTestEpoch) return;
    const gain = ac.createGain(); gain.gain.value = .12 * speakerLevel; gain.connect(ac.destination);
    const sound = { gain, oscillators: [] }; outputTest = sound;
    for (const [i, frequency] of [660, 880, 990].entries()) {
      const oscillator = ac.createOscillator(), envelope = ac.createGain(), start = ac.currentTime + .02 + i * .4;
      oscillator.frequency.value = frequency;
      envelope.gain.setValueAtTime(0, start); envelope.gain.linearRampToValueAtTime(1, start + .015);
      envelope.gain.setValueAtTime(1, start + .24); envelope.gain.linearRampToValueAtTime(0, start + .3);
      oscillator.connect(envelope); envelope.connect(gain); sound.oscillators.push(oscillator);
      oscillator.onended = () => { oscillator.disconnect(); envelope.disconnect(); if (i === 2 && epoch === outputTestEpoch) stopOutputTest(); };
      oscillator.start(start); oscillator.stop(start + .31);
    }
  } catch (e) { if (epoch === outputTestEpoch) { stopOutputTest(); settingsError(e.message || 'Не удалось воспроизвести звук. Проверьте устройство вывода.'); } }
};

function applyPeerVolume(p) {
  applyScreenVolume(p);
  const el = p.audioEl; if (!el) return;
  const v = (p.volume == null ? 1 : p.volume) * speakerLevel;
  if (v <= 1) {
    if (p.gain) { try { p.gainSrc.disconnect(); p.gain.disconnect(); } catch (e) {} p.gain = p.gainSrc = null; }
    el.muted = false; el.volume = v;
    return;
  }
  try {
    const ac = playbackCtx();
    if (ac && !p.gain && el.srcObject) {
      p.gainSrc = ac.createMediaStreamSource(el.srcObject);
      p.gain = ac.createGain();
      p.gainSrc.connect(p.gain); p.gain.connect(ac.destination);
    }
    if (p.gain) { el.muted = true; el.volume = 1; p.gain.gain.value = v; }
    else { el.muted = false; el.volume = 1; }
  } catch (e) { el.muted = false; el.volume = 1; }
}
function newPeerObj(info, self) {
  const audioEl = self ? null : Object.assign(document.createElement('audio'), { autoplay: true });
  if (audioEl) {
    document.body.appendChild(audioEl);
    if (outputDevice && audioEl.setSinkId) audioEl.setSinkId(outputDevice).catch(() => settingsError('Не удалось выбрать наушники для нового участника. Выберите устройство ещё раз.'));
  }
  const screenAudioEl = audioEl ? audioEl.cloneNode() : null;
  if (screenAudioEl) { document.body.appendChild(screenAudioEl); if (outputDevice && screenAudioEl.setSinkId) screenAudioEl.setSinkId(outputDevice).catch(() => settingsError('Не удалось изменить устройство вывода демонстрации.')); }
  return { screenAudioEl, screenVolume: 1, id: info.id, name: info.name, self: !!self, mutes: self ? { mic: !micOn, cam: !camOn } : (info.mutes || {}), host: !!info.host, speaking: false, audioEl, vtiles: new Map(), speakTimer: 0, volume: 1, gain: null, gainSrc: null };
}

function addPeer(info) {
  if (info.id === selfId || peers.has(info.id)) { if (peers.has(info.id)) { const p = peers.get(info.id); p.host = !!info.host; } return; }
  const peer = newPeerObj(info, false);
  peers.set(info.id, peer); ensureVideoTile(peer, 'cam');
  updateCount();
}

function removePeer(id) {
  const p = peers.get(id); if (!p) return;
  clearTimeout(p.speakTimer); p.gainSrc?.disconnect(); p.gain?.disconnect();

  for (const vt of p.vtiles.values()) { if (!p.self && vt.video.srcObject) vt.video.srcObject.getTracks().forEach((t) => t.stop()); if (document.fullscreenElement === vt.tile) document.exitFullscreen().catch(() => {}); if (expandedScreen === vt.tile) closeExpandedScreen(); vt.video.srcObject = null; vt.tile.remove(); }
  if (p.screenAudioEl) { stopStream(p.screenAudioEl.srcObject); p.screenAudioEl.srcObject = null; p.screenAudioEl.remove(); }
  if (p.audioEl) { if (p.audioEl.srcObject) p.audioEl.srcObject.getTracks().forEach((t) => t.stop()); p.audioEl.remove(); }
  for (const [mid, info] of trackMap) if (info.peerId === id) trackMap.delete(mid);
  peers.delete(id); updateCount(); layoutStage();
}

function reconcileVideoTiles() {
  const active = new Set();
  for (const info of trackMap.values()) active.add(info.peerId + ':' + info.source);
  for (const p of peers.values()) {
    if (p.self) continue;
    if (!active.has(p.id + ':screen-audio') && p.screenAudioEl?.srcObject) { stopStream(p.screenAudioEl.srcObject); p.screenAudioEl.srcObject = null; updateScreenAudioControl(p); }
    for (const source of [...p.vtiles.keys()]) if (!active.has(p.id + ':' + source)) {
      if (source === 'cam') { p.vtiles.get(source).hasVideo = false; renderVideoTile(p, source); }
      else removeVideoTile(p, source);
    }
  }
}

function initials(name) { return (name || '?').trim().slice(0, 2).toUpperCase(); }

function renderVideoTile(peer, source) {
  const vt = peer.vtiles.get(source); if (!vt) return;
  const camMuted = source === 'cam' && !!(peer.mutes && peer.mutes.cam);
  const show = vt.hasVideo && !camMuted;
  const visibilityChanged = vt.tile.dataset.video !== String(show);
  vt.tile.dataset.video = String(show);
  vt.video.style.display = show ? '' : 'none';
  vt.avatar.style.display = show ? 'none' : '';
  vt.avatar.textContent = initials(peer.name);
  const micMuted = !!(peer.mutes && peer.mutes.mic);
  vt.nm.textContent = peer.name + (peer.self ? ' (вы)' : '') + (source === 'screen' ? ' — экран' : '');
  vt.label.querySelector('.micoff')?.remove();
  if (micMuted && source !== 'screen') vt.label.prepend(icon('i-mic-off', 'micoff'));
  vt.tile.classList.toggle('speaking', peer.speaking && source === 'cam');
  if (source === 'cam' && visibilityChanged) layoutStage();
}
function relabel(peer) { for (const s of peer.vtiles.keys()) renderVideoTile(peer, s); }

function onPeerMute(m) {
  const p = peers.get(m.peerId); if (!p) return;
  p.mutes = p.mutes || {}; p.mutes[m.source] = !!m.muted;
  if (m.source === 'screen' && m.muted) removeVideoTile(p, 'screen');
  if (m.source === 'screen-audio') { applyScreenVolume(p); updateScreenAudioControl(p); }
  relabel(p); renderPeople();
}
function setSpeaking(p, on) {
  p.speaking = on; renderVideoTile(p, 'cam');
  if (on) { clearTimeout(p.speakTimer); p.speakTimer = setTimeout(() => { p.speaking = false; renderVideoTile(p, 'cam'); }, 1500); }
}

function updateCount() {
  $('count').textContent = String(peers.size || 1);
  $('peopleBtn').setAttribute('aria-label', `Участники: ${peers.size || 1}`);
  $('waitingHint').hidden = peers.size !== 1 || !joined;
}
function updateTimer() { const s = Math.floor((Date.now() - startedAt) / 1000); $('timer').textContent = `${String(Math.floor(s / 60)).padStart(2, '0')}:${String(s % 60).padStart(2, '0')}`; }
let stageLayoutFrame = 0;
function layoutStage() {
  const n = stage.children.length;
  stage.dataset.count = String(n); stage.dataset.many = String(n > 4);
  const portraitCount = stage.querySelectorAll('.tile[data-source="cam"][data-video="true"][data-portrait="true"]').length;
  stage.dataset.portrait = String(portraitCount > 0 && !stage.querySelector('.tile[data-source="screen"]'));
  stage.dataset.bothPortrait = String(n === 2 && portraitCount === 2);
  if (stageLayoutFrame) return;
  stageLayoutFrame = requestAnimationFrame(() => {
    stageLayoutFrame = 0;
    if (!stage.clientHeight) return;
    const style = getComputedStyle(stage), columns = style.gridTemplateColumns.split(' ').length;
    const rows = Math.max(1, Math.ceil(stage.children.length / columns)), gap = parseFloat(style.rowGap) || 0;
    const cards = [...stage.children], footer = 56;
    const inverseRatio = card => { const v = card.querySelector('video'); return card.dataset.video === 'true' && v.videoWidth ? v.videoHeight / v.videoWidth : 9 / 16; };
    if (stage.dataset.portrait === 'true' && columns === 1 && cards.length === 2) {

      const available = Math.max(120, stage.clientHeight - gap - footer * 2);
      const total = cards.reduce((sum, card) => sum + inverseRatio(card), 0);
      for (const card of cards) card.style.setProperty('--camera-height', Math.floor(footer + available * inverseRatio(card) / total) + 'px');
    } else {
      const available = Math.max(Math.min(200, stage.clientHeight), Math.floor((stage.clientHeight - gap * (rows - 1)) / rows));
      for (let row = 0; row < rows; row++) {
        const group = cards.slice(row * columns, (row + 1) * columns);
        const natural = Math.max(...group.map(card => (card.clientWidth - 18) * inverseRatio(card) + footer));
        for (const card of group) card.style.setProperty('--camera-height', Math.floor(Math.min(available, natural)) + 'px');
      }
    }
  });
}
new ResizeObserver(layoutStage).observe(stage);

function banner(t) { const b = $('banner'); b.textContent = t; b.hidden = false; }
function clearBanner() { $('banner').hidden = true; }

function renderPeople() {
  const list = $('peopleList'); if (!list) return;
  const arr = [...peers.values()].sort((a, b) => (b.host - a.host) || a.name.localeCompare(b.name, 'ru'));
  list.innerHTML = '';
  for (const p of arr) {
    const li = document.createElement('li');
    const pa = document.createElement('span'); pa.className = 'pa'; pa.textContent = initials(p.name); li.appendChild(pa);
    const nm = document.createElement('span'); nm.className = 'pname'; nm.textContent = p.name + (p.self ? ' (вы)' : ''); li.appendChild(nm);
    if (p.host) { const t = document.createElement('span'); t.className = 'tag'; t.textContent = 'организатор'; li.appendChild(t); }
    if (p.mutes && p.mutes.mic) { const s = document.createElement('span'); s.className = 'st mic'; s.appendChild(icon('i-mic-off')); li.appendChild(s); }
    if (isHost && !p.self) { const k = document.createElement('button'); k.className = 'kick'; k.title = 'Удалить'; k.appendChild(icon('i-x')); k.onclick = () => send({ type: 'remove', peerId: p.id }); li.appendChild(k); }
    if (!p.self) {
      const vol = document.createElement('div'); vol.className = 'pvol';
      vol.appendChild(icon('i-vol', 'sm'));
      const r = document.createElement('input'); r.type = 'range'; r.min = '0'; r.max = '200'; r.step = '5';
      r.value = String(Math.round((p.volume == null ? 1 : p.volume) * 100));
      r.setAttribute('aria-label', 'Громкость участника ' + p.name);
      const val = document.createElement('span'); val.className = 'pvolval'; val.textContent = r.value + '%';
      r.oninput = () => { p.volume = (+r.value) / 100; val.textContent = r.value + '%'; applyPeerVolume(p); };
      vol.append(r, val); li.appendChild(vol);
    }
    list.appendChild(li);
  }
}

function applyHostUI() {
  $('hostTools').hidden = !isHost;
  const t = $('lockRoom').querySelector('.t'); if (t) t.textContent = roomLocked ? 'Открыть комнату' : 'Закрыть комнату';
}
function onHostChange(id) { for (const p of peers.values()) p.host = (p.id === id); isHost = (id === selfId); applyHostUI(); renderPeople(); relabelAll(); }
function relabelAll() { for (const p of peers.values()) relabel(p); }

$('muteAll').onclick = () => send({ type: 'mute-all' });
$('lockRoom').onclick = () => send({ type: 'lock', locked: !roomLocked });
$('endAll').onclick = () => openDialog('end');
$('confirmEnd').onclick = () => { send({ type: 'end' }); callDialog.close(); };
$('cancelEnd').onclick = () => openDialog('people');

async function sendChat(text) {
  if (!text.trim()) return;
  const payload = enc.encode(JSON.stringify({ n: myName, t: text }));
  let blob;
  if (chatKey) { const iv = crypto.getRandomValues(new Uint8Array(12)); const ct = new Uint8Array(await crypto.subtle.encrypt({ name: 'AES-GCM', iv }, chatKey, payload)); const out = new Uint8Array(12 + ct.length); out.set(iv); out.set(ct, 12); blob = b64enc(out); }
  else blob = b64enc(payload);
  if (!send({ type: 'chat', chat: blob })) throw new Error('offline');
  addChat(myName, text, true);
}

async function onChat(m) {
  try {
    const raw = b64dec(m.chat); let payload;
    if (chatKey) { const iv = raw.subarray(0, 12); const ct = raw.subarray(12); payload = new Uint8Array(await crypto.subtle.decrypt({ name: 'AES-GCM', iv }, chatKey, ct)); }
    else payload = raw;
    const o = JSON.parse(dec.decode(payload));

    const peer = peers.get(m.peerId);
    addChat(peer ? peer.name : (o.n || '?'), o.t || '', false);
  } catch {                             }
}

function addChat(who, text, mine) {
  const li = document.createElement('li'); if (mine) li.className = 'mine';
  const w = document.createElement('span'); w.className = 'who'; w.textContent = who;
  const b = document.createElement('span'); b.className = 'bubble'; b.textContent = text;
  li.append(w, b);
  $('chatLog').appendChild(li);
  while ($('chatLog').children.length > 300) $('chatLog').firstElementChild.remove();
  $('chatLog').scrollTop = $('chatLog').scrollHeight;
  if (!mine && !chatOpen) { unread++; const bg = $('chatBadge'); bg.textContent = String(unread); bg.hidden = false; $('chatToggle').classList.add('active'); }
}
function sysChat(text) { const li = document.createElement('li'); li.className = 'sys'; li.textContent = text; $('chatLog').appendChild(li); }

let chatSending = false;
$('chatForm').onsubmit = async (e) => {
  e.preventDefault();
  const input = $('chatInput'), value = input.value;
  if (chatSending || !value.trim()) return;
  chatSending = true; $('chatSend').disabled = true; $('chatError').hidden = true;
  try { await sendChat(value); if (input.value === value) input.value = ''; }
  catch { $('chatError').textContent = 'Не отправлено. Текст сохранён — повторите после восстановления связи.'; $('chatError').hidden = false; }
  finally { chatSending = false; $('chatSend').disabled = false; input.focus(); }
};

let speakingIv = 0, speakingSource = null;
function startSpeakingDetection() {
  clearInterval(speakingIv); speakingSource?.disconnect(); speakingSource = null;
  if (!micTrack) return;
  try {
    const ctx = meterCtx || new (window.AudioContext || window.webkitAudioContext)(); meterCtx = ctx;
    const an = ctx.createAnalyser(); an.fftSize = 512; an.smoothingTimeConstant = 0.3;
    speakingSource = ctx.createMediaStreamSource(new MediaStream([micTrack])); speakingSource.connect(an);
    const data = new Uint8Array(an.frequencyBinCount);
    let speaking = false, lastSend = 0, hangover = 0;
    speakingIv = setInterval(() => {
      if (!micOn) { if (speaking) { speaking = false; send({ type: 'speaking', speaking: false }); const me = peers.get(selfId); if (me) setSpeaking(me, false); } return; }
      an.getByteFrequencyData(data); let s = 0; for (const v of data) s += v; const level = s / data.length;
      const now = Date.now();
      if (level > 18) hangover = now + 600;
      const sp = now < hangover;
      if (sp !== speaking && now - lastSend > 180) { speaking = sp; lastSend = now; send({ type: 'speaking', speaking: sp }); const me = peers.get(selfId); if (me) setSpeaking(me, sp); }
    }, 150);
  } catch {}
}

async function makeMicPipeline(raw) {
  const ctx = new (window.AudioContext || window.webkitAudioContext)({ sampleRate: 48000 });
  const source = ctx.createMediaStreamSource(new MediaStream([raw]));
  const gain = ctx.createGain(), destination = ctx.createMediaStreamDestination();
  destination.channelCount = 1; gain.gain.value = micLevel;
  source.connect(gain); gain.connect(destination);
  const track = destination.stream.getAudioTracks()[0];
  const pipeline = { raw, ctx, gain, track, stop() { source.disconnect(); gain.disconnect(); track.stop(); ctx.close().catch(() => {}); } };
  try { await ctx.resume(); if (left) throw new Error('left'); return pipeline; }
  catch (e) { pipeline.stop(); throw e; }
}

function settingsError(message) { $('settingsError').textContent = message; $('settingsError').hidden = !message; }
let callDeviceBusy = false, transportBusy = false, micMuteEpoch = 0;
function syncCallSettingsBusy() {
  for (const id of ['mic', 'cam', 'callMic', 'callCam', 'noiseFilter']) $(id).disabled = callDeviceBusy || transportBusy;
  $('callTransport').disabled = callDeviceBusy || transportBusy;
  if (!navigator.mediaDevices?.getSupportedConstraints().noiseSuppression) $('noiseFilter').disabled = true;
}
async function withCallDevice(action) {
  if (callDeviceBusy || transportBusy || !pub || left) return;
  callDeviceBusy = true; syncCallSettingsBusy(); settingsError('');
  try { await action(); }
  catch (e) { if (!left) { settingsError(e.message || 'Не удалось изменить устройство. Попробуйте ещё раз.'); if (!callDialog.open) banner($('settingsError').textContent); } }
  finally { callDeviceBusy = false; syncCallSettingsBusy(); }
}
async function replaceCallInput(kind) {
  const audio = kind === 'audio', select = $(audio ? 'micSel' : 'camSel');
  const connection = pub, old = audio ? micTrack : camTrack;
  const transceiver = audio ? micTr : camTr;
  let stream, pipeline;
  try {
    stream = await navigator.mediaDevices.getUserMedia({ [kind]: deviceConstraints(kind) });
    const track = stream.getTracks()[0];
    if (audio && typeof track.getSettings().noiseSuppression === 'boolean' && track.getSettings().noiseSuppression !== noiseFilter) throw new Error('Unsupported noise processing');
    track.enabled = audio ? micOn : camOn;
    if (audio) pipeline = await makeMicPipeline(track);
    if (left || connection !== pub || connection.signalingState === 'closed') { pipeline?.stop(); stopStream(stream); return false; }
    const outgoing = audio ? pipeline.track : track;
    if (transceiver) await transceiver.sender.replaceTrack(outgoing);
    else {
      const tr = connection.addTransceiver(outgoing, { direction: 'sendonly', streams: [localStream] });
      attachTransform(tr.sender, 'encrypt');
      if (audio) micTr = tr; else camTr = tr;
      await republish();
    }
    if (left || connection !== pub) { pipeline?.stop(); stopStream(stream); return false; }
    track.enabled = audio ? micOn : camOn;
    if (old) { localStream.removeTrack(old); old.stop(); }
    localStream.addTrack(track); watchPreviewTrack(track, select.value);
    if (audio) { micPipeline?.stop(); micPipeline = pipeline; micTrack = track; startSpeakingDetection(); }
    else { camTrack = track; ensureSelfTile(); }
    return true;
  } catch (e) {
    pipeline?.stop(); stopStream(stream);
    throw new Error(audio ? 'Не удалось включить выбранный микрофон. Проверьте доступ и подключение.' : 'Не удалось включить выбранную камеру. Проверьте доступ и подключение.');
  }
}
async function toggleInput(kind) {
  const muteEpoch = micMuteEpoch;
  const audio = kind === 'audio', old = audio ? micTrack : camTrack;
  const enabled = audio ? micOn : camOn, select = $(audio ? 'micSel' : 'camSel');
  if (!enabled && (!old || old.readyState === 'ended' || previewTrackDevices.get(old) !== select.value)) {
    if (!await replaceCallInput(kind)) return;
  }
  if (left || (audio && muteEpoch !== micMuteEpoch)) return;
  const next = !enabled;
  if (audio) { micOn = next; if (micTrack) micTrack.enabled = next; }
  else { camOn = next; if (camTrack) camTrack.enabled = next; }
  const source = audio ? 'mic' : 'cam';
  setCtrl($(source), next); send({ type: 'mute', source, muted: !next });
  const me = peers.get(selfId);
  if (me) { me.mutes[source] = !next; relabel(me); renderVideoTile(me, 'cam'); renderPeople(); }
}

function muteMic() {
  micMuteEpoch++;
  micOn = false; if (micTrack) micTrack.enabled = false; setCtrl($('mic'), false);
  send({ type: 'mute', source: 'mic', muted: true });
  const me = peers.get(selfId); if (me) { me.mutes.mic = true; relabel(me); renderPeople(); }
}
$('mic').onclick = () => withCallDevice(() => toggleInput('audio'));
$('cam').onclick = () => withCallDevice(() => toggleInput('video'));

async function refreshCallDevices() {
  if (callDeviceBusy || left) return;
  try {
    const devs = await navigator.mediaDevices.enumerateDevices();
    for (const [id, kind, chosen] of [['callMic', 'audioinput', $('micSel').value], ['callCam', 'videoinput', $('camSel').value], ['callOutput', 'audiooutput', outputDevice]]) {
      const el = $(id);
      if (id === 'callOutput' && !outputSupported) { el.replaceChildren(new Option('Системное устройство', '')); el.disabled = true; continue; }
      el.replaceChildren(new Option('По умолчанию', ''));
      devs.filter(d => d.kind === kind && d.deviceId).forEach((d, i) => el.add(new Option(d.label || `Устройство ${i + 1}`, d.deviceId)));
      if (chosen && ![...el.options].some(o => o.value === chosen)) el.add(new Option('Устройство недоступно', chosen));
      el.value = chosen;
    }
  } catch { settingsError('Не удалось получить список устройств. Проверьте разрешения браузера.'); }
  $('callTransport').value = forceRelay ? 'relay' : 'auto';
  $('callTransport').options[1].disabled = !iceServers.length;
}
for (const [id, sourceId, kind] of [['callMic', 'micSel', 'audio'], ['callCam', 'camSel', 'video']]) {
  $(id).onchange = () => withCallDevice(async () => {
    const value = $(id).value, source = $(sourceId), previous = source.value;
    if (![...source.options].some(o => o.value === value)) source.add(new Option($(id).selectedOptions[0].text, value));
    source.value = value;
    try {

      if ((kind === 'audio' ? micOn : camOn) && !await replaceCallInput(kind)) return;
      $('settingsStatus').textContent = (kind === 'audio' ? micOn : camOn) ? 'Устройство изменено' : 'Устройство выбрано. Оно включится вместе с микрофоном или камерой.';
    } catch (e) { source.value = previous; $(id).value = previous; throw e; }
    finally { devicePickers.forEach(p => p.sync()); }
  });
}
navigator.mediaDevices?.addEventListener('devicechange', () => { if (joined) refreshCallDevices(); else listDevices(); });
$('micLevel').oninput = () => {
  micLevel = Number($('micLevel').value) / 100;
  $('micLevelValue').value = `${$('micLevel').value}%`;
  if (micPipeline) micPipeline.gain.gain.setTargetAtTime(micLevel, micPipeline.ctx.currentTime, .02);
};
$('speakerLevel').oninput = () => {
  speakerLevel = Number($('speakerLevel').value) / 100;
  $('speakerLevelValue').value = `${$('speakerLevel').value}%`;
  peers.forEach(applyPeerVolume);
  if (outputTest) outputTest.gain.gain.value = .12 * speakerLevel;
};
const noiseSupported = !!navigator.mediaDevices?.getSupportedConstraints().noiseSuppression;
if (!noiseSupported) { $('noiseFilter').checked = false; $('noiseFilter').disabled = true; $('noiseHelp').hidden = false; $('noiseHelp').textContent = 'Шумоподавление недоступно в этом браузере.'; }
$('noiseFilter').onchange = () => withCallDevice(async () => {
  const next = $('noiseFilter').checked, previous = noiseFilter;
  noiseFilter = next;
  try {

    if (micTrack?.readyState === 'live' && !await replaceCallInput('audio')) return;
    $('settingsStatus').textContent = next ? 'Шумоподавление включено' : 'Шумоподавление выключено';
  } catch { noiseFilter = previous; $('noiseFilter').checked = previous; throw new Error('Браузер не смог изменить шумоподавление для этого микрофона.'); }
});
const outputSupported = 'setSinkId' in HTMLMediaElement.prototype && 'setSinkId' in (window.AudioContext || window.webkitAudioContext).prototype;
$('callOutput').disabled = !outputSupported; $('outputHelp').hidden = outputSupported;
if (!outputSupported) $('callOutput').replaceChildren(new Option('Системное устройство', ''));
$('callOutput').onchange = async () => {
  stopOutputTest();
  const chosen = $('callOutput').value, previous = outputDevice;
  $('callOutput').disabled = true; settingsError('');
  const outputs = [...peers.values()].flatMap(p => [p.audioEl, p.screenAudioEl]).filter(Boolean);
  try {
    const ctx = playbackCtx(); if (ctx) outputs.push(ctx);
    const results = await Promise.allSettled(outputs.map(el => el.setSinkId(chosen)));
    if (results.some(r => r.status === 'rejected')) throw new Error('output');
    outputDevice = chosen; $('settingsStatus').textContent = 'Устройство вывода изменено';
  } catch {
    await Promise.allSettled(outputs.map(el => el.setSinkId(previous)));
    $('callOutput').value = previous; settingsError('Не удалось выбрать устройство вывода. Проверьте подключение и разрешения.');
  } finally { $('callOutput').disabled = false; }
};
$('callTheme').onchange = () => { applyTheme($('callTheme').value); storage.set('audio.theme', $('callTheme').value); };

async function restartTransport() {
  for (const pc of [pub, sub]) if (pc && pc.signalingState !== 'closed') pc.setConfiguration(pcConfig());
  if (sub?.remoteDescription && !send({ type: 'restart-sub-ice' })) throw new Error('offline');
  if (pub?.localDescription) { pub.restartIce(); await republish(); }
}
async function waitForTransport(relay, connection) {
  for (let i = 0; i < 30; i++) {
    await new Promise(resolve => setTimeout(resolve, 500));
    if (left || pub !== connection || ws?.readyState !== WebSocket.OPEN) throw new Error('disconnected');
    let connected = true;
    for (const pc of [pub, sub]) {
      if (!pc?.remoteDescription) continue;
      const stats = await pc.getStats(); let selected = false;
      stats.forEach(r => { if (r.type === 'transport' && r.selectedCandidatePairId) { const pair = stats.get(r.selectedCandidatePairId); const local = pair && stats.get(pair.localCandidateId); selected = !!local && (!relay || local.candidateType === 'relay'); } });
      if (!selected || pc.connectionState !== 'connected') connected = false;
    }
    if (connected) return;
  }
  throw new Error('timeout');
}
$('callTransport').onchange = async () => {
  if (transportBusy || callDeviceBusy || left) return;
  const previous = forceRelay, next = $('callTransport').value === 'relay', connection = pub;
  if (next === previous) return;
  transportBusy = true; syncCallSettingsBusy(); settingsError('');
  $('settingsStatus').textContent = 'Меняем соединение…'; forceRelay = next;
  try {
    await restartTransport(); await waitForTransport(next, connection);
    $('connMode').value = next ? 'relay' : 'auto'; storage.set('audio.transport', $('connMode').value);
    $('settingsStatus').textContent = 'Соединение изменено';
  } catch {
    forceRelay = previous; $('callTransport').value = previous ? 'relay' : 'auto';
    if (!left && pub === connection) { try { await restartTransport(); } catch {} }
    $('settingsStatus').textContent = ''; settingsError('Этот режим не подключился. Возвращён предыдущий режим.');
  } finally { transportBusy = false; syncCallSettingsBusy(); }
};

$('leave').onclick = () => { left = true; endCall(); };
for (const id of ['copyInvite', 'prepInvite', 'waitingInvite']) $(id).onclick = () => openDialog('invite');
let inviteNoticeTimer;
$('copyRoomInvite').onclick = async () => {
  clearTimeout(inviteNoticeTimer);
  try { await navigator.clipboard.writeText($('roomInvite').value); $('inviteStatus').textContent = 'Ссылка скопирована'; }
  catch { $('roomInvite').focus(); $('roomInvite').select(); $('inviteStatus').textContent = 'Скопируйте выделенную ссылку.'; }
  inviteNoticeTimer = setTimeout(() => { $('inviteStatus').textContent = ''; }, 2500);
};

function setCtrl(btn, on) { btn.classList.toggle('on', on); btn.setAttribute('aria-pressed', String(on)); }
function flash(btn, text) { const old = [...btn.childNodes].map(n => n.cloneNode(true)); btn.textContent = text; setTimeout(() => btn.replaceChildren(...old), 1600); }

const callDialog = $('callDialog');
for (const id of ['peopleView', 'settingsView']) $('callDialogBody').append($(id));
let panelOpener = null, dialogOpener = null;
function openDialog(view) {
  stopOutputTest();
  if (!callDialog.open) dialogOpener = document.activeElement;
  callDialog.dataset.view = view;
  for (const name of ['people', 'settings', 'invite', 'end']) $(name + 'View').hidden = name !== view;
  $('callDialogTitle').textContent = { people: 'Участники', settings: 'Настройки', invite: 'Пригласить', end: 'Завершить звонок?' }[view];
  $('settingsBtn').setAttribute('aria-expanded', String(view === 'settings'));
  $('peopleBtn').setAttribute('aria-expanded', String(view === 'people'));
  if (view === 'invite') { $('roomInvite').value = location.origin + '/#' + b64urlEnc(K); $('inviteStatus').textContent = ''; }
  if (!callDialog.open) callDialog.showModal();
  $('callDialogClose').focus();
  if (view === 'settings') refreshCallDevices();
  if (view === 'people') renderPeople();
}
callDialog.addEventListener('close', () => {
  stopOutputTest(); $('settingsBtn').setAttribute('aria-expanded', 'false'); $('peopleBtn').setAttribute('aria-expanded', 'false');
  if (dialogOpener?.isConnected && !left) dialogOpener.focus();
});
callDialog.onclick = e => { if (e.target !== callDialog) return; const r = callDialog.getBoundingClientRect(); if (e.clientX < r.left || e.clientX > r.right || e.clientY < r.top || e.clientY > r.bottom) callDialog.close(); };
$('callDialogClose').onclick = () => callDialog.close();
function openPanel() {
  if ($('panel').hidden) panelOpener = document.activeElement;
  $('panel').hidden = false; $('chatView').hidden = false; chatOpen = true;
  unread = 0; $('chatBadge').hidden = true; $('chatToggle').classList.remove('active'); $('chatInput').focus();
}
function closePanel() { $('panel').hidden = true; chatOpen = false; panelOpener?.focus(); }
$('peopleBtn').onclick = () => openDialog('people');
$('chatToggle').onclick = () => { if (!$('panel').hidden) closePanel(); else openPanel(); };
$('tabChat').onclick = () => $('chatInput').focus();
$('settingsBtn').onclick = () => openDialog('settings');
$('panelClose').onclick = closePanel;
document.addEventListener('keydown', e => { if (e.key === 'Escape' && !callDialog.open && !$('shareDialog').open && !$('panel').hidden) closePanel(); });

function showCall() { $('prejoin').hidden = true; $('call').hidden = false; setCtrl($('mic'), micOn); setCtrl($('cam'), camOn); document.querySelector('.skip-link').hidden = true; document.title = 'Звонок — audio.lgbt'; $('leave').focus(); startConnChip(); }
let connIv = 0;
function startConnChip() { if (connIv) return; updateConnChip(); connIv = setInterval(updateConnChip, 3000); }
async function updateConnChip() {
  const pc = sub || pub; if (!pc) return;
  try {
    const stats = await pc.getStats(); const by = new Map(); stats.forEach(r => by.set(r.id, r));
    let pair = null;
    stats.forEach(r => { if (r.type === 'candidate-pair' && (r.nominated || r.state === 'succeeded') && (r.bytesReceived || 0) > 0) pair = r; });
    const chip = $('connChip'); if (!chip || !pair) return;
    const L = by.get(pair.localCandidateId) || {}, R = by.get(pair.remoteCandidateId) || {};
    const relay = L.candidateType === 'relay' || R.protocol === 'tcp' || R.port === 443;
    chip.hidden = false;
    chip.lastChild.textContent = relay ? ' Через TLS' : ' Прямое (UDP)';
    chip.title = relay ? 'Медиа идёт через TLS-ретранслятор на 443 — как обычный HTTPS (обход фильтров)' : 'Медиа идёт напрямую по UDP — лучшее качество и задержка';
  } catch (e) {}
}
function endCall(message) {
  left = true; joined = false; previewRequest++;
  stopOutputTest(); if (callDialog.open) callDialog.close();
  stopShare(false); if ($('shareDialog').open) $('shareDialog').close(); closeExpandedScreen();
  clearInterval(speakingIv); clearInterval(connIv); connIv = 0;
  cancelAnimationFrame(meterRAF); speakingSource?.disconnect(); meterSource?.disconnect();
  clearInterval(timerIv); clearTimeout(joinTimer); clearTimeout(reconnectTimer); reconnectTimer = 0;
  if (ws) { ws.onclose = null; ws.close(); ws = null; }
  teardownPeers();
  stopStream(localStream); stopStream(previewStream); stopStream(screenStream);
  micPipeline?.stop(); micPipeline = null;
  worker?.terminate(); worker = null;
  meterCtx?.close().catch(() => {}); playCtx?.close().catch(() => {});
  $('preview').srcObject = null;
  $('call').hidden = true; $('prejoin').hidden = true; $('ended').hidden = false;
  $('endedTitle').textContent = message || 'Вы вышли из звонка';
  $('endedDuration').textContent = startedAt ? `Время в звонке: ${$('timer').textContent}` : '';
  $('returnCall').hidden = !!message;
  document.title = 'Звонок завершён — audio.lgbt'; $('endedTitle').focus();
}
$('returnCall').onclick = () => location.reload();
window.addEventListener('beforeunload', () => { try { ws && ws.close(); } catch {} });

(async function boot() {
  if (forcePlain) { $('e2eeBadge').textContent = 'Сквозное шифрование отключено в этой ссылке'; $('e2eeBadge').hidden = false; }
  else if (e2eeOK) {
    $('e2eeBadge').hidden = false;
  } else if (!forcePlain) {
    const b = $('e2eeBadge');
    b.textContent = 'Этот браузер не поддерживает шифрование. Для входа нужен актуальный Chrome, Edge или Firefox.';
    b.style.color = 'var(--dim)';
    b.hidden = false;
  }
  if (badLink) { $('inviteError').textContent = 'Ссылка повреждена. Вставьте полное приглашение или создайте новый звонок.'; $('inviteError').hidden = false; }

})();

if (debug) {
  window.__z = {
    peers, trackMap, e2ee: e2eeOK, serverCands: dbgCands,
    pub: () => pub, sub: () => sub,
    tiles: () => document.querySelectorAll('#stage .tile').length,
    async stats(pc) { pc = pc || sub; if (!pc) return {}; const out = {}; (await pc.getStats()).forEach((s) => { if (s.type === 'inbound-rtp') out[s.kind] = { frames: s.framesDecoded || 0, bytes: s.bytesReceived || 0, pkts: s.packetsReceived || 0 }; }); return out; },
    async transport(pc) { pc = pc || sub; if (!pc) return {}; const r = await pc.getStats(); let pid = null; const sel = {}; const rc = {}; r.forEach((s) => { if (s.type === 'transport' && s.selectedCandidatePairId) pid = s.selectedCandidatePairId; }); r.forEach((s) => { if (s.type === 'remote-candidate') rc[s.protocol + '/' + s.candidateType] = 1; if (s.type === 'candidate-pair' && (s.id === pid || s.nominated)) { const l = r.get(s.localCandidateId); if (l) sel.local = l.protocol + '/' + l.candidateType; } }); return { selected: sel, remoteCandidates: Object.keys(rc) }; },
  };
}
