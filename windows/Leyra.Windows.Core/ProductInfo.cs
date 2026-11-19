namespace Leyra.Windows.Core;
/// <summary>Single source of the product version: agent, desktop, installer and update checks.</summary>
public static class ProductInfo
{
 public const string Version="0.4.19";
 /// <summary>Semantic-version order; a pre-release sorts before its release.</summary>
 public static int Compare(string a,string b)
 {
  static (int[] N,string Pre) Split(string v){var dash=v.IndexOf('-');var core=dash<0?v:v[..dash];var n=core.Split('.').Select(x=>int.TryParse(x,out var i)?i:0).Concat([0,0,0]).Take(3).ToArray();return (n,dash<0?"":v[(dash+1)..]);}
  var x=Split(a);var y=Split(b);
  for(var i=0;i<3;i++)if(x.N[i]!=y.N[i])return x.N[i].CompareTo(y.N[i]);
  if(x.Pre==y.Pre)return 0;if(x.Pre.Length==0)return 1;if(y.Pre.Length==0)return -1;return string.CompareOrdinal(x.Pre,y.Pre);
 }
}

