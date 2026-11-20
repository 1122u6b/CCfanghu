"""Rename Leyra's own code (types, methods, fields) and hide its strings with Obfuscar (MIT, free), in place.

Only Leyra's assemblies are touched, all in one run so names stay consistent between them. Kept as they are:
property names (JSON sent to the interface and the server is built from them), enum values, P/Invoke
methods, and types marked [Obfuscation(Exclude=true)] (records read back from JSON files).
Usage: obfuscate.py <published folder> <assembly.dll> [<assembly.dll> ...]
"""
import os, shutil, subprocess, sys
from pathlib import Path
from xml.sax.saxutils import quoteattr

ROOT = Path(__file__).resolve().parents[1]
VERSION = '2.2.50'


def tool():
    path = ROOT / 'artifacts/tools'
    exe = path / ('obfuscar.console.exe' if os.name == 'nt' else 'obfuscar.console')
    if not exe.exists():
        subprocess.run(['dotnet', 'tool', 'install', '--tool-path', str(path), 'Obfuscar.GlobalTool', '--version', VERSION], check=True)
    return exe


def obfuscate(folder, modules):
    folder = Path(folder).resolve(); out = folder / '.obfuscated'
    if out.exists(): shutil.rmtree(out)
    xml = ['<?xml version="1.0"?>', '<Obfuscator>']
    for k, v in [('InPath', str(folder)), ('OutPath', str(out)), ('KeepPublicApi', 'false'), ('HidePrivateApi', 'true'),
                 ('RenameProperties', 'false'), ('RenameEvents', 'false'), ('RenameFields', 'true'), ('SkipEnums', 'true'),
                 ('HideStrings', 'true'), ('UseUnicodeNames', 'false'), ('SuppressIldasm', 'true'), ('LogFile', str(out / 'map.txt'))]:
        xml.append('  <Var name=%s value=%s />' % (quoteattr(k), quoteattr(v)))
    xml.append('  <AssemblySearchPath path=%s />' % quoteattr(str(folder)))
    for m in modules:
        # Anonymous types are written to JSON, which needs their constructor's parameter names.
        xml.append('  <Module file=%s>' % quoteattr(str(folder / m)))
        xml.append('    <SkipType name="*AnonymousType*" skipMethods="true" skipFields="true" skipProperties="true" />')
        xml.append('  </Module>')
    for m in modules:
        if not (folder / m).is_file(): raise SystemExit('missing assembly to obfuscate: ' + m)
    xml.append('</Obfuscator>')
    config = folder / '.obfuscar.xml'; config.write_text('\n'.join(xml) + '\n', encoding='utf-8')
    env = dict(os.environ, DOTNET_ROLL_FORWARD='Major')
    try:
        subprocess.run([str(tool()), str(config)], check=True, env=env)
        for m in modules: shutil.copy2(out / m, folder / m)
        for m in modules:   # debug symbols would hand the original names back
            pdb = folder / (Path(m).stem + '.pdb')
            if pdb.exists(): pdb.unlink()
    finally:
        config.unlink(missing_ok=True)
        # The name map stays out of the shipped folder (kept next to the build for reading crash logs).
        if (out / 'map.txt').exists():
            keep = ROOT / 'artifacts/obfuscation-maps'; keep.mkdir(parents=True, exist_ok=True)
            shutil.copy2(out / 'map.txt', keep / (folder.name + '-' + Path(modules[0]).stem + '.txt'))
        shutil.rmtree(out, ignore_errors=True)
    print('obfuscated:', ', '.join(modules))


if __name__ == '__main__':
    if len(sys.argv) < 3: raise SystemExit(__doc__)
    obfuscate(sys.argv[1], sys.argv[2:])
