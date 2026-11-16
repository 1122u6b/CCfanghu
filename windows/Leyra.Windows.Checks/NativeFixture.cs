using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Leyra.Windows.Platform;

[SupportedOSPlatform("windows")]
public static class NativeFixture
{
    static async Task ConnectPublic()
    {
        using var socket=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp);
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(4));
        await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse("1.1.1.1"),443),deadline.Token);
    }
    public static async Task Processes()
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Native process fixture requires Windows");
        var host=Environment.ProcessPath??throw new InvalidOperationException("Process host is unknown");
        var prefix=Path.GetFileNameWithoutExtension(host).Equals("dotnet",StringComparison.OrdinalIgnoreCase)?new[]{typeof(NativeFixture).Assembly.Location}:Array.Empty<string>();
        var directory=Path.GetDirectoryName(typeof(NativeFixture).Assembly.Location)!;
        using var job=new NativeJob();
        using(var child=job.Launch(host,prefix.Concat(new[]{"--fixture-job-exit","有 空格"}).ToArray(),directory))
        {
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));await child.WaitForExitAsync(deadline.Token);
            var actual=child.ExitCode;if(actual!=7)throw new Exception($"Native ordinary-argv child exit code: {actual} (0x{actual:X8}), expected 7");
        }
        using(var child=job.Launch(host,prefix.Concat(new[]{"--fixture-job-wait"}).ToArray(),directory))
        {job.Stop();if(!child.WaitForExit(5000))throw new Exception("Job-owned child survived termination");}
        Console.WriteLine("PASS atomic job-owned process launch, Unicode/space argv, exit code and bounded teardown");
        var kernel=Environment.GetEnvironmentVariable("LEYRA_NATIVE_KERNEL");
        if(string.IsNullOrEmpty(kernel))throw new InvalidOperationException("Provide the verified official Windows kernel for native process acceptance");
        using(var child=job.Launch(kernel,new[]{"-v"},Path.GetDirectoryName(kernel)!))
        {
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));await child.WaitForExitAsync(deadline.Token);
            if(child.ExitCode!=0)throw new Exception($"Official kernel did not start successfully: {child.ExitCode}");
        }
        Console.WriteLine("PASS official Windows Mihomo executable launched inside the owned job");
    }
    public static async Task Run()
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Native fixture requires a disposable Windows machine");
        using var engine=new WfpEngine();
        if(engine.DefaultGuardsInstalled())throw new InvalidOperationException("Existing LEYRA guard detected; refusing to replace it");
        // No customer endpoint, credentials, DNS queries or application content.
        await ConnectPublic();
        Console.WriteLine("PASS public TCP fixture reachable before guard");
        var process=Environment.ProcessPath ?? throw new InvalidOperationException("Unknown process path");
        var kernel=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"notepad.exe");
        if(!File.Exists(kernel))throw new FileNotFoundException("Controlled application identity absent",kernel);
        var plan=WfpPlan.Create(kernel,process,[new GatewayEndpoint(new IPEndPoint(IPAddress.Parse("203.0.113.9"),24443),6)],[],0,[]);
        try
        {
            engine.Apply(plan);
            if(!engine.DefaultGuardsInstalled())throw new Exception("Persistent guards missing after installation");
            Console.WriteLine("PASS native WFP transaction and default filter readback");
            var listener=new TcpListener(IPAddress.Loopback,0);
            listener.Start();
            try
            {
                using var c=new TcpClient();
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var accepted=listener.AcceptTcpClientAsync(deadline.Token);
                await c.ConnectAsync(IPAddress.Loopback,((IPEndPoint)listener.LocalEndpoint).Port,deadline.Token);
                using var peer=await accepted;
                await c.GetStream().WriteAsync(new byte[]{0x41},deadline.Token);
                var one=new byte[1];
                if(await peer.GetStream().ReadAsync(one,deadline.Token)!=1 || one[0]!=0x41)throw new Exception("Loopback transfer failed");
                Console.WriteLine("PASS actual loopback transfer under installed WFP guard");
            }
            finally{listener.Stop();}
            var blocked=false;
            try{await ConnectPublic();}
            catch(SocketException){blocked=true;}
            catch(OperationCanceledException){blocked=true;}
            if(!blocked)throw new Exception("Public TCP connection bypassed guard");
            Console.WriteLine("PASS fresh public TCP connection denied under native guard");
        }
        finally{engine.RemoveOwned();}
        if(engine.DefaultGuardsInstalled())throw new Exception("Owned guards remain after cleanup");
        await ConnectPublic();
        Console.WriteLine("PASS own-filter removal and public TCP restoration");
        Console.WriteLine("This controlled fixture does not validate TUN, DNS, WebRTC, suspend/resume or full customer deployment.");
    }
    public static void Cleanup()
    {
        using var engine=new WfpEngine();engine.RemoveOwned();
        if(engine.DefaultGuardsInstalled())throw new Exception("Cleanup failed");
        Console.WriteLine("Owned fixture filters absent");
    }
}
