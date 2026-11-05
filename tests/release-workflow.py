"""Exercise release shell steps with mocked GitHub/git commands; never publishes."""
import json
import os
import pathlib
import shutil
import subprocess
import tempfile
import unittest
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
WORKFLOW = (ROOT / '.github/workflows/release.yml').read_text()
GIT_BASH = pathlib.Path('C:/Program Files/Git/bin/bash.exe')
BASH = str(GIT_BASH) if GIT_BASH.exists() else shutil.which('bash')


def step(name):
    body = WORKFLOW.split('      - name: ' + name + '\n', 1)[1].split('        run: |\n', 1)[1]
    lines = []
    for line in body.splitlines():
        if line.strip() and not line.startswith('          '):
            break
        lines.append(line[10:])
    return '\n'.join(lines)


MOCKS = r'''
export RUNNER_TEMP="$PWD/runner" GITHUB_ENV="$PWD/env" GH_TOKEN=test-token
export GITHUB_REPOSITORY=example/streamfin TAG=v0.12.0.1 VERSION=0.12.0.1
mkdir -p "$RUNNER_TEMP"
git() { case "$1" in fetch) return 0;; tag) echo 'Release notes';; *) return 99;; esac; }
gh() {
  printf '%s\n' "$*" >> calls.txt
  case "$1 $2" in
    'release view')
      if [ "$3" = "$TAG" ]; then [ "$SCENARIO" = existing ]; return; fi
      echo manifest.json;;
    'release list') [ "$SCENARIO" != previous ] || echo v0.11.0.0;;
    'release download') mkdir -p "$RUNNER_TEMP/previous"; printf '[]' > "$RUNNER_TEMP/previous/manifest.json";;
    'release create'|'release upload') return 0;;
    *) return 98;;
  esac
}
python3() { python "$@"; }
'''


@unittest.skipUnless(BASH, 'Bash is required to test workflow shell steps')
class ReleaseWorkflow(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='streamfin-release-check-')
        self.root = pathlib.Path(self.temp.name)
        (self.root / 'src').mkdir()
        (self.root / 'src/StreamFin.csproj').write_text('<Project><PropertyGroup><Version>0.12.0.1</Version></PropertyGroup></Project>')
        (self.root / 'scripts').mkdir()
        shutil.copy2(ROOT / 'scripts/manifest.py', self.root / 'scripts/manifest.py')
        (self.root / 'artifacts').mkdir()
        meta = dict(guid='test-guid', name='StreamFin', description='Test', overview='Test',
                    owner='Test', category='Channels', version='0.12.0.1', targetAbi='12.3.0.0')
        with zipfile.ZipFile(self.root / 'artifacts/StreamFin-0.12.0.1.zip', 'w') as package:
            package.writestr('meta.json', json.dumps(meta))
        (self.root / 'artifacts/StreamFin-0.12.0.1.zip.sigstore.json').write_text('{}')

    def tearDown(self):
        self.temp.cleanup()

    def run_step(self, name, scenario='first', prefix=''):
        env = dict(os.environ, SCENARIO=scenario)
        return subprocess.run([BASH, '--noprofile', '--norc', '-e', '-c', MOCKS + prefix + '\n' + step(name)],
                              cwd=self.root, env=env, capture_output=True, text=True)

    def test_branch_tag_with_matching_version_is_accepted(self):
        result = self.run_step('Check the tag matches the project version')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn('VERSION=0.12.0.1', (self.root / 'env').read_text())

    def test_mismatched_version_is_rejected(self):
        result = self.run_step('Check the tag matches the project version', prefix='TAG=v9.9.9.9')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('does not match version', result.stdout)

    def test_first_release_creates_manifest_and_uploads_all_assets(self):
        result = self.run_step('Publish the release with an updated manifest.json')
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        manifest = json.loads((self.root / 'artifacts/manifest.json').read_text())
        self.assertEqual(manifest[0]['versions'][0]['version'], '0.12.0.1')
        calls = (self.root / 'calls.txt').read_text()
        self.assertNotIn('release download', calls)
        self.assertIn('release create', calls)
        self.assertIn('StreamFin-0.12.0.1.zip.sigstore.json artifacts/manifest.json --clobber', calls)

    def test_previous_manifest_is_downloaded(self):
        result = self.run_step('Publish the release with an updated manifest.json', 'previous')
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn('release download v0.11.0.0', (self.root / 'calls.txt').read_text())

    def test_existing_release_refreshes_every_asset(self):
        result = self.run_step('Publish the release with an updated manifest.json', 'existing')
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        calls = (self.root / 'calls.txt').read_text()
        self.assertNotIn('release create', calls)
        self.assertIn('release upload v0.12.0.1 artifacts/StreamFin-0.12.0.1.zip ', calls)


if __name__ == '__main__':
    unittest.main()
