const { chromium } = require('playwright');
const fs = require('node:fs');
(async()=>{
 const browser=await chromium.launch({channel:'chrome',headless:true,args:['--use-fake-device-for-media-stream','--use-fake-ui-for-media-stream','--autoplay-policy=no-user-gesture-required']});
 const ctx=await browser.newContext({permissions:['camera','microphone']});
 const p=await ctx.newPage(); await p.goto('https://audio.lgbt/?debug=1'); await p.click('#createRoom');await p.waitForSelector('#join:not([disabled])');
 fs.writeFileSync('dist/verification/room.txt',p.url());await p.fill('#name','Chrome · проверка Windows');await p.click('#join');await p.waitForSelector('#call:not([hidden])');
 let stopped=false;process.on('SIGTERM',()=>stopped=true);
 let lastCommand='';
 for(let i=0;i<900&&!stopped;i++){
  const command=fs.existsSync('dist/verification/command.txt')?fs.readFileSync('dist/verification/command.txt','utf8').trim():'';
  if(command && command!==lastCommand){lastCommand=command;
    if(command.startsWith('chat:'))await p.evaluate(text=>{document.querySelector('#chatInput').value=text;document.querySelector('#chatForm').requestSubmit()},command.slice(5));
    if(command==='exit')break;
  }
  const data=await p.evaluate(async()=>({peers:[...__z.peers.values()].map(x=>({id:x.id,name:x.name,mutes:x.mutes})),tracks:[...__z.trackMap.values()],chat:document.querySelector('#chatLog').textContent,stats:[...(await __z.sub().getStats()).values()].filter(s=>s.type==='inbound-rtp').map(s=>({kind:s.kind,mid:s.mid,bytes:s.bytesReceived,width:s.frameWidth,height:s.frameHeight,frames:s.framesDecoded,energy:s.totalAudioEnergy,concealed:s.concealedSamples,received:s.totalSamplesReceived}))}));
  fs.writeFileSync('dist/verification/browser.json',JSON.stringify(data,null,2));await new Promise(r=>setTimeout(r,1000));
 }
 await browser.close();
})().catch(e=>{console.error(e);process.exitCode=1});
