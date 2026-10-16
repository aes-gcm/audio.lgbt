const {chromium}=require('playwright');
const assert=require('node:assert/strict');
const base=process.env.BASE_URL||'http://127.0.0.1:8081';
const capture=async(p,name)=>{if(process.env.CAPTURE==='1'){await p.evaluate(()=>Promise.all(document.getAnimations().filter(a=>a.effect?.getTiming().iterations!==Infinity).map(a=>a.finished.catch(()=>{}))));await p.screenshot({path:`output/playwright/${name}.png`,fullPage:true});}};
const waitAsync = async (page, predicate, arg) => {
 const deadline = Date.now() + 25000;
 while (Date.now() < deadline) {
  const result = await page.evaluate(predicate, arg);
  if (result) return result;
  await new Promise(resolve => setTimeout(resolve, 150));
 }
 throw new Error('Timed out waiting for media statistics');
};
(async()=>{
 const browser=await chromium.launch({channel:'chrome',headless:true,args:['--use-fake-device-for-media-stream','--use-fake-ui-for-media-stream','--autoplay-policy=no-user-gesture-required']});
 try {
 const errors=[];
 const context=await browser.newContext({permissions:['camera','microphone'],viewport:{width:1440,height:1000}});
 await context.addInitScript(()=>{
  window.captured=[];const original=navigator.mediaDevices.getUserMedia.bind(navigator.mediaDevices);
  navigator.mediaDevices.getUserMedia=async c=>{const s=await original(c);captured.push(...s.getTracks());return s;};
 });
 const a=await context.newPage(),b=await context.newPage();
 for(const p of [a,b]){p.on('pageerror',e=>errors.push(e.message));p.setDefaultTimeout(25000);}
 await a.goto(base+'/?debug=1');await a.click('#createRoom');await a.waitForSelector('#join:not([disabled])');
 const room=a.url();await a.click('#join');await a.waitForSelector('#call:not([hidden])');
 await b.goto(room);await b.waitForSelector('#join:not([disabled])');await b.click('#join');
 await waitAsync(b,async()=> (await __z.stats()).audio?.pkts>5);
 await a.click('#settingsBtn');await a.waitForFunction(()=>document.querySelector('#callMic').options.length>1);
 await capture(a,'settings-desktop');
 const setRange=async(p,id,value)=>p.locator(id).evaluate((el,v)=>{el.value=v;el.dispatchEvent(new Event('input',{bubbles:true}));},String(value));
 // Observe outgoing audio energy, not just the slider's text.
 const energy=()=>waitAsync(b,async()=>{const stats=await __z.sub()?.getStats();const r=stats&&[...stats.values()].find(s=>s.type==='inbound-rtp'&&s.kind==='audio'&&s.totalSamplesDuration>0);return r?{energy:r.totalAudioEnergy,duration:r.totalSamplesDuration}:false;});
 const sample=async()=>{const start=await energy();await new Promise(r=>setTimeout(r,700));const end=await energy();return (end.energy-start.energy)/(end.duration-start.duration);};
 await energy();
 await setRange(a,'#micLevel',0);await new Promise(r=>setTimeout(r,500));const silence=await sample();
 await setRange(a,'#micLevel',100);await new Promise(r=>setTimeout(r,500));const normal=await sample();
 assert.ok(normal>silence*5 && normal>0,`microphone gain affects remote audio: silence=${silence}, normal=${normal}`);
 await setRange(a,'#micLevel',150);assert.equal(await a.locator('#micLevelValue').textContent(),'150%');
 await setRange(a,'#speakerLevel',35);
 assert.equal(await a.evaluate(()=>[...__z.peers.values()].find(p=>!p.self).audioEl.volume),.35);
 // Replace capture devices without leaving; failed replacement retains the old track.
 const mic=await a.locator('#callMic option').nth(1).getAttribute('value');
 await a.selectOption('#callMic',mic);await a.waitForSelector('#callMic:not([disabled])');
 assert.equal(await a.locator('#settingsError').isVisible(),false);
 const beforeFailure=await a.evaluate(()=>__z.pub().getSenders().find(s=>s.track?.kind==='audio').track.id);
 await a.locator('#callMic').evaluate(el=>el.add(new Option('Unplugged test device','missing-test-device')));
 await a.selectOption('#callMic','missing-test-device');await a.waitForSelector('#settingsError:not([hidden])');
 assert.equal(await a.inputValue('#callMic'),mic);
 assert.equal(await a.evaluate(()=>__z.pub().getSenders().find(s=>s.track?.kind==='audio').track.id),beforeFailure);
 await a.click('#callDialogClose');await a.click('#cam');await a.waitForSelector('#cam:not([disabled])');await a.click('#settingsBtn');
 const captureCount=await a.evaluate(()=>captured.length);
 const cam=await a.locator('#callCam option').nth(1).getAttribute('value');
 await a.selectOption('#callCam',cam);await a.waitForSelector('#callCam:not([disabled])');
 assert.equal(await a.evaluate(()=>captured.length),captureCount,'muted camera selection does not access hardware');
 assert.equal(await a.locator('#cam').getAttribute('aria-pressed'),'false');
 await a.click('#callDialogClose');await a.click('#cam');await a.waitForSelector('#cam:not([disabled])');await a.click('#settingsBtn');
 assert.equal(await a.locator('#cam').getAttribute('aria-pressed'),'true');
 await a.uncheck('#noiseFilter');await a.waitForSelector('#noiseFilter:not([disabled])');
 assert.equal(await a.locator('#settingsError').isVisible(),false,await a.locator('#settingsError').textContent());
 assert.equal(await a.evaluate(()=>captured.filter(t=>t.kind==='audio'&&t.readyState==='live').at(-1).getSettings().noiseSuppression),false);
 await a.check('#noiseFilter');await a.waitForSelector('#noiseFilter:not([disabled])');
 assert.equal(await a.evaluate(()=>captured.filter(t=>t.kind==='audio'&&t.readyState==='live').at(-1).getSettings().noiseSuppression),true);
 if(await a.locator('#callOutput').isEnabled()) {
  const output=await a.locator('#callOutput option').nth(1).getAttribute('value');
  await a.selectOption('#callOutput',output);await a.waitForSelector('#callOutput:not([disabled])');
  assert.equal(await a.locator('#settingsError').isVisible(),false,await a.locator('#settingsError').textContent());
  assert.equal(await a.evaluate(()=>[...__z.peers.values()].find(p=>!p.self).audioEl.sinkId),output);
 }
 await a.selectOption('#callTheme','light');assert.equal(await a.locator('html').getAttribute('data-theme'),'light');
 await a.selectOption('#callTheme','dark');
 // Production-only relay switch must preserve membership, lock and host rights.
 if(process.env.TEST_RELAY==='1'){
  const ownId=await a.evaluate(()=>[...__z.peers.values()].find(p=>p.self).id);
  await a.click('#callDialogClose');await a.click('#peopleBtn');await a.click('#lockRoom');await a.waitForSelector('#lockState:not([hidden])');await a.click('#callDialogClose');await a.click('#settingsBtn');
  await a.selectOption('#callTransport','relay');await a.waitForSelector('#callTransport:not([disabled])');
  assert.equal(await a.locator('#settingsError').isVisible(),false,await a.locator('#settingsError').textContent());
  assert.equal(await a.inputValue('#callTransport'),'relay');
  assert.equal(await a.evaluate(()=>[...__z.peers.values()].find(p=>p.self).id),ownId);
  assert.equal(await a.evaluate(()=>[...__z.peers.values()].find(p=>p.self).host),true);
  assert.equal(await a.locator('#count').textContent(),'2');
  const before=await b.evaluate(async()=> (await __z.stats()).video.frames);
  await waitAsync(b,async old=>(await __z.stats()).video.frames>old+5,before);
  await a.selectOption('#callTransport','auto');await a.waitForSelector('#callTransport:not([disabled])');
  assert.equal(await a.inputValue('#callTransport'),'auto');
  await a.click('#callDialogClose');await a.click('#peopleBtn');await a.click('#lockRoom');await a.click('#callDialogClose');await a.click('#settingsBtn');
 }
 await b.setViewportSize({width:390,height:844});await b.click('#settingsBtn');await capture(b,'settings-mobile');
 assert.ok(await b.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));
 await b.press('#callMic','Escape');assert.equal(await b.locator('#panel').isVisible(),false);
 assert.equal(await b.evaluate(()=>document.activeElement.id),'settingsBtn');
 await a.evaluate(()=>window.outgoingAudio=__z.pub().getSenders().find(s=>s.track?.kind==='audio').track);
 await b.click('#leave');await a.click('#callDialogClose');await a.click('#leave');
 assert.equal(await a.evaluate(()=>outgoingAudio.readyState),'ended');
 assert.equal(await a.evaluate(()=>captured.every(t=>t.readyState==='ended')),true);
 for(const [name,width,theme] of [['desktop',1440,'dark'],['mobile',390,'dark'],['narrow-light',320,'light']]){
  await a.setViewportSize({width,height:900});await a.goto(base+'/download');
  if(await a.locator('html').getAttribute('data-theme')!==theme)await a.click('#themeToggle');
  await capture(a,'simple-download-'+name);
  assert.ok(await a.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));
  assert.equal(await a.locator('a[href="/"]').count(),1,'only logo returns home');
  await a.getByRole('link',{name:'audio.lgbt — главная'}).click();await capture(a,'simple-home-'+name);
  assert.equal(await a.locator('#howBtn,#landingFeatures,#landingFooter').count(),0);
  assert.ok(await a.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));
 }
 assert.deepEqual(errors,[]);
 console.log('PASS remote microphone gain, output volume, live device replacement, failure rollback, muted selection, noise processing, theme, keyboard settings, cleanup and simplified pages'+(process.env.TEST_RELAY==='1'?', live relay switch with host/lock preserved':''));
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
