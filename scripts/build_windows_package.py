"""Cross-publish native Windows binaries and compile an offline NSIS installer.
This builds an INTERNAL candidate. Windows native acceptance is a separate required step.
"""
import argparse,hashlib,json,os,shutil,subprocess
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
def main():
 p=argparse.ArgumentParser();p.add_argument('--dotnet',default='dotnet');p.add_argument('--makensis',default='makensis');a=p.parse_args()
 import re
 version=re.search(r'Version="([^"]+)"',(ROOT/'windows/Leyra.Windows.Core/ProductInfo.cs').read_text()).group(1)
 payload=ROOT/'artifacts/payload'
 if payload.exists():shutil.rmtree(payload)
 payload.mkdir(parents=True)
 for project in ['Desktop','Agent']:
  subprocess.run([a.dotnet,'publish',str(ROOT/f'windows/Leyra.Windows.{project}'),'-c','Release','-r','win-x64','--self-contained','true','-o',str(payload),'-m:1','-nr:false','-p:UseSharedCompilation=false'],check=True)
 # The service (Leyra.Agent.exe) is compiled to machine code (PublishAot in its project file). The window's code
 # stays .NET: renamed and with its strings hidden (scripts/obfuscate.py).
 if (payload/'Leyra.Agent.dll').exists():raise SystemExit('the service was not compiled to machine code')
 from obfuscate import obfuscate
 obfuscate(payload,['LeyraConnect.dll','Leyra.Windows.Core.dll','Leyra.Windows.Platform.dll'])
 for f in payload.glob('*.pdb'):f.unlink()
 from prepare_kernel import prepare
 prepare('windows',payload,ROOT/'artifacts/downloads')
 shutil.copy2(ROOT/'THIRD-PARTY-NOTICES.txt',payload/'THIRD-PARTY-NOTICES.txt')
 prerequisites=ROOT/'artifacts/prerequisites';runtime=prerequisites/'MicrosoftEdgeWebview2Setup.exe'
 if not runtime.is_file():raise SystemExit('Provide the official Microsoft WebView2 bootstrapper (MicrosoftEdgeWebview2Setup.exe) in artifacts/prerequisites')
 digest=hashlib.sha256(runtime.read_bytes()).hexdigest()
 manifest=json.loads((prerequisites/'runtime-origin.json').read_text())
 if manifest['sha256']!=digest:raise SystemExit('Offline runtime checksum mismatch')
 output=ROOT/f'artifacts/Leyra-{version}-setup.exe'
 subprocess.run([a.makensis,'-V2','-INPUTCHARSET','UTF8',f'-DVERSION={version}',f'-DPAYLOAD={payload}',f'-DPREREQUISITES={prerequisites}',f'-DWEBVIEW_SHA256={digest}',f'-DOUTPUT={output}','LeyraConnect.nsi'],cwd=ROOT/'installer',check=True)
 (output.with_suffix('.sha256')).write_text(hashlib.sha256(output.read_bytes()).hexdigest()+'  '+output.name+'\n')
 print('Installer built:',output)
 print('Publish to clients after Windows acceptance: python3 server/admin.py publish-update',version,output)
if __name__=='__main__':main()
