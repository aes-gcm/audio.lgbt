const button = document.getElementById('serverAccount');
const dialog = document.getElementById('accountDialog');
const form = document.getElementById('accountForm');
const message = document.getElementById('accountError');
let current = null, pending = null;

async function request(path, body) {
  const response = await fetch(path, {
    method: body === undefined ? 'GET' : 'POST', credentials: 'same-origin',
    headers: body === undefined ? {} : {'Content-Type': 'application/json'},
    body: body === undefined ? undefined : JSON.stringify(body),
    signal: AbortSignal.timeout(10000),
  });
  const data = await response.json();
  if (!response.ok) throw new Error(data.error || 'Сервер недоступен. Попробуйте снова.');
  return data;
}
export async function refreshAccount() {
  current = await request('/api/server');
  button.hidden = !current.authenticationRequired;
  button.textContent = current.username ? `Выйти (${current.username})` : 'Войти';
  return current;
}
function openLogin() {
  if (pending) return pending.promise;
  let resolve;
  const promise = new Promise(done => { resolve = done; });
  pending = {promise, resolve}; message.hidden = true;
  dialog.showModal(); document.getElementById('accountName').focus();
  return promise;
}
dialog.addEventListener('close', () => {
  document.getElementById('accountPassword').value = '';
  pending?.resolve(!!current?.username); pending = null;
});
document.getElementById('accountClose').onclick = () => dialog.close();
form.onsubmit = async event => {
  event.preventDefault();
  const submit = document.getElementById('accountSubmit');
  submit.disabled = true; submit.textContent = 'Входим…'; message.hidden = true;
  try {
    await request('/api/login', {username: form.elements.username.value.trim(), password: form.elements.password.value});
    await refreshAccount(); dialog.close();
  } catch (error) { message.textContent = error.message; message.hidden = false; }
  finally { submit.disabled = false; submit.textContent = 'Войти'; }
};
button.onclick = async () => {
  button.disabled = true;
  try {
    const status = await refreshAccount();
    if (status.username) { await request('/api/logout', {}); await refreshAccount(); }
    else await openLogin();
  } catch (error) {
    openLogin(); message.textContent = error.message; message.hidden = false;
  } finally { button.disabled = false; }
};
export async function authorizeCreation() {
  const status = await refreshAccount();
  return !status.authenticationRequired || !!status.username || await openLogin();
}
refreshAccount().catch(() => { button.hidden = false; button.textContent = 'Войти'; });
