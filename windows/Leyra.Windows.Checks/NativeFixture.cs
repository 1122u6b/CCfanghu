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
    public static async Task Run()
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Native fixture requires a disposable Windows machine");
        var system=Environment.GetFolderPath(Environment.SpecialFolder.System);
        using(var job=new NativeJob())
        {
            using(var child=job.Launch(Path.Combine(system,"cmd.exe"),["/c","exit 7"],system))
            {
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));await child.WaitForExitAsync(deadline.Token);
                if(child.ExitCode!=7)throw new Exception("Native process exit code mismatch");
            }
            using(var child=job.Launch(Path.Combine(system,"WindowsPowerShell","v1.0","powershell.exe"),["-NoProfile","-NonInteractive","-Command","Start-Sleep -Seconds 30"],system))
            {job.Stop();if(!child.WaitForExit(5000))throw new Exception("Job-owned child survived termination");}
        }
        Console.WriteLine("PASS atomic job-owned process launch, exit code and bounded teardown");
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
