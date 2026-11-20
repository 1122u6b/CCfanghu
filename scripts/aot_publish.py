"""Publish a Leyra service as machine code, with its own code renamed and its strings hidden first.

1. build for the target system (the output folder then holds every assembly the code refers to),
2. obfuscate Leyra's own assemblies there (scripts/obfuscate.py: one run, so names agree between them),
3. put the obfuscated assemblies back wherever the build left a copy (the machine-code compiler reads those),
4. publish without building again: NativeAOT (PublishAot in the project) compiles the obfuscated code,
5. refuse the result if Leyra's own names can still be read in it.
Usage: aot_publish.py <project folder or .csproj> <runtime id> <output folder> <assembly.dll> [<assembly.dll> ...]
"""
import os, subprocess, sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from obfuscate import obfuscate

ROOT = Path(__file__).resolve().parents[1]
# Names that must never be readable in a shipped service (all from Leyra's own code).
SENTINELS = [b'ObserveExit', b'HoldListed', b'ConfirmExpiry', b'exitVerified', b'VerifyDns']


def run(*args):
    print('+', ' '.join(map(str, args)), flush=True)
    subprocess.run([str(a) for a in args], check=True)


def main(project, rid, out, modules):
    project = Path(project).resolve(); folder = project if project.is_dir() else project.parent
    common = ['-c', 'Release', '-r', rid, '--self-contained', 'true', '-nr:false', '-p:UseSharedCompilation=false', '-p:LeyraOneAssembly=true']
    # A full build: an earlier run left obfuscated copies that an incremental build would take as up to date.
    run('dotnet', 'build', project, *common, '--no-incremental')
    bins = [p for p in (folder / 'bin' / 'Release').glob('*/' + rid) if p.is_dir()]
    if len(bins) != 1: raise SystemExit('build output not found for ' + rid + ': ' + str(bins))
    bin_dir = bins[0]
    obfuscate(bin_dir, modules)
    # Every other copy the build left (this project's obj, the referenced projects' bin and obj).
    for m in modules:
        fresh = (bin_dir / m).read_bytes(); copies = 0
        for base in (ROOT / 'windows', ROOT / 'mac'):
            for p in base.rglob(m):
                parts = set(p.parts)
                if p.parent == bin_dir or 'ref' in parts or 'refint' in parts or not ({'bin', 'obj'} & parts): continue
                if 'Release' not in parts: continue
                p.write_bytes(fresh); copies += 1
        print('obfuscated %s: %d build copies replaced' % (m, copies), flush=True)
    run('dotnet', 'publish', project, *common, '--no-build', '-o', out)
    leaked = []
    for f in Path(out).iterdir():
        if f.is_file() and f.suffix.lower() not in ('.pdb', '.dbg'):
            data = f.read_bytes(); leaked += [f.name + ': ' + s.decode() for s in SENTINELS if s in data]
    for f in Path(out).iterdir():   # symbols would hand the names back
        if f.suffix.lower() in ('.pdb', '.dbg'): f.unlink()
    if (Path(out) / modules[0]).exists(): raise SystemExit(modules[0] + ' was published as .NET code, not machine code')
    if leaked: raise SystemExit('Leyra code still readable: ' + '; '.join(leaked))
    print('machine code published to', out)


if __name__ == '__main__':
    if len(sys.argv) < 5: raise SystemExit(__doc__)
    main(sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4:])
