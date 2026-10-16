const {chromium}=require('playwright');
const {spawn,spawnSync}=require('node:child_process');
const fs=require('node:fs');
const assert=require('node:assert/strict');
const path=require('node:path');
const base=process.env.BASE_URL||'http://127.0.0.1:8082';
const executable=process.env.SERVER_EXE||path.resolve('dist/test-selfhost.exe');
const folder=path.resolve('dist/selfhost-check');fs.mkdirSync(folder,{recursive:true});
const accountFile=path.join(folder,'accounts.json');
const password='local-test-password-123';
const account=spawnSync(executable,['account','-file',accountFile,'-password-stdin','admin'],{input:password,windowsHide:true});
assert.equal(account.status,0,account.stderr.toString());
(async()=>{
 const server=spawn(executable,['-dev','-http','127.0.0.1:8082','-udp','3482','-require-auth','-accounts',accountFile],{windowsHide:true,stdio:'ignore'});
 let browser;
 try{
  for(let i=0;i<100;i++){try{if((await fetch(base+'/healthz')).ok)break;}catch{}await new Promise(r=>setTimeout(r,100));}
  browser=await chromium.launch({channel:'chrome',headless:true,args:['--use-fake-device-for-media-stream','--use-fake-ui-for-media-stream']});
  const host=await browser.newContext({permissions:['microphone','camera']});const guest=await browser.newContext({permissions:['microphone','camera']});
  const a=await host.newPage(),b=await guest.newPage();const errors=[];a.on('pageerror',e=>errors.push(e.message));b.on('pageerror',e=>errors.push(e.message));
  await a.goto(base);await a.click('#createRoom');await a.waitForSelector('#accountDialog[open]');
  await a.fill('#accountName','admin');await a.fill('#accountPassword','wrong');await a.click('#accountSubmit');await a.waitForSelector('#accountError:not([hidden])');
  await a.fill('#accountPassword',password);await a.click('#accountSubmit');await a.waitForSelector('#join:not([disabled])');
  assert.equal(await a.inputValue('#accountPassword'),'');
  const cookies=await host.cookies();assert.ok(cookies.some(c=>c.name==='audio_session'&&c.httpOnly&&c.sameSite==='Strict'));
  await a.click('#join');await a.waitForSelector('#call:not([hidden])');
  const link=a.url();await b.goto(link);await b.waitForSelector('#join:not([disabled])');await b.click('#join');await b.waitForSelector('#call:not([hidden])');
  await a.waitForFunction(()=>document.querySelector('#count').textContent==='2');
  assert.equal((await guest.cookies()).some(c=>c.name==='audio_session'),false);
  const probe=await guest.newPage();await probe.goto(base);
  const denied=await probe.evaluate(()=>new Promise((resolve,reject)=>{const s=new WebSocket(location.origin.replace('http','ws')+'/ws');const timer=setTimeout(()=>reject(Error('WS timeout')),5000);s.onopen=()=>s.send(JSON.stringify({type:'join',room:'unauthorized-room',name:'Guest'}));s.onmessage=e=>{clearTimeout(timer);resolve(JSON.parse(e.data));s.close()};}));
  assert.equal(denied.error,'a valid token is required to create a room');
  assert.equal((await guest.request.post(base+'/api/login',{headers:{Origin:'https://evil.example'},data:{username:'admin',password}})).status(),403);
  await probe.click('#createRoom');await probe.waitForSelector('#accountDialog[open]');await probe.setViewportSize({width:390,height:844});await probe.screenshot({path:'output/playwright/selfhost-login-mobile.png'});await probe.press('#accountName','Escape');assert.equal(await probe.locator('#accountDialog').isVisible(),false);
  await host.request.post(base+'/api/logout',{data:{}});assert.equal((await (await host.request.get(base+'/api/server')).json()).username,'');
  await a.click('#leave');await b.click('#leave');
  assert.deepEqual(errors,[]);
  console.log('PASS restricted creation, login/error/logout, guest invite without account, direct WS denial, origin checks, mobile dialog');
  if(process.env.TEST_NATIVE==='1') {
   const run=(exe,args,env)=>new Promise((resolve,reject)=>{
    const p=spawn(exe,args,{windowsHide:true,env:{...process.env,...env},stdio:'inherit'});
    p.on('error',reject);p.on('close',code=>code===0?resolve():reject(Error('Native test failed: '+code)));
   });
   await run(process.env.DOTNET_EXE||'dotnet',['test/windows/bin/Release/net10.0-windows10.0.19041.0/AudioLgbt.Native.Tests.dll','server-checks'],{BASE_URL:base+'/'});
   for(const creates of ['1','0'])await run(process.execPath,['test/e2e/native-client.cjs'],{BASE_URL:base,AUTH_USER:'admin',AUTH_PASSWORD:password,NATIVE_CREATES:creates});
   await run(process.env.DOTNET_EXE||'dotnet',['test/windows/bin/Release/net10.0-windows10.0.19041.0/AudioLgbt.Native.Tests.dll','server-ui-checks'],{BASE_URL:base+'/'});
  }
 }finally{await browser?.close();server.kill();}
})().catch(e=>{console.error(e);process.exitCode=1});
