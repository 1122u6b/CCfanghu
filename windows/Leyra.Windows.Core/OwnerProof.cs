using System.Security.Cryptography;
using System.Text;
namespace Leyra.Windows.Core;

public sealed class OwnerProof(TimeProvider? time=null)
{
 readonly TimeProvider clock=time??TimeProvider.System;
 readonly object gate=new();string? token;long granted;
 public string Grant(){lock(gate){token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));granted=clock.GetTimestamp();return token;}}
 public bool Consume(string? supplied)
 {lock(gate){
  var expected=token;
  if(expected is null || supplied is null || clock.GetElapsedTime(granted)>TimeSpan.FromSeconds(30)) {token=null;return false;}
  if(supplied.Length!=expected.Length || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected),Encoding.ASCII.GetBytes(supplied)))return false;
  token=null;return true;
 }}
}
