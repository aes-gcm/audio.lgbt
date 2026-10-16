const {chromium}=require('playwright');
const assert=require('node:assert/strict');
const base=process.env.BASE_URL||'http://127.0.0.1:8081';
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
(async()=>{
 const browser=await chromium.launch({channel:'chrome',headless:true,args:['--use-fake-device-for-media-stream','--use-fake-ui-for-media-stream','--autoplay-policy=no-user-gesture-required']});
 try{
 const ctx=await browser.newContext({permissions:['camera','microphone','clipboard-read','clipboard-write'],viewport:{width:1440,height:1000}});
 await ctx.addInitScript(()=>{window.testContexts=[];window.testOscillators=[];const AC=AudioContext;window.AudioContext=class extends AC{constructor(...args){super(...args);testContexts.push(this);}createOscillator(){const o=super.createOscillator();testOscillators.push(o);return o;}};});
 const a=await ctx.newPage(),b=await ctx.newPage(),errors=[];
 for(const p of[a,b]){p.on('pageerror',e=>errors.push(e.message));p.setDefaultTimeout(15000);}
 await a.goto(base+'/?debug=1');await a.click('#createRoom');await a.waitForSelector('#join:not([disabled])');const room=a.url();await a.click('#join');await a.waitForSelector('#call:not([hidden])');
 await b.goto(room);await b.waitForSelector('#join:not([disabled])');await b.click('#join');await a.waitForFunction(()=>__z.peers.size===2);
 assert.equal(await a.locator('#hand,#react,#reactBar').count(),0);
 const stage=()=>a.locator('#stage').boundingBox();const original=await stage();
 for(const [button,view] of [['#peopleBtn','people'],['#settingsBtn','settings'],['#copyInvite','invite']]){
  for(let i=0;i<3;i++){await a.click(button);assert.equal(await a.locator('#callDialog').getAttribute('data-view'),view);assert.deepEqual(await stage(),original);assert.equal(await a.locator('#panel').isVisible(),false);await a.keyboard.press('Escape');await a.waitForFunction(()=>!document.querySelector('#callDialog').open);assert.equal(await a.evaluate(()=>document.activeElement.id),button.slice(1));}
 }
 await a.click('#copyInvite');const invitation=await a.inputValue('#roomInvite');assert.match(invitation,/\/#[-\w]{22}$/);assert.equal(new URL(invitation).search,'');await a.click('#copyRoomInvite');assert.equal(await a.evaluate(()=>navigator.clipboard.readText()),invitation);
 await a.screenshot({path:'output/playwright/controls-invite.png'});await a.click('#callDialogClose');
 await a.click('#peopleBtn');const volume=a.locator('#peopleList input[type=range]');assert.equal(await volume.inputValue(),'100');assert.equal(await volume.getAttribute('max'),'200');
 await volume.evaluate(el=>{el.value='200';el.dispatchEvent(new Event('input',{bubbles:true}));});
 await a.waitForFunction(()=>[...__z.peers.values()].some(p=>p.gain&&p.gain.gain.value===2));
 const ratio=await a.evaluate(async()=>{
  const p=[...__z.peers.values()].find(p=>!p.self),ac=p.gain.context,before=ac.createAnalyser(),after=ac.createAnalyser();p.gainSrc.connect(before);p.gain.connect(after);
  await new Promise(r=>setTimeout(r,600));let ratios=[];
  for(let i=0;i<5;i++){const x=new Float32Array(2048),y=new Float32Array(2048);before.getFloatTimeDomainData(x);after.getFloatTimeDomainData(y);const rms=z=>Math.sqrt(z.reduce((s,v)=>s+v*v,0)/z.length);if(rms(x)>.001)ratios.push(rms(y)/rms(x));await new Promise(r=>setTimeout(r,100));}
  p.gainSrc.disconnect(before);p.gain.disconnect(after);return ratios.reduce((s,v)=>s+v,0)/ratios.length;
 });assert.ok(Math.abs(ratio-2)<.08,'Actual decoded participant amplification: '+ratio);
 await a.screenshot({path:'output/playwright/controls-people.png'});
 await volume.evaluate(el=>{el.value='100';el.dispatchEvent(new Event('input',{bubbles:true}));});assert.equal(await a.evaluate(()=>[...__z.peers.values()].find(p=>!p.self).audioEl.muted),false);
 await a.click('#callDialogClose');await a.click('#settingsBtn');
 if(await a.locator('#callOutput').isEnabled()){const select=a.locator('#callOutput');await a.waitForFunction(()=>document.querySelector('#callOutput').options.length>1);const output=await select.locator('option').nth(1).getAttribute('value');await select.selectOption(output);await a.waitForSelector('#callOutput:not([disabled])');}
 const senders=await a.evaluate(()=>__z.pub().getSenders().map(s=>s.track?.id));
 await a.click('#testOutput');await a.waitForFunction(()=>testOscillators.length>=3);
 const tone=await a.evaluate(async()=>{const o=testOscillators.at(-3),ac=o.context,analyser=ac.createAnalyser();o.connect(analyser);await new Promise(r=>setTimeout(r,100));const data=new Float32Array(2048);analyser.getFloatTimeDomainData(data);o.disconnect(analyser);return{rms:Math.sqrt(data.reduce((s,v)=>s+v*v,0)/data.length),sink:ac.sinkId,chosen:document.querySelector('#callOutput').value};});
 assert.ok(tone.rms>.1,'Real oscillator samples');assert.equal(tone.sink,tone.chosen);assert.deepEqual(await a.evaluate(()=>__z.pub().getSenders().map(s=>s.track?.id)),senders,'Test sound must not change outgoing tracks');
 await a.waitForFunction(()=>document.querySelector('#testOutput').getAttribute('aria-pressed')==='false');
 await a.click('#testOutput');await a.click('#callDialogClose');await a.waitForFunction(()=>document.querySelector('#testOutput').getAttribute('aria-pressed')==='false');
 await a.click('#chatToggle');assert.equal(await a.locator('#panel').isVisible(),true);const chatStage=await stage();await a.click('#peopleBtn');assert.deepEqual(await stage(),chatStage);await a.click('#callDialogClose');assert.equal(await a.locator('#panel').isVisible(),true);await a.click('#panelClose');
 await a.setViewportSize({width:390,height:844});
 for(const [button,label] of[['#settingsBtn','settings'],['#peopleBtn','people'],['#copyInvite','invite']]){await a.click(button);assert.equal(await a.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);const bounds=await a.locator('#callDialog').boundingBox();assert.ok(bounds.x>=0&&bounds.y>=0&&bounds.x+bounds.width<=390&&bounds.y+bounds.height<=844);await a.screenshot({path:'output/playwright/controls-mobile-'+label+'.png'});await a.keyboard.press('Escape');await a.waitForFunction(()=>!document.querySelector('#callDialog').open);}
 await a.click('#leave');await b.click('#leave');assert.deepEqual(errors,[]);
 console.log(JSON.stringify({result:'PASS overlay geometry/focus/Escape/copy/mobile, chat sidebar, removed hand/reactions, real participant boost and output tone/sink/cleanup',gainRatio:ratio,tone}));
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1});
