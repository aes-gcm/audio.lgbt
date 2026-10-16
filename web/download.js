const toggle = document.getElementById('themeToggle');
function applyTheme(theme) {
  document.documentElement.dataset.theme = theme;
  toggle.setAttribute('aria-label', theme === 'dark' ? 'Включить светлую тему' : 'Включить тёмную тему');
  document.querySelector('meta[name="theme-color"]').content = theme === 'dark' ? '#101010' : '#f8f8f8';
}
applyTheme(document.documentElement.dataset.theme);
toggle.addEventListener('click', () => {
  const theme = document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark';
  applyTheme(theme);
  try { localStorage.setItem('audio.theme', theme); } catch {}
});

fetch('https://raw.githubusercontent.com/aes-gcm/audio.lgbt-windows/main/update/windows-x64.json', { cache: 'no-store', signal: AbortSignal.timeout(8000) })
  .then(response => { if (!response.ok) throw new Error('Update feed unavailable'); return response.json(); })
  .then(release => {
    if (!/^\d+\.\d+\.\d+(?:-preview\.\d+)?$/.test(release.version) || release.architecture !== 'x64') return;
    const expected = `https://github.com/aes-gcm/audio.lgbt-windows/releases/download/v${release.version}/audio.lgbt-${release.version}-x64.exe`;
    if (release.url !== expected) return;
    document.getElementById('downloadInstaller').href = expected;
    document.getElementById('downloadChecksum').href = `https://github.com/aes-gcm/audio.lgbt-windows/releases/tag/v${release.version}`;
  }).catch(() => {});
