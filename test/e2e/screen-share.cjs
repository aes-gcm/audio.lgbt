const {chromium}=require('playwright');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const base=process.env.BASE_URL||'http://127.0.0.1:8097';
const wait=async(page,predicate,arg)=>{
 const end=Date.now()+30000;
 while(Date.now()<end){const result=await page.evaluate(predicate,arg);if(result)return result;await new Promise(resolve=>setTimeout(resolve,150));}
 console.error(await page.evaluate(async()=>({
  tracks:[...__z.trackMap.values()],
  peers:[...__z.peers.values()].map(p=>({self:p.self,tiles:[...p.vtiles].map(([source,t])=>({source,width:t.video.videoWidth,height:t.video.videoHeight})),screenAudio:!!p.screenAudioEl?.srcObject})),
  stats:[...(await __z.sub().getStats()).values()].filter(s=>s.type==='inbound-rtp').map(s=>({kind:s.kind,mid:s.mid,width:s.frameWidth,frames:s.framesDecoded,energy:s.totalAudioEnergy})),
 })));
 throw Error('Timed out: '+predicate.toString());
};
const shot=async(p,name)=>{if(process.env.CAPTURE==='1'){fs.mkdirSync('.impeccable/review',{recursive:true});await p.screenshot({path:'.impeccable/review/'+name+'.png',fullPage:true});}};
(async()=>{
 const browser=await chromium.launch({channel:'chrome',headless:true,args:['--use-fake-device-for-media-stream=fps=60','--use-fake-ui-for-media-stream','--autoplay-policy=no-user-gesture-required']});
 try{
 const context=await browser.newContext({permissions:['camera','microphone'],viewport:{width:1440,height:1000}});
 await context.addInitScript(()=>{
  window.displayCalls=[];window.displayStreams=[];window.displayContexts=[];window.cancelDisplay=false;window.noDisplayAudio=false;window.delayDisplay=false;
  navigator.mediaDevices.getDisplayMedia=async options=>{
   displayCalls.push(options);
   if(cancelDisplay){cancelDisplay=false;throw new DOMException('cancel','NotAllowedError');}
   if(delayDisplay)await new Promise(resolve=>window.finishDisplay=resolve);
   // Deterministic display fixture: actual canvas MediaStream, real WebRTC/E2EE afterwards.
   const canvas=document.createElement('canvas');canvas.width=1920;canvas.height=1080;
   const draw=canvas.getContext('2d');draw.scale(.5,.5);let frame=0;
   const paint=()=>{draw.fillStyle='#172027';draw.fillRect(0,0,3840,2160);draw.fillStyle='#f4f4f4';draw.font='80px sans-serif';draw.fillText('audio.lgbt · Демонстрация',160,240);draw.font='48px sans-serif';draw.fillText('Текст, видео и звук',160,340);draw.fillStyle='#75b8b0';draw.fillRect(160+(frame++%100)*24,650,300,300);};paint();
   const captured=canvas.captureStream(60);const timer=setInterval(paint,1000/60);const videoTrack=captured.getVideoTracks()[0],stopVideo=videoTrack.stop.bind(videoTrack);videoTrack.stop=()=>{clearInterval(timer);stopVideo();};

   if(options.audio&&!noDisplayAudio){
    const ctx=new AudioContext({sampleRate:48000});await ctx.resume();const dest=ctx.createMediaStreamDestination();const merger=ctx.createChannelMerger(2);
    for(const [i,f] of [440,880].entries()){const osc=ctx.createOscillator();osc.frequency.value=f;const gain=ctx.createGain();gain.gain.value=.1;osc.connect(gain);gain.connect(merger,0,i);osc.start();}
    merger.connect(dest);captured.addTrack(dest.stream.getAudioTracks()[0]);displayContexts.push(ctx);
   }
   displayStreams.push(captured);return captured;
  };
 });
 const a=await context.newPage(), b=await context.newPage();const errors=[];
 for(const p of [a,b]){p.setDefaultTimeout(30000);p.on('pageerror',e=>errors.push(e.message));p.on('console',m=>{if(m.type()==='error')console.error(m.text())});}
 await a.goto(base+'/?debug=1');await a.click('#createRoom');await a.waitForSelector('#join:not([disabled])');const room=a.url();await a.fill('#name','Аня');if(await a.getAttribute('#pjCam','aria-pressed')==='true')await a.click('#pjCam');await a.click('#join');await a.waitForSelector('#call:not([hidden])');
 await b.goto(room);await b.waitForSelector('#join:not([disabled])');await b.fill('#name','Марк');if(await b.getAttribute('#pjCam','aria-pressed')==='true')await b.click('#pjCam');await b.click('#join');await b.waitForSelector('#call:not([hidden])');
 await a.click('#share');await a.waitForSelector('#shareDialog[open]');await shot(a,'share-setup-desktop');
 await a.setViewportSize({width:390,height:844});await shot(a,'share-setup-mobile');assert.equal(await a.evaluate(()=>document.documentElement.scrollWidth>innerWidth),false);await a.setViewportSize({width:1440,height:1000});
 await a.selectOption('#shareQuality','1080');await a.selectOption('#shareFps','60');await a.selectOption('#shareContent','detail');await a.click('#shareStart');await a.waitForSelector('#shareDialog:not([open])',{state:'attached'}).catch(async e=>{console.error('Share failure',await a.locator('#shareError').textContent(),await a.evaluate(()=>displayCalls));throw e});
 await wait(b,()=>{const p=[...__z.peers.values()].find(p=>!p.self);return p?.vtiles.get('screen')?.video.videoWidth>=1920&&p?.screenAudioEl?.srcObject?.getAudioTracks().length===1;});
 const received=await wait(b,async()=>{const values=[...(await __z.sub().getStats()).values()];const audio=values.filter(s=>s.type==='inbound-rtp'&&s.kind==='audio'&&s.totalAudioEnergy>0);const video=values.filter(s=>s.type==='inbound-rtp'&&s.kind==='video'&&s.frameWidth>=1920&&s.framesDecoded>5);return audio.length===2&&video.length?{audioTracks:audio.length,width:video[0].frameWidth,height:video[0].frameHeight,frames:video[0].framesDecoded}:false;});
 const params=await a.evaluate(()=>{const s=__z.pub().getSenders().find(s=>s.track?.getSettings().width>=1920);return {p:s.getParameters(),settings:s.track.getSettings(),answer:__z.pub().remoteDescription.sdp};});
 assert.equal(params.p.encodings[0].maxFramerate,60);assert.equal(params.p.encodings[0].maxBitrate,9600000);assert.equal(params.settings.frameRate,60);assert.ok(params.answer.split('\r\n').some(line=>line.startsWith('a=fmtp:')&&line.includes(';stereo=1')&&line.includes('sprop-stereo=1')),'display Opus negotiates stereo');
 // Audio volume is separate from the microphone, and survives fullscreen.
 const volume=b.locator('[data-source="screen"] .screen-volume input');await volume.fill('25');await volume.dispatchEvent('input');
 assert.equal(await b.evaluate(()=>[...__z.peers.values()].find(p=>!p.self).screenAudioEl.volume),.25);
 assert.equal(await b.evaluate(()=>[...__z.peers.values()].find(p=>!p.self).audioEl.volume),1);
 assert.equal(await b.evaluate(()=>{const shared=document.querySelector('[data-source="screen"]').getBoundingClientRect();return [...document.querySelectorAll('[data-source="cam"]')].every(tile=>tile.getBoundingClientRect().top>=shared.bottom);}),true,'camera rows must not overlap the shared screen');
 await shot(b,'share-viewer-desktop');await b.locator('[data-source="screen"] .screen-expand').click();await wait(b,()=>!!document.fullscreenElement||!!document.querySelector('.screen-expanded'));await shot(b,'share-fullscreen-desktop');
 await b.locator('[data-source="screen"] .screen-expand').click();await wait(b,()=>!document.fullscreenElement&&!document.querySelector('.screen-expanded'));
 // Force the standards fallback to cover mobile/embedded browsers without Fullscreen.
 await b.evaluate(()=>Object.defineProperty(document,'fullscreenEnabled',{configurable:true,value:false}));
 await b.setViewportSize({width:390,height:844});await b.locator('[data-source="screen"] .screen-expand').click();await wait(b,()=>!!document.querySelector('.screen-expanded'));await shot(b,'share-fullscreen-mobile');await b.keyboard.press('Escape');await wait(b,()=>!document.querySelector('.screen-expanded'));await b.setViewportSize({width:1440,height:1000});
 // Lower quality and disable display audio without taking down the mic.
 await a.click('#share');await a.selectOption('#shareQuality','720');await a.selectOption('#shareFps','30');await a.uncheck('#shareAudio');await a.click('#shareStart');await a.waitForSelector('#shareDialog:not([open])',{state:'attached'}).catch(async e=>{console.error('Share failure',await a.locator('#shareError').textContent(),await a.evaluate(()=>displayCalls));throw e});
 await wait(b,()=>[...__z.peers.values()].find(p=>!p.self)?.screenAudioEl.muted);
 await wait(a,()=>displayStreams[0].getVideoTracks()[0].getSettings().height===720);
 // Cancelling a replacement must preserve the existing capture.
 await a.click('#share');await a.evaluate(()=>cancelDisplay=true);await a.click('#shareSource');await a.waitForSelector('#shareSource:not([disabled])');assert.equal(await a.evaluate(()=>displayStreams[0].getVideoTracks()[0].readyState),'live');await a.click('#shareClose');
 // Re-enable previously captured audio, independently of a muted microphone.
 await a.click('#mic');await a.click('#share');await a.check('#shareAudio');await a.click('#shareStart');await wait(b,()=>![...__z.peers.values()].find(p=>!p.self).screenAudioEl.muted);
 assert.equal(await a.locator('#mic').getAttribute('aria-pressed'),'false');
 // Late join receives the separate screen and audio tracks.
 const c=await context.newPage();await c.goto(room);await c.waitForSelector('#join:not([disabled])');if(await c.getAttribute('#pjCam','aria-pressed')==='true')await c.click('#pjCam');await c.click('#join');await wait(c,()=>[...__z.peers.values()].some(p=>p.vtiles.get('screen')?.video.videoWidth>0&&p.screenAudioEl?.srcObject));await c.click('#leave');
 await a.click('#share');await a.click('#shareStop');await wait(a,()=>displayStreams[0].getTracks().every(t=>t.readyState==='ended'));
 await wait(b,()=>!document.querySelector('[data-source="screen"]'));
 await wait(b,()=>![...__z.trackMap.values()].some(t=>t.source==='screen'||t.source==='screen-audio'));
 // Repeat start/stop catches stale receiver/transceiver associations.
 await a.click('#share');await a.click('#shareStart');await wait(b,()=>!!document.querySelector('[data-source="screen"] video')?.videoWidth);await a.click('#share');await a.click('#shareStop');
 // A source with no audio remains a valid visual share and must say so.
 await a.evaluate(()=>noDisplayAudio=true);await a.click('#share');await a.click('#shareStart');await wait(a,()=>document.querySelector('[data-source="screen"] .screen-quality')?.textContent.includes('без звука'));
 await a.click('#share');await a.click('#shareStop');
 // Leaving while the browser's source picker is pending must stop its eventual stream.
 await a.evaluate(()=>delayDisplay=true);await a.click('#share');await a.click('#shareStart');await a.click('#shareClose');await a.click('#leave');await a.evaluate(()=>finishDisplay());await wait(a,()=>displayStreams.at(-1)?.getTracks().every(t=>t.readyState==='ended'));
 await b.click('#leave');assert.deepEqual(errors,[]);
 console.log(JSON.stringify({pass:true,...received,quality:params.settings,tests:'two audio tracks, fullscreen, live quality/audio, cancel, late join, repeat, no audio, leave while picker pending'}));
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1});
