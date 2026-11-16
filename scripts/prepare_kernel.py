"""Fetch official portable kernels; verify pinned release archives before extraction."""
import argparse,gzip,hashlib,io,json,os,urllib.request,zipfile
from pathlib import Path
VERSION='v1.19.33'
RELEASES={
 'windows':('mihomo-windows-amd64-compatible-'+VERSION+'.zip','7aaf636a4f2172dcd8ec34ac4f52797b059195e5afd6409508c963e545f8864d'),
 'linux':('mihomo-linux-amd64-compatible-'+VERSION+'.gz','386508bfb1c72c43b6a0eda73708ecdce82546581b0ebcb16366c53757c622b6')}
def prepare(platform,destination,cache):
 name,expected=RELEASES[platform];url='https://github.com/MetaCubeX/mihomo/releases/download/'+VERSION+'/'+name
 cache.mkdir(parents=True,exist_ok=True);archive=cache/name
 if not archive.exists():
  temporary=archive.with_suffix(archive.suffix+'.part')
  try:
   with urllib.request.urlopen(url,timeout=30) as response,temporary.open('wb') as output:
    while chunk:=response.read(1024*1024):
     output.write(chunk)
     if output.tell()>64*1024*1024:raise ValueError('Release exceeds allowed size')
   temporary.replace(archive)
  finally:temporary.unlink(missing_ok=True)
 raw=archive.read_bytes()
 if hashlib.sha256(raw).hexdigest()!=expected:raise ValueError('Official release archive checksum mismatch')
 destination.mkdir(parents=True,exist_ok=True)
 if platform=='windows':
  with zipfile.ZipFile(io.BytesIO(raw)) as z:
   candidates=[i for i in z.infolist() if i.filename.lower().endswith('.exe')]
   if len(candidates)!=1 or candidates[0].file_size>100*1024*1024:raise ValueError('Unexpected executable archive layout')
   binary=z.read(candidates[0])
  filename='mihomo.exe'
 else:binary=gzip.decompress(raw);filename='mihomo'
 target=destination/filename;target.write_bytes(binary)
 if platform=='linux':target.chmod(0o755)
 license_url='https://raw.githubusercontent.com/MetaCubeX/mihomo/'+VERSION+'/LICENSE'
 with urllib.request.urlopen(license_url,timeout=30) as response:license_text=response.read(100000)
 if b'GNU GENERAL PUBLIC LICENSE' not in license_text:raise ValueError('Unexpected upstream license')
 (destination/'MIHOMO-LICENSE.txt').write_bytes(license_text)
 (destination/'kernel-origin.json').write_text(json.dumps({'version':VERSION,'platform':platform,'source':url,'source_code':'https://github.com/MetaCubeX/mihomo/tree/'+VERSION,'archive_sha256':expected,'binary_sha256':hashlib.sha256(binary).hexdigest()},indent=2)+'\n')
 print('Verified official kernel:',VERSION,platform)
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('platform',choices=RELEASES);p.add_argument('destination',type=Path);p.add_argument('--cache',type=Path,default=Path('artifacts/downloads'));a=p.parse_args();prepare(a.platform,a.destination,a.cache)
