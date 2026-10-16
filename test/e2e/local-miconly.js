const { chromium } = require('playwright');
const BASE = 'http://127.0.0.1:8080/';
const SHOTS = '/Users/localhost/Desktop/call/screenshots';

const INIT = `(() => {
  window.__gum = [];
  try {
    const md = navigator.mediaDevices;
    const orig = md.getUserMedia.bind(md);
    md.getUserMedia = function(c){ try { window.__gum.push(JSON.parse(JSON.stringify(c||{}))); } catch(e){ window.__gum.push(String(c)); } return orig(c); };
  } catch(e){}
  window.__pcs = [];
  const O = window.RTCPeerConnection;
  window.RTCPeerConnection = function(...a){ const pc = new O(...a); try{ window.__pcs.push(pc); }catch(e){} return pc; };
  window.RTCPeerConnection.prototype = O.prototype;
})();`;

const STATS = `async () => {
  let framesDecoded=0, audioPktRecv=0, inVideo=0, inAudio=0, outVideo=0, outAudio=0;
  for (const pc of (window.__pcs||[])) {
    let stats; try { stats = await pc.getStats(); } catch(e){ continue; }
    stats.forEach(r => {
      if (r.type==='inbound-rtp' && r.kind==='video'){ inVideo++; framesDecoded += (r.framesDecoded||0); }
      if (r.type==='inbound-rtp' && r.kind==='audio'){ inAudio++; audioPktRecv += (r.packetsReceived||0); }
      if (r.type==='outbound-rtp' && r.kind==='video'){ outVideo++; }
      if (r.type==='outbound-rtp' && r.kind==='audio'){ outAudio++; }
    });
  }
  return { framesDecoded, audioPktRecv, inVideo, inAudio, outVideo, outAudio, gum: window.__gum };
}`;

async function prep(ctx){ const p = await ctx.newPage(); const e=[]; p.on('pageerror',x=>e.push('PE '+x.message)); p.on('console',m=>{if(m.type()==='error')e.push('CE '+m.text());}); p.__e=e; return p; }
const stats = p => p.evaluate(eval('('+STATS+')'));

async function enter(page, url, name, mode){
  await page.goto(url, { waitUntil:'load' });
  await page.waitForTimeout(400);
  await page.fill('#name', name);
  if (mode==='full') await page.click('#enableDevices');
  else if (mode==='mic') await page.click('#audioOnly');
  else if (mode==='skip') await page.click('#skipDevices');
  await page.waitForSelector('#join:not([disabled])', { timeout:15000 });
  await page.click('#join');
  await page.waitForSelector('#call:not([hidden])', { timeout:20000 });
}

(async () => {
  const browser = await chromium.launch({ headless:true,
    args:['--use-fake-device-for-media-stream','--use-fake-ui-for-media-stream','--autoplay-policy=no-user-gesture-required'] });
  const out = {};
  try {
    const cA = await browser.newContext({ viewport:{width:1280,height:800}, permissions:['microphone'] });
    await cA.addInitScript(INIT); const A = await prep(cA);
    await enter(A, BASE, 'Аноним (микрофон)', 'mic');
    const room = A.url(); out.room = room;

    const cB = await browser.newContext({ viewport:{width:1280,height:800}, permissions:['microphone','camera'] });
    await cB.addInitScript(INIT); const B = await prep(cB);
    await enter(B, room, 'Боря (камера)', 'full');

    const cC = await browser.newContext({ viewport:{width:1280,height:800} });
    await cC.addInitScript(INIT); const C = await prep(cC);
    await enter(C, room, 'Гость (слушатель)', 'skip');

    await A.waitForTimeout(5000);

    out.A = await stats(A); out.B = await stats(B); out.C = await stats(C);
    out.counts = { A: await A.textContent('#count').catch(()=>'?'), B: await B.textContent('#count').catch(()=>'?'), C: await C.textContent('#count').catch(()=>'?') };
    await B.screenshot({ path: `${SHOTS}/14-newdesign-call-miconly-view.png` });
    out.errs = { A:A.__e.slice(0,6), B:B.__e.slice(0,6), C:C.__e.slice(0,6) };
  } catch(e){ out.FATAL = e.message + '\n' + (e.stack||''); }
  finally {
    // ---- verdict ----
    const v = [];
    const A=out.A||{}, B=out.B||{}, C=out.C||{};
    const aNoVideoReq = (A.gum||[]).length>0 && (A.gum||[]).every(c => !c.video);
    v.push(['A(mic-only) NEVER requested camera', aNoVideoReq, 'gum='+JSON.stringify(A.gum)]);
    v.push(['A sends audio only (outAudio=1,outVideo=0)', A.outAudio===1 && A.outVideo===0, `outAudio=${A.outAudio} outVideo=${A.outVideo}`]);
    v.push(['C(no-device) made ZERO getUserMedia calls', (C.gum||[]).length===0, 'gum='+JSON.stringify(C.gum)]);
    v.push(['C sends nothing (out=0)', (C.outAudio||0)===0 && (C.outVideo||0)===0, `outA=${C.outAudio} outV=${C.outVideo}`]);
    v.push(['B hears A audio (B.audioPktRecv>0)', (B.audioPktRecv||0)>0, 'B.audioPktRecv='+B.audioPktRecv]);
    v.push(['C hears A audio (C.audioPktRecv>0)', (C.audioPktRecv||0)>0, 'C.audioPktRecv='+C.audioPktRecv]);
    v.push(['A sees B video (A.framesDecoded>0)', (A.framesDecoded||0)>0, 'A.framesDecoded='+A.framesDecoded]);
    v.push(['C sees B video (C.framesDecoded>0)', (C.framesDecoded||0)>0, 'C.framesDecoded='+C.framesDecoded]);
    v.push(['all 3 in room', out.counts && out.counts.A==='3' && out.counts.B==='3' && out.counts.C==='3', JSON.stringify(out.counts)]);
    console.log('ROOM', out.room);
    let pass=true;
    for (const [name,ok,detail] of v){ if(!ok) pass=false; console.log((ok?'PASS':'FAIL').padEnd(5), name, '  ['+detail+']'); }
    console.log('ERRORS', JSON.stringify(out.errs));
    if (out.FATAL) { pass=false; console.log('FATAL', out.FATAL); }
    console.log(pass ? '\n=== ALL PASS ===' : '\n=== SOME FAIL ===');
    await browser.close();
  }
})();
