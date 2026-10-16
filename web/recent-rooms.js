
const storageKey = 'audio.recentRooms.v1';
const section = document.getElementById('recentRooms');
const list = document.getElementById('recentRoomsList');
const validKey = key => {
  if (typeof key !== 'string' || !/^[A-Za-z0-9_-]{22}$/.test(key)) return false;
  try { return btoa(atob(key.replace(/-/g, '+').replace(/_/g, '/') + '==')).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '') === key; }
  catch { return false; }
};
function readRooms() {
  try {
    const data = JSON.parse(localStorage.getItem(storageKey) || '[]');
    if (!Array.isArray(data)) return [];
    const seen = new Set();
    return data.filter(room => room && validKey(room.key) && typeof room.title === 'string' &&
      Number.isFinite(room.lastJoined) && room.lastJoined > 0 && room.lastJoined <= 8640000000000000)
      .sort((a, b) => b.lastJoined - a.lastJoined).filter(room => {
        if (seen.has(room.key)) return false;
        seen.add(room.key); return true;
      }).slice(0, 12).map(room => ({ key: room.key, title: room.title.trim().slice(0, 60) || 'Комната', lastJoined: room.lastJoined }));
  } catch { return []; }
}
function saveRooms(rooms) {
  try { localStorage.setItem(storageKey, JSON.stringify(rooms)); } catch { }
  renderRooms();
}
export function rememberRoom(key, created) {
  if (!validKey(key)) return;
  const rooms = readRooms(), previous = rooms.find(room => room.key === key);
  saveRooms([{ key, title: previous?.title || (created ? 'Моя комната' : 'Комната'), lastJoined: Date.now() },
    ...rooms.filter(room => room.key !== key)].slice(0, 12));
}
function action(label, glyph) {
  const button = document.createElement('button'); button.type = 'button';
  button.className = 'recent-action'; button.title = button.ariaLabel = label;
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('viewBox', '0 0 24 24'); svg.setAttribute('class', 'ic'); svg.setAttribute('aria-hidden', 'true');
  const use = document.createElementNS(svg.namespaceURI, 'use'); use.setAttribute('href', '#' + glyph); svg.append(use); button.append(svg);
  return button;
}
function renderRooms() {
  if (!section || !list) return;
  const rooms = readRooms(); section.hidden = rooms.length === 0; list.replaceChildren();
  for (const room of rooms) {
    const row = document.createElement('div'); row.className = 'recent-room';
    const link = document.createElement('a'); link.className = 'recent-join'; link.href = '/#' + room.key;
    link.ariaLabel = 'Войти: ' + room.title;
    const title = document.createElement('span'); title.className = 'recent-title'; title.textContent = room.title;
    const date = document.createElement('time'); date.dateTime = new Date(room.lastJoined).toISOString();
    date.textContent = new Date(room.lastJoined).toLocaleString('ru-RU', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });
    link.append(title, date);
    const rename = action('Переименовать: ' + room.title, 'i-edit');
    const remove = action('Убрать из истории: ' + room.title, 'i-x');
    rename.onclick = () => {
      const form = document.createElement('form'); form.className = 'recent-edit';
      const input = document.createElement('input'); input.value = room.title; input.maxLength = 60;
      input.required = true; input.ariaLabel = 'Название комнаты';
      const save = action('Сохранить название', 'i-check'); save.type = 'submit';
      const cancel = action('Отменить переименование', 'i-x');
      const close = () => { renderRooms(); list.querySelectorAll('.recent-room')[rooms.indexOf(room)]?.querySelector('button')?.focus(); };
      form.onsubmit = event => {
        event.preventDefault(); const value = input.value.trim(); if (!value) { input.focus(); return; }
        saveRooms(readRooms().map(item => item.key === room.key ? { ...item, title: value } : item)); close();
      };
      cancel.onclick = close;
      input.onkeydown = event => { if (event.key === 'Escape') { event.preventDefault(); close(); } };
      form.append(input, save, cancel); row.replaceChildren(form); input.focus(); input.select();
    };
    remove.onclick = () => {
      const index = rooms.indexOf(room);
      saveRooms(readRooms().filter(item => item.key !== room.key));
      (list.children[Math.min(index, list.children.length - 1)]?.querySelector('a') || document.getElementById('createRoom')).focus();
    };
    row.append(link, rename, remove); list.append(row);
  }
}
window.addEventListener('storage', event => { if (event.key === storageKey || event.key === null) renderRooms(); });
renderRooms();
