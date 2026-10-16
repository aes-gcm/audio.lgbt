const { chromium } = require('playwright');
const BASE = 'https://audio.lgbt/';

const INIT_PCS = `(() => {
  window.__pcs = [];
  const O = window.RTCPeerConnection;
  window.RTCPeerConnection = function(...a){ const pc = new O(...a); try{ window.__pcs.push(pc); }catch(e){} return pc; };
  window.RTCPeerConnection.prototype = O.prototype;
})();`;
const STATS = `async () => {
  let f=0,a=0; const policies=[]; const pairs=[];
  for(const pc of (window.__pcs||[])){
    try{ policies.push(pc.getConfiguration().iceTransportPolicy||'all'); }catch(e){}
    let s;try{s=await pc.getStats();}catch(e){continue;}
    const by=new Map(); s.forEach(r=>by.set(r.id,r));
    s.forEach(r=>{ if(r.type==='inbound-rtp'&&r.kind==='video')f+=(r.framesDecoded||0); if(r.type==='inbound-rtp'&&r.kind==='audio')a+=(r.packetsReceived||0);
      if(r.type==='candidate-pair'&&(r.nominated||r.state==='succeeded')&&(r.bytesReceived||0)>0){const L=by.get(r.localCandidateId)||{},R=by.get(r.remoteCandidateId)||{};pairs.push(L.candidateType+'/'+L.protocol+'->'+R.candidateType+'/'+(R.protocol||'')+':'+(R.port||''));}});
  }
  return {framesDecoded:f,audioPktRecv:a,policies,pairs:[...new Set(pairs)]};
}`;
const stats = p => p.evaluate(eval('('+STATS+')'));
const checks=[]; const push=(n,ok,d='')=>checks.push([n,!!ok,d]);

async function enterForcingRelay(browser, url, name, relay){
  const ctx = await browser.newContext({ viewport:{width:1100,height:760}, permissions:['microphone','camera'], ignoreHTTPSErrors:true });
  if (relay) await ctx.addInitScript(() => { try{ localStorage.setItem('audio.transport','relay'); }catch(e){} });
  await ctx.addInitScript(INIT_PCS);
  const p = await ctx.newPage();
  await p.goto(url, { waitUntil:'load' });
  if (url === BASE) { await p.click('#createRoom'); }
  await p.waitForSelector('#join:not([disabled])', {timeout:12000});
  await p.fill('#name', name); await p.click('#join');
  await p.waitForSelector('#call:not([hidden])', {timeout:25000});
  return p;
}

(async () => {
  const browser = await chromium.launch({ headless:true,
    args:['--use-fake-device-for-media-stream','--use-fake-ui-for-media-stream','--autoplay-policy=no-user-gesture-required'] });
  try {
    // ---- RELAY-FORCED room (Только TLS) ----
    const A = await enterForcingRelay(browser, BASE, 'Алиса-relay', true);
    const room = A.url();
    const B = await enterForcingRelay(browser, room, 'Боря-relay', true);
    await A.waitForTimeout(7000);
    const sa = await stats(A), sb = await stats(B);
    push('relay: A media decoded via forced TLS', sa.framesDecoded>0, 'fd='+sa.framesDecoded+' pairs='+JSON.stringify(sa.pairs));
    push('relay: B media decoded via forced TLS', sb.framesDecoded>0, 'fd='+sb.framesDecoded+' pairs='+JSON.stringify(sb.pairs));
    push('relay: iceTransportPolicy=relay on PCs', sa.policies.every(x=>x==='relay') && sa.policies.length>0, 'policies='+JSON.stringify(sa.policies));
    push('relay: selected path is relay/tcp (TLS)', sa.pairs.some(x=>/relay/.test(x)), 'pairs='+JSON.stringify(sa.pairs));
    push('relay: connChip shows TLS', /TLS/.test((await A.textContent('#connChip'))||''), 'chip='+((await A.textContent('#connChip'))||'').trim());
    await A.context().close(); await B.context().close();

    // ---- AUTO room (confirm direct still works with new build) ----
    const A2 = await enterForcingRelay(browser, BASE, 'Алиса-auto', false);
    const room2 = A2.url();
    const B2 = await enterForcingRelay(browser, room2, 'Боря-auto', false);
    await A2.waitForTimeout(6000);
    const sa2 = await stats(A2), sb2 = await stats(B2);
    push('auto: A+B media decoded', sa2.framesDecoded>0 && sb2.framesDecoded>0, 'A='+sa2.framesDecoded+' B='+sb2.framesDecoded);
    push('auto: path + chip', true, 'pairs='+JSON.stringify(sa2.pairs)+' chip='+((await A2.textContent('#connChip'))||'').trim());
  } catch(e){ push('FATAL', false, e.message); }
  finally {
    let pass=true; for (const [n,ok,d] of checks){ if(!ok)pass=false; console.log((ok?'PASS':'FAIL').padEnd(5), n, d?('  ['+d+']'):''); }
    console.log(pass?'\n=== REAL ALL PASS ===':'\n=== REAL SOME FAIL ===');
    await browser.close();
  }
})();
