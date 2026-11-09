import json
import pathlib
import shutil
import zipfile
import xml.etree.ElementTree as ET

root = pathlib.Path(__file__).resolve().parents[1]
output = root / 'artifacts/plugin'
output.mkdir(parents=True, exist_ok=True)
for name in ['Jellyfin.Plugin.StreamFin.dll', 'AngleSharp.dll']:
    shutil.copy2(root / 'src/bin/Release/net10.0' / name, output / name)
DESCRIPTION = 'Anime, movies, TV, lossless music and manga from online sources, as Jellyfin channels.'
OVERVIEW = 'Stream and read instead of storing: anime, movies, TV, music and manga inside Jellyfin.'
TARGET_ABI = '12.3.0.0'
version = ET.parse(root / 'src/StreamFin.csproj').findtext('.//Version')
manifest = {
    'guid': 'a74fdb2e-938f-483b-b2ef-f527e14ff724',
    'name': 'JellySteamHub', 'description': DESCRIPTION, 'overview': OVERVIEW,
    'owner': 'ItzDabbzz (Forked from mmrmagno)', 'category': 'Channels', 'version': version, 'targetAbi': TARGET_ABI,
    'imagePath': 'streamfin.png', 'status': 'Active', 'autoUpdate': True,
}
(output / 'meta.json').write_text(json.dumps(manifest, indent=2) + '\n')
with zipfile.ZipFile(root / f'artifacts/StreamFin-{version}.zip', 'w', zipfile.ZIP_DEFLATED) as archive:
    for name in ['Jellyfin.Plugin.StreamFin.dll', 'AngleSharp.dll', 'meta.json']:
        archive.write(output / name, name)
    archive.write(root / 'assets/banner.png', 'streamfin.png')
    for name in ['LICENSE', 'THIRD_PARTY_NOTICES.txt']:
        archive.write(root / name, name)
print(f'Packaged artifacts/StreamFin-{version}.zip')
