const {chromium}=require('playwright');const assert=require('node:assert/strict');
(async()=>{const browser=await chromium.launch({channel:'chrome',headless:true});try{
 const ctx=await browser.newContext({viewport:{width:1440,height:1000}});
 await ctx.addInitScript(()=>{delete HTMLMediaElement.prototype.setSinkId;delete AudioContext.prototype.setSinkId;navigator.permissions.query=async()=>({state:'prompt'});});
 const p=await ctx.newPage();const errors=[];p.on('pageerror',e=>errors.push(e.message));
 await p.goto(process.env.BASE_URL||'http://127.0.0.1:8081');await p.click('#createRoom');await p.click('#skipDevices');await p.click('#join');await p.waitForSelector('#call:not([hidden])');
 await p.click('#settingsBtn');assert.ok(await p.locator('#outputField').isVisible());assert.equal(await p.locator('#callOutput').isDisabled(),true);assert.equal(await p.locator('#callOutput option').textContent(),'Системное устройство');
 const mic=await p.locator('#callMic').boundingBox(),out=await p.locator('#callOutput').boundingBox(),test=await p.locator('#testOutput').boundingBox();assert.equal(mic.y,out.y);assert.ok(test.y>out.y+out.height);
 await p.screenshot({path:'output/playwright/settings-system-output.png'});await p.click('#callDialogClose');
 await p.click('#copyInvite');assert.equal(await p.locator('#copyRoomInvite').evaluate(e=>getComputedStyle(e).justifyContent),'center');await p.screenshot({path:'output/playwright/invite-centered.png'});await p.click('#callDialogClose');
 await p.click('#leave');assert.equal(await p.locator('#returnCall').evaluate(e=>getComputedStyle(e).justifyContent),'center');await p.screenshot({path:'output/playwright/return-centered.png'});
 assert.deepEqual(errors,[]);console.log('PASS unsupported output keeps aligned system field; copy and return labels centered');
 }finally{await browser.close()}})().catch(e=>{console.error(e);process.exitCode=1});
