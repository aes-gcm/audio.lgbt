const { chromium } = require('playwright');
const BASE = 'https://audio.lgbt/';
const SHOTS = '/Users/localhost/Desktop/call/screenshots';

const INIT = `(() => {
  window.__pcs = [];
  const O = window.RTCPeerConnection;
  window.RTCPeerConnection = function(...a){ const pc = new O(...a); try{ window.__pcs.push(pc); }catch(e){} return pc; };
  window.RTCPeerConnection.prototype = O.prototype;
})();`;
const STATS = `async () => { let f=0,a=0; for(const pc of (window.__pcs||[])){let s;try{s=await pc.getStats();}catch(e){continue;} s.forEach(r=>{ if(r.type==='inbound-rtp'&&r.kind==='video')f+=(r.framesDecoded||0); if(r.type==='inbound-rtp'&&r.kind==='audio')a+=(r.packetsReceived||0);});} return {framesDecoded:f,audioPktRecv:a}; }`;

(async () => {
  const browser = await chromium.launch({ headless:true,
    args:['--use-fake-device-for-media-stream','--use-fake-ui-for-media-stream','--autoplay-policy=no-user-gesture-required'] });
  const out = { checks: [] };
  const push = (n, ok, d='') => out.checks.push([n, !!ok, d]);
  try {
    const cA = await browser.newContext({ viewport:{width:1280,height:850}, permissions:['microphone','camera'], ignoreHTTPSErrors:true });
    await cA.addInitScript(INIT); const A = await cA.newPage();
    const eA=[]; A.on('pageerror',x=>eA.push(x.message));

    // 1) HOME state (root, no hash)
    await A.goto(BASE, { waitUntil:'load' });
    await A.waitForTimeout(500);
    push('home: URL has NO #hash', (await A.evaluate(()=>location.hash))==='', 'hash="'+(await A.evaluate(()=>location.hash))+'"');
    push('home: preparation hidden', await A.evaluate(()=>document.getElementById('preparation').hidden)===true);
    push('home: createRoom visible', await A.isVisible('#createRoom'));
    push('home: invite field visible', await A.isVisible('#inviteEntry'));
    push('home: NOT in call', await A.evaluate(()=>document.getElementById('call').hidden)===true);
    push('home: preview NOT shown', !(await A.isVisible('#preview')) || await A.evaluate(()=>document.getElementById('preparation').hidden)===true);
    await A.screenshot({ path: `${SHOTS}/16-home.png` });

    // 2) CREATE
    await A.click('#createRoom');
    await A.waitForTimeout(400);
    const hash = await A.evaluate(()=>location.hash);
    push('create: URL now has #key', hash.length>20, 'hash len='+hash.length);
    push('create: preparation shown', await A.isVisible('#preparation'));
    push('create: invite/paste-link HIDDEN', await A.isHidden('#inviteEntry'), 'inviteVisible='+(await A.isVisible('#inviteEntry')));
    push('create: enableDevices visible', await A.isVisible('#enableDevices'));
    await A.screenshot({ path: `${SHOTS}/17-preparation.png` });
    const room = A.url();

    // 3) A joins
    await A.fill('#name','Алиса');
    await A.click('#enableDevices');
    await A.waitForSelector('#join:not([disabled])',{timeout:15000});
    await A.click('#join');
    await A.waitForSelector('#call:not([hidden])',{timeout:20000});
    push('A entered call', true);

    // 4) B opens link
    const cB = await browser.newContext({ viewport:{width:1280,height:850}, permissions:['microphone','camera'], ignoreHTTPSErrors:true });
    await cB.addInitScript(INIT); const B = await cB.newPage();
    await B.goto(room, { waitUntil:'load' });
    await B.waitForTimeout(500);
    push('link open: preparation shown directly', await B.isVisible('#preparation'));
    push('link open: NO paste-link field', await B.isHidden('#inviteEntry'), 'inviteVisible='+(await B.isVisible('#inviteEntry')));
    push('link open: createRoom hidden', await B.isHidden('#createRoom'));
    await B.fill('#name','Боря');
    await B.click('#enableDevices');
    await B.waitForSelector('#join:not([disabled])',{timeout:15000});
    await B.click('#join');
    await B.waitForSelector('#call:not([hidden])',{timeout:20000});

    await A.waitForTimeout(5000);
    const sa = await A.evaluate(eval('('+STATS+')'));
    const sb = await B.evaluate(eval('('+STATS+')'));
    push('call works: A decodes B video', sa.framesDecoded>0, 'A.framesDecoded='+sa.framesDecoded);
    push('call works: B decodes A video', sb.framesDecoded>0, 'B.framesDecoded='+sb.framesDecoded);
    out.errsA = eA.slice(0,5);
  } catch(e){ push('FATAL', false, e.message); }
  finally {
    let pass=true;
    for (const [n,ok,d] of out.checks){ if(!ok)pass=false; console.log((ok?'PASS':'FAIL').padEnd(5), n, d?('  ['+d+']'):''); }
    if (out.errsA && out.errsA.length) console.log('page errors A:', JSON.stringify(out.errsA));
    console.log(pass?'\n=== FLOW ALL PASS ===':'\n=== FLOW SOME FAIL ===');
    await browser.close();
  }
})();
