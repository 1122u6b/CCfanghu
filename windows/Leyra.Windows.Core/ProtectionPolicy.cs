namespace Leyra.Windows.Core;
public enum AuditVerdict { Passed,Warning,Failed,Skipped }
public enum ProtectionPhase { Disconnected,Starting,Protected,GuardedFailure,UnprotectedFailure,Paused,RecoveryRequired }
public sealed record ProtectionEvidence(bool Kernel,bool Tunnel,bool Filters,bool Dns,bool ExitMatches,DateTimeOffset Heartbeat)
{
    public bool Fresh(DateTimeOffset now)=>Heartbeat<=now+TimeSpan.FromSeconds(5) && now-Heartbeat<=TimeSpan.FromSeconds(12);
    public ProtectionPhase Assess(DateTimeOffset now,bool paused=false)
    {
        if (paused)return ProtectionPhase.Paused;
        if (!Filters || !Fresh(now))return ProtectionPhase.UnprotectedFailure;
        return Kernel && Tunnel && Dns && ExitMatches ? ProtectionPhase.Protected : ProtectionPhase.GuardedFailure;
    }
}
public sealed record AuditItem(string Id,string Group,string Name,AuditVerdict Verdict,string Detail);
public static class AuditSummary
{
    public static (int Passed,int Warning,int Failed,int Skipped) Count(IEnumerable<AuditItem> items)
    {
        var list=items.ToArray();return (list.Count(x=>x.Verdict==AuditVerdict.Passed),list.Count(x=>x.Verdict==AuditVerdict.Warning),list.Count(x=>x.Verdict==AuditVerdict.Failed),list.Count(x=>x.Verdict==AuditVerdict.Skipped));
    }
}
public static class RoutingPolicy
{
    public static readonly string[] PrivateNetworks=["127.0.0.0/8","10.0.0.0/8","172.16.0.0/12","192.168.0.0/16"];
    public static string[] Rules(IEnumerable<DirectRule>? direct=null)=>(direct??[]).Select(x=>"IP-CIDR,"+x.Cidr+",DIRECT,no-resolve").Concat(PrivateNetworks.Select(x=>"IP-CIDR,"+x+",DIRECT,no-resolve")).Concat(["MATCH,出口","MATCH,REJECT"]).ToArray();
}

