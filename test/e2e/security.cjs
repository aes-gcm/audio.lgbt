const {chromium}=require('playwright');
const assert=require('node:assert/strict');
const base=process.env.BASE_URL||'http://127.0.0.1:8083';
(async()=>{
 const browser=await chromium.launch({channel:'chrome',headless:true,args:['--use-fake-device-for-media-stream','--use-fake-ui-for-media-stream']});
 try{
  const context=await browser.newContext({permissions:['camera','microphone']});
  const page=await context.newPage();
  const response=await page.goto(base+'/?plain=1&debug=1');
  assert.match(response.headers()['content-security-policy'],/script-src 'self';/);
  assert.equal(response.headers()['referrer-policy'],'no-referrer');
  assert.equal((await context.request.get(base+'/bootstrap.js')).status(),200);
  await page.waitForFunction(()=>window.__z);
  assert.equal(await page.evaluate(()=>__z.e2ee),true,'URL must not disable encryption');
  await page.evaluate(()=>{const script=document.createElement('script');script.textContent='window.injected=true';document.body.appendChild(script);});
  assert.equal(await page.evaluate(()=>window.injected),undefined,'inline script executed');
  await page.click('#createRoom');await page.waitForSelector('#join:not([disabled])');
  await page.fill('#name','<img src=x onerror=window.injected=true>');
  await page.click('#join');await page.waitForSelector('#call:not([hidden])');
  assert.equal(await page.locator('#stage img').count(),0);
  assert.equal(await page.evaluate(()=>window.injected),undefined);
  await page.click('#leave');
  const unsupported=await context.newPage();
  await unsupported.addInitScript(()=>{delete window.RTCRtpScriptTransform;delete RTCRtpSender.prototype.createEncodedStreams;});
  await unsupported.goto(base+'/?plain=1&debug=1');await unsupported.click('#createRoom');await unsupported.waitForSelector('#join:not([disabled])');
  await unsupported.click('#join');assert.equal(await unsupported.locator('#call').isVisible(),false);
  assert.match(await unsupported.locator('#pjError').innerText(),/не поддерживает шифрование/);
  console.log('PASS CSP/inline injection, safe names, URL encryption downgrade blocked, unsupported browser fails closed');
 }finally{await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
