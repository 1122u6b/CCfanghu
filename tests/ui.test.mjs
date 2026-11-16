import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {Window} from 'happy-dom';
const base=new URL('../client-assets/',import.meta.url);
const files=['assets/native.js','win/assets/hx.js','win/assets/ui.js','assets/bridge.js','assets/product.js'];
async function setup() {
 const window=new Window({url:'https://app.leyra.local/win/index.html'});
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
test('all nine original page views render with service data unavailable',async()=>{
 const w=await setup();try{
  for(const page of ['overview','routes','check','accounts','profile','messages','tickets','help','settings']){
   const content=render(w,page);
   assert.ok(content.length>20,page);
   assert.equal(w.__proto.S.page,page);
   assert.equal(w.document.querySelector('[data-keep="act-code"]'),null,page);
   assert.equal(w.document.querySelector('.p-group'),null,page);
   assert.equal(w.document.querySelector('.p-qqcard'),null,page);
  }
 }finally{await cleanup(w);}
});
test('unpaired setup imports a connection file without a license field',async()=>{
 const w=await setup();try{
  Object.assign(w.__proto.S,{screen:'activate',consentVer:w.__proto.DISCLAIMER_VER});w.__proto.render();
  assert.match(w.document.body.textContent,/导入服务器连接文件/);
  assert.doesNotMatch(w.document.body.textContent,/授权码/);
  assert.equal(w.document.querySelector('[data-keep="act-code"]'),null);
  assert.ok(w.document.querySelector('[data-act="connectServer"]'));
 }finally{await cleanup(w);}
});
test('legacy license dialogs remain inaccessible when the license feature is off',async()=>{
 const w=await setup();try{
  for(const type of ['switchCode','restoreCode']){
   render(w,'profile');w.__proto.S.dialog={type};w.__proto.render();
   assert.equal(w.document.querySelector('[data-keep="switch-code"]'),null);
   assert.equal(w.document.querySelector('[data-keep="restore-code"]'),null);
  }
  assert.equal(w.LEYRA_FEATURES.licenseCodes,false);
  assert.equal(w.LEYRA_FEATURES.supportGroup,false);
 }finally{await cleanup(w);}
});
