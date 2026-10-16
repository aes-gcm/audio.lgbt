const {chromium} = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const {PNG} = require(path.join(path.dirname(require.resolve('playwright-core/package.json')), 'lib/utilsBundle.js'));
const base = process.env.BASE_URL || 'http://127.0.0.1:8081';
const out = 'output/playwright'; fs.mkdirSync(out, {recursive:true});
const key = 'audio.recentRooms.v1';
const colors = [[255,0,0],[0,255,0],[0,0,255],[255,255,0]];
const points = [[.08,.08],[.92,.08],[.08,.92],[.92,.92]];
async function fullFrame(page, selector, name, expectedColors = colors) {
  const video = page.locator(selector);
  await video.waitFor({state:'visible'});
  await page.waitForFunction(s => {const v=document.querySelector(s);return v?.videoWidth>0&&v.readyState>=2}, selector);
  await video.evaluate(v => new Promise(resolve => v.requestVideoFrameCallback(()=>resolve())));
  const geometry = await video.evaluate(v => ({w:v.videoWidth,h:v.videoHeight,fit:getComputedStyle(v).objectFit,transform:getComputedStyle(v).transform}));
  assert.equal(geometry.fit,'contain'); assert.equal(geometry.transform,'none');
  assert.equal(await video.evaluate(v=>{
    const controls=v.parentElement.querySelector('.label, .preview-bottom');if(!controls)return true;
    const a=v.getBoundingClientRect(),b=controls.getBoundingClientRect();
    const h=Math.min(a.width/v.videoWidth,a.height/v.videoHeight)*v.videoHeight;
    return a.top+(a.height+h)/2<=b.top;
  }),true,`${name}: controls must stay below the entire frame`);
  // Check the actual composited element pixels: a cover crop, rotation, mirror,
  // or controls drawn over the bottom corners must fail this assertion.
  const bytes = await video.screenshot(); const png = PNG.sync.read(bytes);
  fs.writeFileSync(`${out}/${name}-video.png`,bytes);
  const scale = Math.min(png.width/geometry.w,png.height/geometry.h);
  for(let i=0;i<4;i++) {
    const x=Math.round((png.width-geometry.w*scale)/2+points[i][0]*geometry.w*scale);
    const y=Math.round((png.height-geometry.h*scale)/2+points[i][1]*geometry.h*scale);
    const offset=(y*png.width+x)*4, actual=[...png.data.subarray(offset,offset+3)];
    assert.ok(actual.every((c,j)=>Math.abs(c-expectedColors[i][j])<90),`${name} corner ${i}: ${actual}; frame=${JSON.stringify(geometry)} screenshot=${png.width}x${png.height}`);
  }
  await page.screenshot({path:`${out}/${name}.png`,fullPage:true});
}
(async()=>{
 const browser=await chromium.launch({channel:'chrome',headless:true,args:['--use-fake-device-for-media-stream','--use-fake-ui-for-media-stream','--autoplay-policy=no-user-gesture-required']});
 const errors=[];
 try {
  const phone=await browser.newContext({viewport:{width:390,height:844},permissions:['camera','microphone']});
  const desktop=await browser.newContext({viewport:{width:1440,height:1000},permissions:['camera','microphone']});
  await phone.addInitScript(()=>{
    const gum=navigator.mediaDevices.getUserMedia.bind(navigator.mediaDevices);
    navigator.mediaDevices.getUserMedia=async constraints=>{
      if(!constraints.video)return gum(constraints);
      const canvas=document.createElement('canvas');canvas.width=720;canvas.height=1280;
      const ctx=canvas.getContext('2d');let tick=0;
      window.cameraFixture=canvas;
      const paint=()=>{
        const w=canvas.width,h=canvas.height;ctx.fillStyle='#444';ctx.fillRect(0,0,w,h);
        ['red','lime','blue','yellow'].forEach((color,i)=>{ctx.fillStyle=color;ctx.fillRect(i%2?w*.84:0,i>1?h*.84:0,w*.16,h*.16)});
        ctx.fillStyle='white';ctx.font='40px sans-serif';ctx.fillText('FRAME '+tick++,w*.25,h*.5);
      };
      // Model capture with rotation already applied to pixels, as a camera
      // source must provide when CVO is not negotiated. TrackGenerator itself
      // does not implement WebRTC's rotation_applied camera-source contract.
      // The SDP assertion below separately verifies that contract is requested.
      window.cameraRotation=0;
      const generator=new MediaStreamTrackGenerator({kind:'video'}), writer=generator.writable.getWriter();
      const pump=async()=>{
        if(generator.readyState!=='live')return;
        paint();const angle=window.cameraRotation, rotated=document.createElement('canvas');
        rotated.width=angle%180?canvas.height:canvas.width;rotated.height=angle%180?canvas.width:canvas.height;
        const output=rotated.getContext('2d');output.translate(rotated.width/2,rotated.height/2);output.rotate(angle*Math.PI/180);output.drawImage(canvas,-canvas.width/2,-canvas.height/2);
        const frame=new VideoFrame(rotated,{timestamp:Math.round(performance.now()*1000)});
        try {await writer.write(frame)} catch {} finally {frame.close()}
        setTimeout(pump,50);
      };pump();
      const stream=new MediaStream([generator]);
      if(constraints.audio)for(const track of (await gum({audio:constraints.audio})).getTracks())stream.addTrack(track);
      return stream;
    };
  });
  const a=await phone.newPage(), b=await desktop.newPage();
  for(const p of [a,b]){p.setDefaultTimeout(25000);p.on('pageerror',e=>errors.push(e.message));}
  await a.goto(base+'/?debug=1');assert.equal(await a.locator('#recentRooms').isVisible(),false);
  await a.click('#createRoom');await a.waitForSelector('#join:not([disabled])');
  assert.equal(await a.evaluate(k=>localStorage.getItem(k),key),null,'preview alone must not add history');
  await fullFrame(a,'#preview','framing-phone-preview');
  const room=a.url();await a.click('#join');await a.waitForSelector('#call:not([hidden])');
  await fullFrame(a,'.tile.self[data-source="cam"] video','framing-phone-alone');
  const solo = await a.locator('.tile.self video').evaluate(v=>{const r=v.getBoundingClientRect();const scale=Math.min(r.width/v.videoWidth,r.height/v.videoHeight);return {width:v.videoWidth*scale,height:v.videoHeight*scale};});
  assert.ok(solo.height>400 && solo.width>220,`Portrait must use the available phone space: ${JSON.stringify(solo)}`);
  const secondPhone=await phone.newPage(); await secondPhone.goto(room); await secondPhone.waitForSelector('#join:not([disabled])'); await secondPhone.click('#join');
  await a.waitForFunction(()=>document.querySelector('#stage').dataset.bothPortrait==='true');
  await fullFrame(a,'.tile.self[data-source="cam"] video','framing-two-portrait-phones');
  assert.ok(await a.locator('.tile.self video').evaluate(v=>{const r=v.getBoundingClientRect();return Math.min(r.width/v.videoWidth,r.height/v.videoHeight)*v.videoWidth>150;}));
  await secondPhone.click('#leave');await secondPhone.close();await a.waitForFunction(()=>document.querySelector('#count').textContent==='1');
  await a.click('#cam');await a.waitForFunction(()=>document.querySelector('#stage').dataset.portrait==='false');
  await a.click('#cam');await a.waitForFunction(()=>document.querySelector('#stage').dataset.portrait==='true');
  await b.goto(room);await b.waitForSelector('#join:not([disabled])');await b.click('#join');
  await a.waitForFunction(()=>__z.pub()?.remoteDescription);
  assert.ok(!await a.evaluate(()=>__z.pub().remoteDescription.sdp.includes('urn:3gpp:video-orientation')),'SFU must not negotiate orientation metadata that its forwarder drops');
  const remote='.tile:not(.self)[data-source="cam"] video', own='.tile.self[data-source="cam"] video';
  await b.waitForFunction(s=>document.querySelector(s)?.videoHeight>document.querySelector(s)?.videoWidth,remote);
  for(const [w,h,label] of [[390,844,'portrait'],[844,390,'landscape'],[320,740,'narrow']]){
    await a.setViewportSize({width:w,height:h});
    await fullFrame(a,own,`framing-phone-${label}`);
    assert.ok(await a.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));
  }
  await fullFrame(b,remote,'framing-desktop-portrait-source');
  await a.evaluate(()=>{cameraFixture.width=1280;cameraFixture.height=720});
  await b.waitForFunction(s=>document.querySelector(s)?.videoWidth>document.querySelector(s)?.videoHeight,remote);
  await a.waitForFunction(s=>document.querySelector(s)?.videoWidth>document.querySelector(s)?.videoHeight,own);
  await a.setViewportSize({width:390,height:844});
  await fullFrame(a,own,'framing-phone-landscape-source');
  await fullFrame(b,remote,'framing-desktop-landscape-source');
  for(const [rotation,order] of [[90,[2,0,3,1]],[180,[3,2,1,0]],[270,[1,3,0,2]]]) {
    await a.evaluate(value=>cameraRotation=value,rotation);
    const expected=order.map(i=>colors[i]);
    for(const [page,selector,label] of [[a,own,'sender'],[b,remote,'receiver']]) {
      console.log(`Checking rotation ${rotation}: ${label}`);
      await page.waitForFunction(({selector,expected})=>{
        const v=document.querySelector(selector);if(!v?.videoWidth)return false;
        const c=document.createElement('canvas');c.width=v.videoWidth;c.height=v.videoHeight;
        const x=c.getContext('2d');x.drawImage(v,0,0);
        return [[.08,.08],[.92,.08],[.08,.92],[.92,.92]].every(([px,py],i)=>{
          const pixel=x.getImageData(Math.round(px*c.width),Math.round(py*c.height),1,1).data;
          return expected[i].every((value,j)=>Math.abs(value-pixel[j])<90);
        });
      },{selector,expected}).catch(async error=>{
        console.log(await page.locator(selector).evaluate(v=>{const c=document.createElement('canvas');c.width=v.videoWidth;c.height=v.videoHeight;const x=c.getContext('2d');x.drawImage(v,0,0);return {width:c.width,height:c.height,pixels:[[.08,.08],[.92,.08],[.08,.92],[.92,.92]].map(([px,py])=>[...x.getImageData(Math.round(px*c.width),Math.round(py*c.height),1,1).data])}}));
        await page.screenshot({path:`${out}/rotation-failure-${label}.png`});throw error;
      });
      await fullFrame(page,selector,`framing-${label}-rotation-${rotation}`,expected);
    }
  }
  assert.equal(await a.evaluate(()=>__z.e2ee),true);assert.equal(await b.evaluate(()=>__z.e2ee),true);
  await b.click('#leave');await a.click('#leave');await a.goto(base);
  assert.equal(await a.locator('.recent-room').count(),1);
  await a.click('.recent-action[aria-label^="Переименовать"]');
  await a.fill('.recent-edit input','Наша комната');await a.press('.recent-edit input','Enter');
  await a.reload();assert.equal(await a.locator('.recent-title').textContent(),'Наша комната');
  await a.screenshot({path:`${out}/history-mobile.png`,fullPage:true});
  await a.click('.recent-join');await a.waitForSelector('#join:not([disabled])');
  assert.equal(new URL(a.url()).hash,new URL(room).hash);
  assert.equal(await a.locator('#call').isVisible(),false,'recent room still shows camera preview before publishing');
  assert.equal(new URL(a.url()).search,'','history must not preserve plaintext/debug query flags');
  await a.click('#join');await a.waitForSelector('#call:not([hidden])');await a.click('#leave');await a.goto(base);
  assert.equal(await a.locator('.recent-room').count(),1,'repeat joins deduplicate');
  await a.evaluate(async()=>{
    const {rememberRoom}=await import('/recent-rooms.js');
    for(let i=0;i<15;i++){const bytes=new Uint8Array(16);bytes[0]=i;const k=btoa(String.fromCharCode(...bytes)).replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'');rememberRoom(k,false)}
  });
  assert.equal(await a.locator('.recent-room').count(),12);
  await a.setViewportSize({width:1440,height:1000});
  await a.screenshot({path:`${out}/history-desktop.png`,fullPage:true});
  const other=await phone.newPage();await other.goto(base);
  await a.locator('.recent-action[aria-label^="Убрать"]').first().click();
  await other.waitForFunction(()=>document.querySelectorAll('.recent-room').length===11);
  await a.reload();assert.equal(await a.locator('.recent-room').count(),11);
  // Malformed local data must not break entry or produce executable markup.
  await a.evaluate(k=>localStorage.setItem(k,JSON.stringify([{key:'bad',title:'<img src=x onerror=alert(1)>',lastJoined:1},{key:'AAAAAAAAAAAAAAAAAAAAAA',title:'test',lastJoined:1e100}])),key);
  await a.reload();assert.equal(await a.locator('#recentRooms').isVisible(),false);
  const blocked=await browser.newContext();await blocked.addInitScript(()=>{navigator.permissions.query=async()=>({state:'prompt'});Object.defineProperty(window,'localStorage',{get(){throw new Error('blocked')}})});
  const c=await blocked.newPage();c.on('pageerror',e=>errors.push(e.message));await c.goto(base);await c.click('#createRoom');await c.click('#skipDevices');
  await c.click('#join');await c.waitForSelector('#call:not([hidden])');await c.click('#leave');
  assert.deepEqual(errors,[]);console.log('PASS: full-frame corner pixels, portrait/landscape E2EE, preview and call, recent room join/rename/delete/persistence/dedupe/limit/storage failure');
 } finally {await browser.close()}
})().catch(e=>{console.error(e);process.exitCode=1});
