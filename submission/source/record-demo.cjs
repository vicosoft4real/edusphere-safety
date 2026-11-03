// Real browser recordings. Run with node; PLAYWRIGHT_MODULE may override the
// installed skill's module path. Requires the local demo services and state.
process.env.PLAYWRIGHT_BROWSERS_PATH ||= '/tmp/edusphere-playwright';
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || '/Users/user/.agents/skills/playwright-skill/node_modules/playwright');
const fs = require('fs');
const path = require('path');
const ROOT = path.resolve(__dirname, '../..');
const BASE = process.env.DEMO_URL || 'http://127.0.0.1:3000';
const state = JSON.parse(fs.readFileSync(path.join(ROOT,'.demo-runtime/demo-state.json')));
const OUT = path.join(ROOT,'submission/recordings');
const ASSETS = path.join(ROOT,'submission/assets');
const sleep = ms => new Promise(r=>setTimeout(r,ms));

(async()=>{
 const browser = await chromium.launch({headless:false,channel:'chrome'});
 const recordings=fs.existsSync(path.join(OUT,'clips.json'))?JSON.parse(fs.readFileSync(path.join(OUT,'clips.json'))):{};
 async function scene(name,route,duration,actions,auth=true){
  if(process.env.RECORD_ONLY && process.env.RECORD_ONLY!==name)return;
  if(process.env.SKIP_EXISTING && recordings[name] && fs.existsSync(recordings[name].file))return;
  const created=Date.now();
  const context=await browser.newContext({viewport:{width:1280,height:720},deviceScaleFactor:1,
   recordVideo:{dir:OUT,size:{width:1280,height:720}}});
  if(auth) await context.addCookies([{name:'safety_token',value:state.token,domain:'127.0.0.1',path:'/',httpOnly:true,sameSite:'Lax'}]);
  await context.addInitScript(()=>{
   addEventListener('DOMContentLoaded',()=>{
    const dot=document.createElement('div'); dot.id='demo-recording-pointer';
    Object.assign(dot.style,{position:'fixed',left:'0',top:'0',width:'22px',height:'22px',
     border:'2px solid #174d43',borderRadius:'50%',background:'#b4ef7244',zIndex:'2147483647',pointerEvents:'none',transform:'translate(-50%,-50%)',display:'none'});
    document.body.append(dot);
    addEventListener('mousemove',e=>{dot.style.display='block';dot.style.left=e.clientX+'px';dot.style.top=e.clientY+'px'});
   });
  });
  const page=await context.newPage();
  await page.goto(BASE+route);
  await page.locator('main').waitFor();
  const began=Date.now();
  const offset=(began-created)/1000;
  const until=async sec=>sleep(Math.max(0,began+sec*1000-Date.now()));
  const extra=await actions(page,until,context,began)||{};
  await until(duration+0.5);
  await page.screenshot({path:path.join(ASSETS,name+'.png')});
  const file=await page.video().path();
  await context.close();
  const destination=path.join(OUT,name+'.webm');fs.renameSync(file,destination);
  recordings[name]={file:destination,offset,duration,recordedDuration:(Date.now()-began)/1000,...extra};
  fs.writeFileSync(path.join(OUT,'clips.json'),JSON.stringify(recordings,null,2));
  console.log('Recorded',name,recordings[name]);
 }
 try {
  // Authenticate via the real UI for the first shot, then use that cookie.
  await scene('overview','/login',18,async(page,until,ctx)=>{
   await until(1);await page.getByLabel('School tenant ID').pressSequentially('demo-school',{delay:50});
   await page.getByLabel('Staff user ID').pressSequentially('safety-officer',{delay:50});
   await until(5);await page.getByRole('button',{name:'Sign in',exact:true}).click();
   await page.waitForURL(BASE+'/');
   if(!(await page.locator('main').innerText()).includes('Monitoring coverage')) throw new Error('Login failed');
   const cookie=(await ctx.cookies()).find(c=>c.name==='safety_token');state.token=cookie.value;
   fs.writeFileSync(path.join(ROOT,'.demo-runtime/demo-state.json'),JSON.stringify(state));
   await until(12);await page.mouse.move(950,245,{steps:20});
  },false);
  await scene('rules','/rules',18,async(page,until)=>{
   await until(2);await page.mouse.move(470,440,{steps:25});
   await until(4);await page.getByLabel('Confidence',{exact:true}).focus();
   await until(7);await page.mouse.wheel(0,300);
   await until(10);await page.getByRole('button',{name:'Publish new bundle',exact:true}).click();
   await page.getByText(/Published as bundle v/).waitFor({timeout:10000});
   console.log('Rule publication:',await page.getByText(/Published as bundle v/).innerText());
  });
  await scene('incident','/incidents',18,async(page,until,ctx)=>{
   await until(4);await page.getByText('ZoneIntrusion',{exact:true}).click();
   await page.waitForURL('**/incidents/'+state.incidentId);
   await until(10);await page.mouse.move(890,190,{steps:25});
   const response=await ctx.request.get(BASE+'/api/v1/evidence/'+state.edgeEventId+'/clip');
   const bytes=await response.body();
   if(response.status()!==200 || !bytes.equals(fs.readFileSync(path.join(ASSETS,'sim/sim.mp4'))))
    throw new Error('Evidence proxy must return exact original clip bytes: HTTP '+response.status());
   console.log('PASS: evidence proxy returned byte-identical MP4,',bytes.length,'bytes');
  });
  await scene('evidence','/incidents/'+state.incidentId,20,async(page,until)=>{
   await until(3);await page.getByRole('link',{name:'Play clip',exact:true}).click();
   await page.locator('video').waitFor({timeout:15000});
   await page.locator('video').evaluate(v=>{v.muted=true;v.loop=true;v.play()});
   await until(16);await page.goBack();
  });
  await scene('llama','/incidents/'+state.incidentId,22,async(page,until,ctx,began)=>{
   await until(1);await page.getByRole('button',{name:'Generate briefing',exact:true}).click();
   await page.getByRole('heading',{name:'Review checklist',exact:true}).waitFor({timeout:125000});
   const readyAt=(Date.now()-began)/1000;
   await page.getByRole('region',{name:'Llama incident briefing'}).scrollIntoViewIfNeeded();
   await page.screenshot({path:path.join(ASSETS,'llama-result.png')});
   await sleep(15000);
   console.log('PASS: real Meta Llama structured briefing displayed');
   return {inferenceWaitShortened:true,readyAt};
  });
  await scene('response','/incidents/'+state.incidentId,18,async(page,until)=>{
   await until(2);await page.getByRole('button',{name:'Acknowledge',exact:true}).click();
   await page.getByRole('button',{name:'Investigating',exact:true}).waitFor();
   await until(5);await page.getByLabel('Note / reason').pressSequentially('Reviewed synthetic clip; checking corridor.',{delay:25});
   await until(8);await page.getByRole('button',{name:'Investigating',exact:true}).click();
   await page.getByRole('button',{name:'Resolved',exact:true}).waitFor();
   await until(11);await page.getByLabel('Note / reason').fill('Demo check complete; incident resolved by staff.');
   await until(13);await page.getByRole('button',{name:'Resolved',exact:true}).click();
   await page.getByText('Resolved',{exact:true}).waitFor();
   console.log('PASS: staff workflow reached Resolved');
  });
  await scene('operations','/policies',16,async(page,until)=>{
   await until(2);await page.mouse.move(620,470,{steps:30});
   await until(7);await page.goto(BASE+'/diagnostics');
   await until(12);await page.mouse.wheel(0,190);
  });
  const fresh=await browser.newContext();
  const unauth=await fresh.request.get(BASE+'/api/v1/evidence/'+state.edgeEventId+'/clip');
  if(unauth.status()!==401)throw new Error('Anonymous evidence must be denied');
  console.log('PASS: anonymous evidence denied (401)');await fresh.close();
 } finally {await browser.close()}
})().catch(e=>{console.error(e);process.exit(1)});
