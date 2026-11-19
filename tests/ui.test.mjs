import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {Window} from 'happy-dom';
const base=new URL('../client-assets/',import.meta.url);
const files=['assets/native.js','win/assets/hx.js','win/assets/ui.js','assets/bridge.js','assets/product.js'];
async function setup(native) {
 const window=new Window({url:'https://app.leyra.local/win/index.html'});
 if(native){
  let listener;
  window.chrome={webview:{addEventListener:(_,fn)=>{listener=fn;},postMessage:m=>{
   Promise.resolve().then(()=>native(m.command,m.args)).then(value=>listener({data:{id:m.id,ok:true,value}}),error=>listener({data:{id:m.id,ok:false,error:error.message}}));
  }}};
 }
 window.console.warn=()=>{};
 window.document.body.innerHTML='<div id="app"></div>';
 for(const path of files)window.eval(await readFile(new URL(path,base),'utf8'));
 window.document.dispatchEvent(new window.Event('DOMContentLoaded'));
 await new Promise(resolve=>setTimeout(resolve,5));
 return window;
}
async function cleanup(w){await w.happyDOM.close();}
function render(w,page) {
 const p=w.__proto;
 Object.assign(p.S,{screen:'main',page,status:'down',consentVer:p.DISCLAIMER_VER,ai:true,dialog:null,popup:null});
 p.render();return w.document.body.textContent;
}
test('without the native service no protected or demonstration state is shown',async()=>{
 const w=await setup();try{
  assert.notEqual(w.__proto.S.status,'ok');
  assert.equal(w.__proto.AIACC.length,0);
  assert.equal(w.__proto.MSGS.length,0);
  assert.equal(w.__proto.ROUTES[0].n,'-');
  assert.equal(w.document.querySelector('.p-demo'),null);
  assert.equal(w.__proto.S.browsers.length,0);
 }finally{await cleanup(w);}
});

const settle=()=>new Promise(resolve=>setTimeout(resolve,30));
const inactive=(command,args)=>command==='daemon'&&args.request.type==='status'?{protection:{state:'not_activated'}}:command==='daemon'&&args.request.type==='account'?{activated:false}:{};

test('speed button shows pending work and consumes the real measured result',async()=>{
 let finish;
 const w=await setup((command,args)=>command==='daemon'&&args.request.type==='speed_test'?new Promise(resolve=>{finish=resolve;}):inactive(command,args));
 try{
  render(w,'routes');w.__proto.S.status='ok';w.__proto.A.speedTest();await settle();
  assert.equal(w.__proto.S.speedTesting,true);
  assert.match(w.document.body.textContent,/正在测线路/);
  finish({down_bps:125000,measured:true});await settle();
  assert.equal(w.__proto.S.speedTesting,false);assert.equal(w.__proto.S.busy.speed,false);
  const items=w.CC_CHECK([['线路',['线路速度']]])[0].items;
  assert.match(items[0].d,/1.00 Mbps/);
 }finally{await cleanup(w);}
});

test('browser response refreshes both IP and WebRTC rows without repeating the whole check',async()=>{
 const w=await setup((command,args)=>command==='daemon'&&args.request.type==='browser_results'?{installed:['Google Chrome'],items:[{browser:'Google Chrome',at:123,exposure:'clean'}],checks:[{id:'L5',verdict:'pass',detail:'Google Chrome：浏览器出口 IP 203.0.113.9'},{id:'L6',verdict:'pass',detail:'WebRTC 未绕开线路'}]}:inactive(command,args));
 try{
  render(w,'check');w.__proto.A.dialog('browsers');await settle();
  const items=w.CC_CHECK([['真实 IP 检查',['浏览器看到的 IP','浏览器绕开独享线路']]])[0].items;
  assert.ok(items.some(x=>/203.0.113.9/.test(x.d)));assert.ok(items.some(x=>x.n==='浏览器绕开独享线路'&&x.s==='ok'));
 }finally{await cleanup(w);}
});

test('reopening a check refreshes browser IP and WebRTC alongside the live summary',async()=>{
 const w=await setup((command,args)=>{
  if(command!=='daemon')return {};
  if(args.request.type==='check_history')return {items:[{id:'old',at:123,kind:'deep',items:[{id:'L5',verdict:'skipped',detail:'not yet tested'},{id:'L6',verdict:'skipped',detail:'not yet tested'}]}]};
  if(args.request.type==='browser_results')return {installed:['Google Chrome'],items:[{browser:'Google Chrome',at:456,exposure:'clean'}],checks:[{id:'L5',verdict:'pass',detail:'browser IP 203.0.113.9'},{id:'L6',verdict:'pass',detail:'WebRTC clean'}]};
  return inactive(command,args);
 });
 try{
  render(w,'overview');w.__proto.A.go('check');await settle();
  w.__proto.A.checkHist(0);await settle();
  const items=w.CC_CHECK([['真实 IP 检查',['浏览器看到的 IP','浏览器绕开独享线路']]])[0].items;
  assert.equal(items.length,2);assert.ok(items.every(x=>x.s==='ok'));assert.ok(items.some(x=>/203.0.113.9/.test(x.d)));
 }finally{await cleanup(w);}
});

test('running or incomplete browser probes are never summarized as normal',async()=>{
 const w=await setup();try{
  for(const st of ['testing','unknown']){w.__proto.S.browsers=[{n:'Chrome',st}];assert.equal(w.__proto.browserSum().s,'skip');}
 }finally{await cleanup(w);}
});

test('an already activated account recovers the overview after reopening',async()=>{
 const w=await setup((command,args)=>command==='daemon'&&args.request.type==='account'?{activated:true,account_id:5,suite:{kind:'soft'},own_exit:'waiting',exits:{list:[]},channels:[]}:command==='daemon'&&args.request.type==='status'?{protection:{state:'provisioning'}}:{});
 try{await settle();assert.equal(w.__proto.S.screen,'main');assert.equal(w.__proto.S.page,'overview');}
 finally{await cleanup(w);}
});

test('activation ignores status replies sent before activation finished',async()=>{
 let oldReply,activated=false;
 const w=await setup((command,args)=>{
  if(command!=='daemon')return {};
  const type=args.request.type;
  if(type==='status'&&!activated)return new Promise(resolve=>{oldReply=resolve;});
  if(type==='status')return {protection:{state:'provisioning'}};
  if(type==='activate'){activated=true;return {saved:true};}
  if(type==='account')return activated?{activated:true,account_id:5,suite:{kind:'soft'},own_exit:'waiting',exits:{list:[]},channels:[]}:{activated:false};
  return {};
 });
 try{
  Object.assign(w.__proto.S,{screen:'activate',consentVer:w.__proto.DISCLAIMER_VER});w.__proto.render();
  w.document.querySelector('[data-keep="act-code"]').value='FAKE-TEST-CODE';
  w.document.querySelector('[data-keep="act-contact"]').value='fictional-test';
  w.document.querySelector('[data-keep="act-agree"]').checked=true;
  w.__proto.A.activate();await settle();
  assert.equal(w.__proto.S.screen,'main');
  assert.ok(oldReply);oldReply({protection:{state:'not_activated'}});await settle();
  assert.equal(w.__proto.S.screen,'main');assert.equal(w.__proto.S.page,'overview');
 }finally{await cleanup(w);}
});
test('all nine original page views render with service data unavailable',async()=>{
 const w=await setup();try{
  for(const page of ['overview','routes','check','accounts','profile','messages','tickets','help','settings']){
   const content=render(w,page);
   assert.ok(content.length>20,page);
   assert.equal(w.__proto.S.page,page);
   assert.equal(w.document.querySelector('.p-group'),null,page);
   assert.equal(w.document.querySelector('.p-qqcard'),null,page);
  }
 }finally{await cleanup(w);}
});

test('failed activation keeps the entered form and does not claim success',async()=>{
 const w=await setup((command,args)=>{if(command==='daemon'&&args.request.type==='activate')throw new Error('测试授权不可用');return inactive(command,args);});
 try{
  Object.assign(w.__proto.S,{screen:'activate',consentVer:w.__proto.DISCLAIMER_VER});w.__proto.render();
  w.document.querySelector('[data-keep="act-code"]').value='FAKE-INVALID-CODE';
  w.document.querySelector('[data-keep="act-contact"]').value='fictional-test';
  w.document.querySelector('[data-keep="act-agree"]').checked=true;
  w.__proto.A.activate();await settle();
  assert.equal(w.__proto.S.screen,'activate');assert.equal(w.__proto.S.busy.act,null);
  assert.match(w.document.body.textContent,/测试授权不可用/);
  assert.equal(w.document.querySelector('[data-keep="act-code"]').value,'FAKE-INVALID-CODE');
 }finally{await cleanup(w);}
});
test('unpaired setup activates with an authorization code',async()=>{
 const w=await setup();try{
  Object.assign(w.__proto.S,{screen:'activate',consentVer:w.__proto.DISCLAIMER_VER});w.__proto.render();
  assert.match(w.document.body.textContent,/授权码/);
  assert.ok(w.document.querySelector('[data-keep="act-code"]'));
  assert.ok(w.document.querySelector('[data-keep="act-agree"]'));
  assert.ok(w.document.querySelector('[data-act="activate"]'));
 }finally{await cleanup(w);}
});

for(const [name,expires,daysLeft,permanent] of [['limited',Math.floor(Date.now()/1000)+86400,1,false],['permanent',253402300799,null,true]]){
 test(name+' paid license displays the actual entitlement on overview and profile',async()=>{
  const w=await setup((command,args)=>command==='daemon'&&args.request.type==='account'?{activated:true,account_id:5,suite:{kind:'soft'},software_expires_at:expires,node_expires_at:expires,days_left:daysLeft,own_exit:'waiting',exits:{list:[]},channels:[]}:command==='daemon'&&args.request.type==='status'?{protection:{state:'provisioning'}}:{});
  try{
   await settle();assert.equal(w.__proto.S.licensePermanent,permanent);
   for(const page of ['overview','profile']){
    const text=render(w,page);
    if(permanent)assert.match(text,/永久使用/);
    else{assert.doesNotMatch(text,/永久使用|永久授权|全套/);assert.match(text,/授权/);assert.match(text,/到期|1天|1 天/);}
   }
  }finally{await cleanup(w);}
 });
}
test('renewal dialog and support QQ group are available',async()=>{
 const w=await setup();try{
  assert.equal(w.LEYRA_FEATURES.licenseCodes,true);
  assert.equal(w.LEYRA_FEATURES.supportGroup,true);
  render(w,'profile');w.__proto.S.dialog={type:'switchCode'};w.__proto.render();
  assert.ok(w.document.querySelector('[data-keep="switch-code"]'));
  w.__proto.CHANNELS.push(['Leyra 技术交流群（24小时）','1136427075','qqgroup','Leyra 技术交流群','24小时']);
  w.__proto.S.dialog=null;
  const overview=render(w,'overview');
  assert.match(overview,/1136427075/);
 }finally{await cleanup(w);}
});


test('overview shows the traffic the server counted and each exit shows its own address',async()=>{
 const account={activated:true,account_id:5,suite:{kind:'soft'},own_exit:'connected',channels:[],current_exit:2,
  exits:{list:[{id:1,name:'我的ip',own:true,lines:1,current:false,location:'',exit_ip:'203.0.113.7'},{id:2,name:'我的vps',own:true,lines:1,current:true,location:'',exit_ip:'198.51.100.2'},{id:3,name:'坏的',own:true,lines:1,current:false,location:'',exit_ip:'',exit_ip_error:'出口连上了但不转发流量'}],can_add:true,max_own:5},
  traffic_used_bytes:35*1048576,traffic_limit_bytes:null,traffic_reset_at:Math.floor(Date.now()/1000)+5*86400,traffic_days_from:'2026-11-01',traffic_days:[35*1048576]};
 const w=await setup((command,args)=>command==='daemon'&&args.request.type==='account'?account:command==='daemon'&&args.request.type==='status'?{protection:{state:'provisioning'}}:{});
 try{
  await settle();
  const home=render(w,'overview');
  assert.match(home,/本月流量/);assert.match(home,/35\s*MB · 不限/);assert.match(home,/当前线路/);
  for(const status of ['ok','prep']){
   w.__proto.S.status=status;w.__proto.render();
   assert.match(w.document.body.textContent,/本月流量35\s*MB/,'traffic card while '+status);
  }
  const routes=render(w,'routes');
  assert.match(routes,/203\.0\.113\.7/);assert.match(routes,/198\.51\.100\.2/);
  // an exit the server could not get through says why instead of a bare dash
  assert.match(routes,/坏的\s*出口连上了但不转发流量/);
  // the support group number is never cut short on the overview
 }finally{await cleanup(w);}
});
