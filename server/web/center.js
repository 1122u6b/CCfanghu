'use strict';
const base='/private-dns/center/';
const status=document.querySelector('#status'),content=document.querySelector('#content'),nav=document.querySelector('#nav');
async function api(path,value){const r=await fetch(base+path,{method:'POST',headers:{'Content-Type':'application/json','X-Leyra-Center':'1'},body:JSON.stringify(value),credentials:'same-origin'});if(!r.ok)throw new Error(r.status===401?'登录已过期，请从客户端重新打开管理中心。':'操作未完成，请检查输入后重试。');return r.json();}
const rpc=value=>api('rpc',value);
function el(tag,text,parent){const e=document.createElement(tag);if(text!==undefined)e.textContent=text;if(parent)parent.append(e);return e;}
function field(form,name,label,value='',multiline=false){el('label',label,form);const input=el(multiline?'textarea':'input',undefined,form);input.name=name;input.value=value;return input;}
function button(parent,text,action){const b=el('button',text,parent);b.type='button';b.addEventListener('click',()=>Promise.resolve().then(action).catch(showError));return b;}
function showError(e){status.className='error';status.textContent=e.message;}
function form(title,inputs,submit){const f=el('form',undefined,content);el('h2',title,f);const values={};for(const [name,label,value,multi] of inputs)values[name]=field(f,name,label,value,multi);const b=el('button','保存',f);b.className='primary';f.addEventListener('submit',async event=>{event.preventDefault();b.disabled=true;try{await submit(Object.fromEntries(Object.entries(values).map(([k,v])=>[k,v.value])));status.className='';status.textContent='已保存';}catch(e){showError(e);}finally{b.disabled=false;}});return f;}
async function view(name){status.className='';status.textContent='正在加载…';content.replaceChildren();
 if(name==='account'){const a=await rpc({type:'account'});el('h2','我的账户',content);el('p','账户编号：'+a.id,content);form('联系信息',[['contact','联系方式',a.contact]],async v=>{await rpc({type:'set_contact',...v});});}
 if(name==='messages'){const r=await rpc({type:'messages'});el('h2','消息',content);if(!r.items.length)el('p','暂无消息',content);for(const m of r.items){const a=el('article',undefined,content);el('h3',m.title,a);el('p',m.body,a);}button(content,'全部标为已读',()=>rpc({type:'mark_read',all:true}));}
 if(name==='tickets'){const r=await rpc({type:'tickets'});el('h2','工单',content);for(const t of r.items){const a=el('article',undefined,content);el('h3','#'+t.id+' '+t.title,a);el('small','状态：'+t.state,a);for(const m of t.messages||[])el('p',(m.from_admin?'客服：':'我：')+m.body,a);form('回复 #'+t.id,[['body','回复内容','',true]],async v=>{await rpc({type:'ticket_reply',id:t.id,...v});await view('tickets');});}form('新建工单',[['title','标题',''],['body','问题描述','',true]],async v=>{await rpc({type:'new_ticket',kind:1,...v});await view('tickets');});}
 if(name==='exits'){const r=await rpc({type:'exits'});el('h2','我的出口',content);for(const x of r.items){const a=el('article',undefined,content);el('h3',x.name+(x.id===r.selected?' · 使用中':''),a);if(x.id!==r.selected){button(a,'使用此出口',async()=>{await rpc({type:'select_exit',id:x.id});await view('exits');});button(a,'删除',async()=>{if(confirm('确认删除这个出口？')){await rpc({type:'delete_exit',id:x.id});await view('exits');}});}}}
 if(name==='help'){const r=await rpc({type:'help'});el('h2','帮助',content);if(!(r.faq||[]).length&&!(r.guides||[]).length)el('p','暂无帮助内容，可通过工单联系管理员。',content);for(const x of r.faq||[]){const a=el('article',undefined,content);el('h3',x.q||x.title||'',a);el('p',x.a||x.body||'',a);}for(const x of r.guides||[]){try{const u=new URL(x.url);if(u.protocol==='https:'){const a=el('a',x.title||'查看帮助',content);a.href=u.href;a.rel='noreferrer noopener';a.target='_blank';}}catch{}}}
 status.textContent='';
}
nav.addEventListener('click',e=>{const b=e.target.closest('[data-view]');if(b)view(b.dataset.view).catch(showError);});
document.querySelector('#logout').addEventListener('click',async()=>{try{await api('logout',{});nav.hidden=true;content.replaceChildren();status.textContent='已退出，请从客户端重新登录。';}catch(e){showError(e);}});
(async()=>{const token=location.hash.slice(1);history.replaceState(null,'',location.pathname);if(token)await api('session',{token});await view('account');nav.hidden=false;})().catch(showError);
