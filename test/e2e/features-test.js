const { chromium } = require('playwright');
const BASE = 'http://127.0.0.1:8080/';
const SHOTS = '/Users/localhost/Desktop/call/screenshots';

const INIT = `(() => {
  window.__pcs = [];
  const O = window.RTCPeerConnection;
  window.RTCPeerConnection = function(...a){ const pc = new O(...a); try{ window.__pcs.push(pc); }catch(e){} return pc; };
  window.RTCPeerConnection.prototype = O.prototype;
})();`;
const STATS = `async () => { let f=0,a=0; for(const pc of (window.__pcs||[])){let s;try{s=await pc.getStats();}catch(e){continue;} s.forEach(r=>{ if(r.type==='inbound-rtp'&&r.kind==='video')f+=(r.framesDecoded||0); if(r.type==='inbound-rtp'&&r.kind==='audio')a+=(r.packetsReceived||0);});} return {framesDecoded:f,audioPktRecv:a}; }`;
const stats = p => p.evaluate(eval('('+STATS+')'));

const checks = [];
const push = (n, ok, d='') => { checks.push([n, !!ok, d]); };

(async () => {
  const browser = await chromium.launch({ headless:true,
    args:['--use-fake-device-for-media-stream','--use-fake-ui-for-media-stream','--autoplay-policy=no-user-gesture-required'] });
  try {
    // ---------- No-flash: room URL paints room state (html.room + prep), no landing ----------
    const cNf = await browser.newContext({ viewport:{width:1200,height:800} }); // no perms → stays on permission button
    const Nf = await cNf.newPage();
    await Nf.goto(BASE + '#AAAAAAAAAAAAAAAAAAAAAA', { waitUntil:'domcontentloaded' });
    push('no-flash: <html> has .room class early', await Nf.evaluate(()=>document.documentElement.classList.contains('room')));
    push('no-flash: landing hidden on room URL', await Nf.evaluate(()=>getComputedStyle(document.getElementById('landingIntro')).display)==='none');
    push('no-flash: preparation shown on room URL', await Nf.evaluate(()=>getComputedStyle(document.getElementById('preparation')).display)!=='none');
    await cNf.close();

    // ---------- Permission mechanism + auto-preview + flow + connMode ----------
    const cP = await browser.newContext({ viewport:{width:1200,height:900}, permissions:['microphone','camera'] });
    await cP.addInitScript(INIT); const P = await cP.newPage();
    await P.goto(BASE, { waitUntil:'load' });
    await P.waitForTimeout(400);
    push('flow: home has no #hash', (await P.evaluate(()=>location.hash))==='');
    push('flow: home not .room', await P.evaluate(()=>!document.documentElement.classList.contains('room')));
    const camState = await P.evaluate(async()=>{try{return (await navigator.permissions.query({name:'camera'})).state}catch(e){return 'ERR'}});
    push('perm: Permissions API reports camera granted', camState==='granted', 'state='+camState);
    // create
    await P.click('#createRoom');
    // auto-preview should open WITHOUT clicking "Разрешить доступ"
    let autoOk=false; try { await P.waitForSelector('#join:not([disabled])', {timeout:6000}); autoOk=true; } catch(e){}
    push('auto-preview: join enabled without clicking Allow', autoOk);
    push('auto-preview: "Разрешить доступ" hidden (preview auto-opened)', await P.isHidden('#enableDevices'));
    push('auto-preview: camera stream live in preview', await P.evaluate(()=>{const v=document.getElementById('preview');return !!(v&&v.srcObject&&v.srcObject.getVideoTracks().length)}));
    push('flow: create set #hash', (await P.evaluate(()=>location.hash)).length>20);
    push('flow: invite/paste-link hidden in room', await P.isHidden('#inviteEntry'));
    // connMode setting
    await P.click('#connSettings summary');
    await P.selectOption('#connMode', 'relay');
    push('transport: relay persists to localStorage', (await P.evaluate(()=>localStorage.getItem('audio.transport')))==='relay');
    await P.selectOption('#connMode', 'auto');
    push('transport: auto persists to localStorage', (await P.evaluate(()=>localStorage.getItem('audio.transport')))==='auto');
    await cP.close();

    // ---------- Full call + volume + connChip ----------
    const cA = await browser.newContext({ viewport:{width:1280,height:850}, permissions:['microphone','camera'] });
    await cA.addInitScript(INIT); const A = await cA.newPage();
    const eA=[]; A.on('pageerror',x=>eA.push(x.message)); A.on('console',m=>{if(m.type()==='error')eA.push(m.text());});
    await A.goto(BASE, { waitUntil:'load' }); await A.click('#createRoom');
    await A.waitForSelector('#join:not([disabled])', {timeout:8000});
    await A.fill('#name','Алиса'); await A.click('#join');
    await A.waitForSelector('#call:not([hidden])', {timeout:20000});
    const room = A.url();

    const cB = await browser.newContext({ viewport:{width:1280,height:850}, permissions:['microphone','camera'] });
    await cB.addInitScript(INIT); const B = await cB.newPage();
    const eB=[]; B.on('pageerror',x=>eB.push(x.message));
    await B.goto(room, { waitUntil:'load' });
    await B.waitForSelector('#join:not([disabled])', {timeout:8000});
    await B.fill('#name','Боря'); await B.click('#join');
    await B.waitForSelector('#call:not([hidden])', {timeout:20000});

    await A.waitForTimeout(5000);
    const sa = await stats(A), sb = await stats(B);
    push('call: A decodes B video', sa.framesDecoded>0, 'A.fd='+sa.framesDecoded);
    push('call: B decodes A video', sb.framesDecoded>0, 'B.fd='+sb.framesDecoded);

    // connChip visible with a path label
    await A.waitForTimeout(500);
    push('connChip: visible in call', await A.isVisible('#connChip'));
    push('connChip: shows a path label', /UDP|TLS/.test((await A.textContent('#connChip'))||''), 'text='+((await A.textContent('#connChip'))||'').trim());

    // Volume: open people panel on A, set B's slider
    await A.click('#peopleBtn');
    await A.waitForSelector('#peopleList input[type="range"]', {timeout:5000});
    // set 50%
    await A.$eval('#peopleList input[type="range"]', el => { el.value='50'; el.dispatchEvent(new Event('input',{bubbles:true})); });
    const vol50 = await A.evaluate(()=>{ const els=[...document.querySelectorAll('audio')]; return els.map(e=>e.volume); });
    push('volume: 50% sets remote audio element volume to 0.5', vol50.some(v=>Math.abs(v-0.5)<0.01), 'volumes='+JSON.stringify(vol50));
    // set 150% (boost → element muted, webaudio path)
    await A.$eval('#peopleList input[type="range"]', el => { el.value='150'; el.dispatchEvent(new Event('input',{bubbles:true})); });
    await A.waitForTimeout(200);
    const boost = await A.evaluate(()=>{ const els=[...document.querySelectorAll('audio')]; return { muted: els.map(e=>e.muted), vol: els.map(e=>e.volume) }; });
    push('volume: 150% boost mutes element (routes via WebAudio gain)', boost.muted.some(m=>m===true), 'muted='+JSON.stringify(boost.muted));
    // back to 100%
    await A.$eval('#peopleList input[type="range"]', el => { el.value='100'; el.dispatchEvent(new Event('input',{bubbles:true})); });
    const back = await A.evaluate(()=>{ const els=[...document.querySelectorAll('audio')]; return { muted: els.map(e=>e.muted), vol: els.map(e=>e.volume) }; });
    push('volume: back to 100% unmutes at full volume', back.muted.every(m=>m===false) && back.vol.some(v=>Math.abs(v-1)<0.01), JSON.stringify(back));

    await A.screenshot({ path: `${SHOTS}/18-people-volume.png` });

    push('no page errors (A)', eA.length===0, eA.slice(0,4).join(' | '));
    push('no page errors (B)', eB.length===0, eB.slice(0,4).join(' | '));
  } catch(e){ push('FATAL', false, e.message); }
  finally {
    let pass=true; for (const [n,ok,d] of checks){ if(!ok)pass=false; console.log((ok?'PASS':'FAIL').padEnd(5), n, d?('  ['+d+']'):''); }
    console.log(pass?'\n=== ALL PASS ===':'\n=== SOME FAIL ===');
    await browser.close();
  }
})();
