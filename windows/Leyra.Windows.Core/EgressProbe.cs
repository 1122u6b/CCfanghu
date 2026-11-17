using System.Net.Sockets;
using System.Security.Authentication;

namespace Leyra.Windows.Core;

public static class EgressProbe
{
 public static async Task<string> ObserveAsync(Func<CancellationToken,Task<string>> primary,Func<CancellationToken,Task<string>> fallback,CancellationToken stop)
 {
  stop.ThrowIfCancellationRequested();
  string ip;
  try{ip=await primary(stop);}
  catch(Exception error)when(TransportFailure(error,stop)){ip=await fallback(stop);}
  stop.ThrowIfCancellationRequested();
  if(!AddressPolicy.IsLiteral(ip)||!AddressPolicy.IsPublic(ip))throw new InvalidDataException("没有取得有效公网出口");
  return ip;
 }
 static bool TransportFailure(Exception error,CancellationToken stop)
 {
  if(stop.IsCancellationRequested)return false;
  for(Exception? inner=error;inner is not null;inner=inner.InnerException)
   if(inner is AuthenticationException or InvalidDataException||inner is HttpRequestException{StatusCode:not null})return false;
  return error is OperationCanceledException or IOException or SocketException||error is HttpRequestException{InnerException:IOException or SocketException};
 }
}
