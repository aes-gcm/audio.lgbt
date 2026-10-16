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
  window.__e2ee = { hasTransform: 'RTCRtpScriptTransform' in window, hasLegacy: !!(window.RTCRtpSender && 'createEncodedStreams' in RTCRtpSender.prototype) };
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
  return { framesDecoded, audioPktRecv, inVideo, inAudio, outVideo, outAudio, pcs:(window.__pcs||[]).length, gum: window.__gum, e2ee: window.__e2ee };
}`;

async function prep(ctx, name) {
  const page = await ctx.newPage();
  const errs = [];
  page.on('pageerror', e => errs.push('PAGEERROR ' + e.message));
  page.on('console', m => { if (m.type()==='error') errs.push('CONSOLE.err ' + m.text()); });
  page.__errs = errs;
  return page;
}

async function joinFull(page, url, name) {
  await page.goto(url, { waitUntil: 'load' });
  await page.waitForTimeout(400);
  await page.fill('#name', name);
  await page.click('#enableDevices');
  await page.waitForSelector('#join:not([disabled])', { timeout: 15000 });
  await page.click('#join');
  await page.waitForSelector('#call:not([hidden])', { timeout: 20000 });
}

(async () => {
  const browser = await chromium.launch({
    headless: true,
    args: ['--use-fake-device-for-media-stream','--use-fake-ui-for-media-stream','--autoplay-policy=no-user-gesture-required'],
  });
  const out = {};
  try {
    const ctxA = await browser.newContext({ viewport:{width:1280,height:800}, permissions:['microphone','camera'] });
    await ctxA.addInitScript(INIT);
    const A = await prep(ctxA, 'A');
    await joinFull(A, BASE, 'Алиса');
    const room = A.url();
    out.room = room;

    const ctxB = await browser.newContext({ viewport:{width:1280,height:800}, permissions:['microphone','camera'] });
    await ctxB.addInitScript(INIT);
    const B = await prep(ctxB, 'B');
    await joinFull(B, room, 'Боря');

    // let media flow
    await A.waitForTimeout(5000);

    out.A = await A.evaluate(eval('(' + STATS + ')'));
    out.B = await B.evaluate(eval('(' + STATS + ')'));
    out.A_count = await A.textContent('#count').catch(()=>'?');
    out.B_count = await B.textContent('#count').catch(()=>'?');
    out.A_tiles = await A.$$eval('#stage video', v => v.length).catch(()=>-1);
    out.B_tiles = await B.$$eval('#stage video', v => v.length).catch(()=>-1);

    await A.screenshot({ path: `${SHOTS}/12-newdesign-call-A.png` });
    await B.screenshot({ path: `${SHOTS}/13-newdesign-call-B.png` });

    out.A_errs = A.__errs.slice(0,8);
    out.B_errs = B.__errs.slice(0,8);
  } catch (e) {
    out.FATAL = e.message;
  } finally {
    console.log(JSON.stringify(out, null, 2));
    await browser.close();
  }
})();
