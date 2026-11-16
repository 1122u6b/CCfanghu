'use strict';
(async()=>{const output=document.querySelector('#result');const token=location.hash.slice(1);history.replaceState(null,'',location.pathname);const base='/private-dns/browser/';
 const send=async(path,data)=>{const r=await fetch(base+path,{method:'POST',headers:{'Content-Type':'application/json','X-Leyra-Browser':'1'},body:JSON.stringify({token,...data})});if(!r.ok)throw new Error('检查未完成，请从客户端重新开始。');return r.json();};
 let pc;
 try{const observed=await send('observe',{});const candidates=[];
  if(!window.RTCPeerConnection)throw new Error('此浏览器不支持 WebRTC 地址检查，本次未计为通过。');
  pc=new RTCPeerConnection({iceServers:[{urls:observed.stun}]});pc.createDataChannel('leyra-observation');
  pc.addEventListener('icecandidate',e=>{if(e.candidate){const address=e.candidate.address||e.candidate.candidate.split(' ')[4];if(address&&!candidates.includes(address))candidates.push(address);}});
  await pc.setLocalDescription(await pc.createOffer());
  await new Promise(resolve=>{const timer=setTimeout(resolve,8000);pc.addEventListener('icegatheringstatechange',()=>{if(pc.iceGatheringState==='complete'){clearTimeout(timer);resolve();}});});
  const r=await send('result',{candidates});output.textContent=r.exposure==='real'?'本次发现浏览器出口与已验证线路不同，或出现其他公网地址。请回客户端检查防护。':r.exposure==='lan'?'本次未发现额外公网出口，但浏览器报告了局域网地址。':'本次 HTTPS 出口一致，采集到的 WebRTC 地址未出现额外公网地址。';
 }catch(e){output.textContent=e.message;}finally{if(pc)pc.close();}
})();
