using System.Diagnostics;

namespace Leyra.Windows.Core;

public sealed record DownloadSample(long Bytes,long BytesPerSecond,double Seconds)
{
 public static async Task<DownloadSample> MeasureAsync(Stream input,int maximumBytes,TimeSpan duration,CancellationToken stop=default)
 {
  if(maximumBytes<1||duration<=TimeSpan.Zero)throw new ArgumentOutOfRangeException();
  using var sample=CancellationTokenSource.CreateLinkedTokenSource(stop);sample.CancelAfter(duration);
  var watch=Stopwatch.StartNew();long count=0;var buffer=new byte[Math.Min(32768,maximumBytes)];
  try
  {
   while(count<maximumBytes)
   {
    var read=await input.ReadAsync(buffer.AsMemory(0,(int)Math.Min(buffer.Length,maximumBytes-count)),sample.Token);
    if(read==0)break;count+=read;
   }
  }
  // Reaching our sampling duration is a successful partial sample, not a failed download.
  catch(OperationCanceledException)when(!stop.IsCancellationRequested&&sample.IsCancellationRequested&&count>0){}
  stop.ThrowIfCancellationRequested();
  if(count==0)throw new InvalidDataException("测速没有收到数据");
  var seconds=Math.Max(.001,watch.Elapsed.TotalSeconds);
  return new(count,(long)(count/seconds),seconds);
 }
}
