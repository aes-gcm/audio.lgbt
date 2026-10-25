const {chromium} = require('playwright');
const assert = require('node:assert/strict');
const base = process.env.BASE_URL || 'http://127.0.0.1:8097';
(async () => {
  const browser = await chromium.launch({channel:'chrome',headless:true,args:['--use-fake-device-for-media-stream','--use-fake-ui-for-media-stream','--autoplay-policy=no-user-gesture-required']});
  try {
    const context = await browser.newContext({permissions:['camera','microphone']});
    await context.addInitScript(() => {
      window.microphoneTracks = [];
      const capture = navigator.mediaDevices.getUserMedia.bind(navigator.mediaDevices);
      navigator.mediaDevices.getUserMedia = async constraints => {
        const stream = await capture(constraints);
        microphoneTracks.push(...stream.getAudioTracks());
        return stream;
      };
    });
    const page = await context.newPage();
    await page.goto(base+'/?debug=1');
    await page.click('#createRoom');
    await page.waitForSelector('#join:not([disabled])');
    const settings = await page.evaluate(() => microphoneTracks.filter(t=>t.readyState==='live').map(t=>t.getSettings()));
    assert(settings.length > 0);
    assert(settings.every(s=>s.autoGainControl===true && s.echoCancellation===true));
    await page.click('#join');
    await page.waitForSelector('#call:not([hidden])');
    await page.waitForFunction(async () => [...(await __z.pub().getStats()).values()].some(s=>s.type==='outbound-rtp'&&s.kind==='audio'&&s.packetsSent>10));
    const levels = await page.evaluate(async () => {
      const sender = __z.pub().getSenders().find(s=>s.track?.kind==='audio');
      const ctx = new AudioContext(); await ctx.resume();
      const source = ctx.createMediaStreamSource(new MediaStream([sender.track]));
      const analyser = ctx.createAnalyser(); analyser.fftSize=2048; source.connect(analyser);
      const result=[];
      for (const value of [100, 0, 200]) {
        document.querySelector('#micLevel').value=String(value);
        document.querySelector('#micLevel').dispatchEvent(new Event('input'));
        await new Promise(resolve=>setTimeout(resolve,250));
        let energy=0,count=0;
        const data=new Float32Array(2048);
        for(let i=0;i<40;i++) { analyser.getFloatTimeDomainData(data);for(const v of data){energy+=v*v;count++;}await new Promise(resolve=>setTimeout(resolve,20)); }
        result.push(Math.sqrt(energy/count));
      }
      source.disconnect(); await ctx.close(); return result;
    });
    assert(levels[0]>.001, 'Microphone reaches the send track');
    assert(levels[1]<.00001, 'Manual zero must mute after AGC');
    assert(levels[2]>levels[0]*1.3, 'Manual boost must remain available after AGC');
    console.log(JSON.stringify({pass:true,autoGainControl:settings[0].autoGainControl,sendRms100:levels[0],sendRms0:levels[1],sendRms200:levels[2]}));
  } finally { await browser.close(); }
})().catch(e=>{console.error(e);process.exitCode=1;});
