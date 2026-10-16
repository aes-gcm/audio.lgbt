const { chromium } = require('playwright');
const BASE = 'https://audio.lgbt/';
const SHOTS = '/Users/localhost/Desktop/call/screenshots';

const INIT = `(() => {
  window.__pcs = [];
  const O = window.RTCPeerConnection;
  window.RTCPeerConnection = function(...a){ const pc = new O(...a); try{ window.__pcs.push(pc); }catch(e){} return pc; };
  window.RTCPeerConnection.prototype = O.prototype;
})();`;

const STATS = `async () => {
  const res = { framesDecoded:0, audioPktRecv:0, bytesRecv:0, pairs:[] };
  for (const pc of (window.__pcs||[])) {
    let stats; try { stats = await pc.getStats(); } catch(e){ continue; }
    const byId = new Map(); stats.forEach(r => byId.set(r.id, r));
    stats.forEach(r => {
      if (r.type==='inbound-rtp' && r.kind==='video') res.framesDecoded += (r.framesDecoded||0);
      if (r.type==='inbound-rtp' && r.kind==='audio') res.audioPktRecv += (r.packetsReceived||0);
      if (r.type==='candidate-pair' && (r.nominated || r.state==='succeeded') && (r.bytesReceived||0)>0) {
        res.bytesRecv += r.bytesReceived||0;
        const L = byId.get(r.localCandidateId)||{}, R = byId.get(r.remoteCandidateId)||{};
        res.pairs.push(L.candidateType+'/'+L.protocol+' -> '+R.candidateType+'/'+(R.protocol||'')+' '+(R.address||R.ip||'')+':'+(R.port||''));
      }
    });
  }
  res.pairs = [...new Set(res.pairs)];
  return res;
}`;

async function join(page, url, name){
  await page.goto(url, { waitUntil:'load', timeout:30000 });
  await page.waitForTimeout(500);
  await page.fill('#name', name);
  await page.click('#enableDevices');
  await page.waitForSelector('#join:not([disabled])', { timeout:15000 });
  await page.click('#join');
  await page.waitForSelector('#call:not([hidden])', { timeout:25000 });
}

(async () => {
  const browser = await chromium.launch({ headless:true,
    args:['--use-fake-device-for-media-stream','--use-fake-ui-for-media-stream','--autoplay-policy=no-user-gesture-required'] });
  const out = {};
  try {
    const cA = await browser.newContext({ viewport:{width:1280,height:720}, permissions:['microphone','camera'], ignoreHTTPSErrors:true });
    await cA.addInitScript(INIT); const A = await cA.newPage();
    const eA=[]; A.on('pageerror',x=>eA.push(x.message)); A.on('console',m=>{if(m.type()==='error')eA.push(m.text());});
    await join(A, BASE, 'Алиса');
    const room = A.url(); out.room = room;

    const cB = await browser.newContext({ viewport:{width:1280,height:720}, permissions:['microphone','camera'], ignoreHTTPSErrors:true });
    await cB.addInitScript(INIT); const B = await cB.newPage();
    const eB=[]; B.on('pageerror',x=>eB.push(x.message)); B.on('console',m=>{if(m.type()==='error')eB.push(m.text());});
    await join(B, room, 'Боря');

    await A.waitForTimeout(6000);
    out.A = await A.evaluate(eval('('+STATS+')'));
    out.B = await B.evaluate(eval('('+STATS+')'));
    out.counts = { A: await A.textContent('#count').catch(()=>'?'), B: await B.textContent('#count').catch(()=>'?') };
    out.errs = { A:eA.slice(0,6), B:eB.slice(0,6) };
    await A.screenshot({ path: `${SHOTS}/15-REAL-audiolgbt-call.png` });
  } catch(e){ out.FATAL = e.message; }
  finally {
    console.log('ROOM', out.room);
    console.log('A:', JSON.stringify(out.A));
    console.log('B:', JSON.stringify(out.B));
    console.log('counts', JSON.stringify(out.counts));
    console.log('errs', JSON.stringify(out.errs));
    if (out.FATAL) console.log('FATAL', out.FATAL);
    const ok = out.A && out.B && out.A.framesDecoded>0 && out.B.framesDecoded>0;
    console.log(ok ? '\n=== REAL CALL OK: media decoded both ways through audio.lgbt ===' : '\n=== REAL CALL FAILED ===');
    await browser.close();
  }
})();
