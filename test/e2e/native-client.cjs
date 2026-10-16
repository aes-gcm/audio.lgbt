// Real Chrome + the native Pion process. Frames are decrypted by the native
// engine and returned through its normal publish path, then decoded by Chrome.
const {chromium}=require('playwright');
const {spawn}=require('node:child_process');
const readline=require('node:readline');
const assert=require('node:assert/strict');
const base=process.env.BASE_URL||'http://127.0.0.1:8097';
const engine=process.env.NATIVE_ENGINE||'./dist/audio-engine-mac';
const wait=async(fn,label)=>{const end=Date.now()+25000;while(Date.now()<end){const x=await fn();if(x)return x;await new Promise(r=>setTimeout(r,100));}throw Error('Timeout: '+label)};
(async()=>{
 const browser=await chromium.launch({channel:'chrome',headless:true,args:['--use-fake-device-for-media-stream','--use-fake-ui-for-media-stream','--autoplay-policy=no-user-gesture-required']});
 const child=spawn(engine,[],{stdio:['pipe','pipe','pipe'],windowsHide:true});
 let welcome, nativeAudio=0,nativeVideo=0,nativeChat, transportStats, errors=[];
 let closing=false;const send=m=>{if(!closing&&!child.stdin.destroyed)child.stdin.write(JSON.stringify(m)+'\n');};
 child.stdin.on('error',e=>{if(!closing)errors.push(e.message)});
 readline.createInterface({input:child.stdout}).on('line',line=>{
  const m=JSON.parse(line);if(m.type==='error')errors.push(m.error);if(m.type==='welcome')welcome=m;
  if(m.type==='chat')nativeChat=m.text;
  if(m.type==='stats'){transportStats=m.connections;if(process.env.TRACE_STATS==='1')console.log(JSON.stringify({stats:transportStats}));}
  if(m.type==='frame'){
   if(m.source==='mic')nativeAudio++;else nativeVideo++;
   send({type:'media',source:m.source,data:m.data,duration:m.source==='mic'?20000:33333});
  }
 });
 child.stderr.on('data',d=>process.stderr.write(d));
 try{
  const context=await browser.newContext({permissions:['camera','microphone']});const page=await context.newPage();
  await page.goto(base+'/?debug=1');await page.click('#createRoom');
  if(process.env.AUTH_USER){await page.fill('#accountName',process.env.AUTH_USER);await page.fill('#accountPassword',process.env.AUTH_PASSWORD);await page.click('#accountSubmit');}
  await page.waitForSelector('#join:not([disabled])');
  const key=new URL(page.url()).hash.slice(1);
  if(process.env.NATIVE_CREATES==='1'){
   const authToken=(await context.cookies()).find(c=>c.name==='audio_session')?.value;
   send({type:'start',server:base+'/',key,name:'Приложение',relay:false,authToken});await wait(()=>welcome,'authenticated native creates room');
  }
  await page.fill('#name','Браузер');await page.click('#join');await page.waitForSelector('#call:not([hidden])');
  if(process.env.NATIVE_CREATES!=='1'){send({type:'start',server:base+'/',key,name:'Приложение',relay:false});await wait(()=>welcome,'native guest welcome');}
  await wait(()=>nativeAudio>10&&nativeVideo>5,'native decrypts browser Opus/VP8');
  const received=await wait(async()=>page.evaluate(async()=>{const stats=await __z.sub().getStats();const values=[...stats.values()];const audio=values.find(s=>s.type==='inbound-rtp'&&s.kind==='audio');const video=values.find(s=>s.type==='inbound-rtp'&&s.kind==='video');return audio?.totalAudioEnergy>0&&video?.framesDecoded>5?{audioEnergy:audio.totalAudioEnergy,videoDecoded:video.framesDecoded}:false;}),'browser decodes native encrypted Opus/VP8');
  send({type:'speaking',speaking:true});await page.waitForFunction(id=>__z.peers.get(id)?.vtiles.get('cam').tile.classList.contains('speaking'),welcome.selfId);
  send({type:'speaking',speaking:false});await page.waitForFunction(id=>!__z.peers.get(id)?.vtiles.get('cam').tile.classList.contains('speaking'),welcome.selfId);
  send({type:'chat',text:'Привет из Windows-клиента'});await page.waitForFunction(()=>document.querySelector('#chatLog')?.textContent.includes('Привет из Windows-клиента'));
  await page.evaluate(()=>{document.querySelector('#chatInput').value='Привет из браузера';document.querySelector('#chatForm').requestSubmit();});await wait(()=>nativeChat==='Привет из браузера','native decrypts browser chat');
  if(process.env.TEST_RELAY==='1'){
   const before=welcome.selfId;const first=await page.evaluate(async()=>[...await __z.sub().getStats().then(s=>s.values())].find(s=>s.type==='inbound-rtp'&&s.kind==='video').framesDecoded);
   send({type:'transport',relay:true});
   await wait(async()=>{send({type:'stats'});return ['pub','sub'].every(t=>transportStats?.[t]?.state==='connected'&&transportStats[t].candidateType==='relay'&&(transportStats[t].relayProtocol==='tls'||transportStats[t].server?.startsWith('turns:')))},'confirmed TLS candidate pairs');
   console.log(JSON.stringify({tls:transportStats}));
   await wait(async()=>page.evaluate(async n=>[...(await __z.sub().getStats()).values()].some(s=>s.type==='inbound-rtp'&&s.kind==='video'&&s.framesDecoded>n+30),first),'TLS media');
   transportStats=null;send({type:'transport',relay:false});
   await wait(async()=>{send({type:'stats'});return ['pub','sub'].every(t=>transportStats?.[t]?.state==='connected'&&transportStats[t].candidateType!=='relay')},'return to direct Auto');
   console.log(JSON.stringify({auto:transportStats}));assert.equal(welcome.selfId,before);
  }
  assert.deepEqual(errors,[]);console.log(JSON.stringify({pass:true,nativeAudio,nativeVideo,...received,chat:'bidirectional encrypted'}));
 }finally{closing=true;child.stdin.end();await browser.close();const timer=setTimeout(()=>child.kill(),2000);timer.unref();}
})().catch(e=>{console.error(e);process.exitCode=1});
