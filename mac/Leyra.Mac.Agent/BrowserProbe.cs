using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Leyra.Mac.Agent;

/// <summary>
/// Loopback-only test page for the browser check. Each test gets a one-time nonce; the page
/// gathers WebRTC ICE candidates and the address the browser reaches the internet from,
/// then posts them back here. Nothing else is served.
/// </summary>
sealed class BrowserProbe(Func<string,string?,IReadOnlyList<(string Ip,string Type)>,object> report):IDisposable
{
 public const int Port=17892;
 readonly Dictionary<string,(string Browser,DateTimeOffset Expires)> pending=new();
 TcpListener? listener;CancellationTokenSource? stop;
 public string Begin(string browser)
 {
  EnsureStarted();
  var nonce=Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
  lock(pending){foreach(var k in pending.Where(x=>x.Value.Expires<DateTimeOffset.UtcNow).Select(x=>x.Key).ToList())pending.Remove(k);if(pending.Count>16)throw new InvalidOperationException("同时进行的浏览器检查太多");pending[nonce]=(browser,DateTimeOffset.UtcNow.AddMinutes(3));}
  return "http://127.0.0.1:"+Port+"/probe/"+nonce;
 }
 void EnsureStarted()
 {
  if(listener is not null)return;
  listener=new TcpListener(IPAddress.Loopback,Port);listener.Start(8);stop=new CancellationTokenSource();var token=stop.Token;
  _=Task.Run(async()=>
  {
   while(!token.IsCancellationRequested)
   {
    TcpClient client;try{client=await listener.AcceptTcpClientAsync(token);}catch{break;}
    _=Task.Run(async()=>{using(client)try{await Serve(client,token);}catch(Exception e)when(e is IOException or SocketException or OperationCanceledException or InvalidDataException or JsonException or FormatException){}},token);
   }
  },token);
 }
 async Task Serve(TcpClient client,CancellationToken token)
 {
  using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(15));
  var stream=client.GetStream();var buffer=new MemoryStream();var chunk=new byte[4096];int headerEnd=-1;
  while(headerEnd<0)
  {
   var n=await stream.ReadAsync(chunk,deadline.Token);if(n==0)return;buffer.Write(chunk,0,n);
   if(buffer.Length>16384)throw new InvalidDataException("header too large");
   headerEnd=IndexOf(buffer.GetBuffer(),(int)buffer.Length,"\r\n\r\n"u8);
  }
  var head=Encoding.ASCII.GetString(buffer.GetBuffer(),0,headerEnd).Split("\r\n");var line=head[0].Split(' ');
  if(line.Length<3)throw new InvalidDataException("bad request");
  var headers=head.Skip(1).Select(h=>h.Split(':',2)).Where(h=>h.Length==2).ToDictionary(h=>h[0].Trim().ToLowerInvariant(),h=>h[1].Trim());
  // DNS rebinding guard: only the literal loopback origin this page is served from.
  if(!headers.TryGetValue("host",out var host)||host!="127.0.0.1:"+Port){await Reply(stream,403,"text/plain","forbidden",deadline.Token);return;}
  var path=line[1];var parts=path.Split('/',StringSplitOptions.RemoveEmptyEntries);
  if(parts.Length<2||parts[0]!="probe"||parts[1].Length!=32){await Reply(stream,404,"text/plain","not found",deadline.Token);return;}
  string browser;lock(pending){if(!pending.TryGetValue(parts[1],out var p)||p.Expires<DateTimeOffset.UtcNow){browser="";}else browser=p.Browser;}
  if(browser.Length==0){await Reply(stream,410,"text/html; charset=utf-8",Page("这个检查链接已失效，请回到 Leyra 再点一次「检查」。",""),deadline.Token);return;}
  if(line[0]=="GET"&&parts.Length==2){await Reply(stream,200,"text/html; charset=utf-8",Page("",parts[1]),deadline.Token);return;}
  if(line[0]=="POST"&&parts.Length==3&&parts[2]=="result")
  {
   if(!headers.TryGetValue("content-length",out var lengthText)||!int.TryParse(lengthText,out var length)||length is <2 or >16384)throw new InvalidDataException("bad length");
   var body=new byte[length];var have=(int)buffer.Length-headerEnd-4;Array.Copy(buffer.GetBuffer(),headerEnd+4,body,0,Math.Min(have,length));
   while(have<length){var n=await stream.ReadAsync(body.AsMemory(have),deadline.Token);if(n==0)return;have+=n;}
   using var doc=JsonDocument.Parse(body);var r=doc.RootElement;
   string? seen=r.TryGetProperty("seen_ip",out var s)&&s.ValueKind==JsonValueKind.String&&IPAddress.TryParse(s.GetString(),out _)?s.GetString():null;
   var candidates=new List<(string,string)>();
   if(r.TryGetProperty("candidates",out var list)&&list.ValueKind==JsonValueKind.Array)
    foreach(var c in list.EnumerateArray().Take(64))
    {
     var ip=c.TryGetProperty("ip",out var i)?i.GetString()??"":"";var type=c.TryGetProperty("type",out var t)?t.GetString()??"":"";
     if(IPAddress.TryParse(ip,out _)&&type is "host" or "srflx" or "prflx" or "relay")candidates.Add((ip,type));
    }
   lock(pending)pending.Remove(parts[1]);
   var verdict=report(browser,seen,candidates);
   await Reply(stream,200,"application/json",Leyra.Windows.Core.LeyraJson.ToText(verdict),deadline.Token);return;
  }
  await Reply(stream,405,"text/plain","method not allowed",deadline.Token);
 }
 static int IndexOf(byte[] data,int length,ReadOnlySpan<byte> marker)=>data.AsSpan(0,length).IndexOf(marker);
 static async Task Reply(NetworkStream stream,int status,string type,string body,CancellationToken token)
 {
  var bytes=Encoding.UTF8.GetBytes(body);
  var head=$"HTTP/1.1 {status} {(status==200?"OK":"Error")}\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nContent-Security-Policy: default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self' https://api.ipify.org https://api64.ipify.org\r\nConnection: close\r\n\r\n";
  await stream.WriteAsync(Encoding.ASCII.GetBytes(head),token);await stream.WriteAsync(bytes,token);
 }
 static string Page(string error,string nonce)=>"""
<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>Leyra 浏览器检查</title>
<style>body{font:15px/1.6 system-ui,"Microsoft YaHei",sans-serif;max-width:560px;margin:12vh auto;padding:0 16px;color:#222}h1{font-size:20px}.ok{color:#137333}.muted{color:#666}</style>
<h1>Leyra 浏览器检查</h1><p id="s">
""" + (error.Length>0?error+"</p>":"""
正在检查这个浏览器会不会绕开独享线路……</p><p class="muted">只收集这个浏览器的网络地址候选，检查完会自动回传给 Leyra，可以关掉这个页面。</p>
<script>
(function(){
 var nonce=__NONCE__,cands=[],seen=null,sent=false;
 function send(){
  if(sent)return;sent=true;
  fetch('/probe/'+nonce+'/result',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({seen_ip:seen,candidates:cands})})
   .then(function(r){return r.json();}).then(function(v){
    var el=document.getElementById('s');
    var ip=v.seen_ip?'<div style="font-size:22px;margin:12px 0"><b>浏览器出口 IP：'+v.seen_ip+'</b></div>':'<div>没能取得浏览器的出口 IP。</div>';
    var cmp=!v.seen_ip?'':v.match?'<div class="ok"><b>✓ 和你在 Leyra 里的出口 IP 一致</b>，网站看到的就是这个 IP。</div>':'<div style="color:#c5221f"><b>✗ 和你的出口 IP（'+(v.exit_ip||'未知')+'）不一致</b>，请回到 Leyra 查看处理办法。</div>';
    var rtc=v.exposure==='real'?'<div style="color:#c5221f">WebRTC 暴露了不是出口的地址。</div>':v.exposure==='lan'?'<div style="color:#b06000">WebRTC 暴露了局域网地址。</div>':v.exposure==='unknown'?'<div style="color:#b06000">未取得完整出口 IP，暂时无法完成比对，请回 Leyra 再试。</div>':'<div class="ok">WebRTC 没有暴露其他地址。</div>';
    el.innerHTML=(v.exposure==='unknown'?'<b style="color:#b06000">检查结果不完整</b>':'<b class="ok">检查完成</b>')+'，结果已经交回 Leyra。'+ip+cmp+rtc+'<p class="muted">这个页面可以关掉了。</p>';})
   .catch(function(){document.getElementById('s').textContent='没能把结果交回 Leyra，请回到 Leyra 再查一次。';});
 }
 function getIp(url){
  var controller=new AbortController(),timer=setTimeout(function(){controller.abort();},20000);
  return fetch(url,{signal:controller.signal,cache:'no-store',credentials:'omit'}).then(function(r){if(!r.ok)throw Error('IP check failed');return r.json();}).then(function(j){if(typeof j.ip!=='string'||!/^[0-9a-fA-F:.]+$/.test(j.ip))throw Error('Invalid IP');return j.ip;}).finally(function(){clearTimeout(timer);});
 }
 // IPv4-only and dual-stack endpoints race: a broken IPv6 path must not hide a valid IPv4 result.
 var ipDone=Promise.any([getIp('https://api.ipify.org?format=json'),getIp('https://api64.ipify.org?format=json')]).then(function(ip){seen=ip;}).catch(function(){});
 var iceDone=new Promise(function(resolve){
  try{
   var pc=new RTCPeerConnection({iceServers:[{urls:['stun:stun.l.google.com:19302','stun:stun.cloudflare.com:3478']}]});
   pc.createDataChannel('probe');
   pc.onicecandidate=function(e){
    if(!e.candidate){resolve();return;}
    var m=/ ([0-9a-fA-F.:]+) \d+ typ (\w+)/.exec(e.candidate.candidate||'');
    if(m)cands.push({ip:m[1],type:m[2]});
   };
   pc.createOffer().then(function(o){return pc.setLocalDescription(o);}).catch(resolve);
  }catch(x){resolve();}
  setTimeout(resolve,8000);
 });
 Promise.all([ipDone,iceDone]).then(send);setTimeout(send,22000);
})();
</script>
""".Replace("__NONCE__",Leyra.Windows.Core.LeyraJson.ToText(nonce)));
 public void Dispose(){stop?.Cancel();listener?.Stop();stop?.Dispose();}
}

