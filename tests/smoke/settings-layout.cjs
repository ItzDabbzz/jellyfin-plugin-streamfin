// Local-only settings regression checks. Uses mocked Jellyfin APIs; never contacts a server.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || process.argv[2] || 'playwright-core');

(async () => {
  const html = fs.readFileSync(path.join(__dirname, '../../src/Configuration.html'), 'utf8');
  const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || (process.platform === 'win32' ? 'C:/Program Files/Google/Chrome/Application/chrome.exe' : '/usr/bin/chromium'), headless: true });
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  const config = { Enabled: true, StreamingNavigationMode: 'Channels', AnimeProviderFallback: true, MetadataCacheMinutes: 30, MusicCacheMb: 4096, CacheRevision: 4,
    AllowedUserIds: ['ABCDEFAB-1234-5678-9012-ABCDEFABCDEF'], SubsonicPasswords: [{ UserId: 'ABCDEFAB-1234-5678-9012-ABCDEFABCDEF', Password: 'existing-password' }],
    MusicPlayOrder: ['youtube', 'monochrome'], MusicSearchOrder: ['deezer', 'monochrome'],
    StreamingLibraryLinks: [{ LibraryId: '11111111-1111-1111-1111-111111111111', Category: 'movies' }] };
  const users = [{ Id: 'abcdefab123456789012abcdefabcdef', Name: 'Alex' }, { Id: '12345678123456781234123456789012', Name: 'Morgan' }];
  async function load(fail = false, libraryFail = false) {
    await page.evaluate(({ config, users, fail, libraryFail }) => {
      window.savedSettings = null;
      window.ApiClient = {
        getPluginConfiguration: async () => { if (fail) throw new Error('mock load failure'); return structuredClone(config); },
        getUsers: async () => users,
        serverAddress: () => 'https://example.invalid', getUrl: route => route,
        updatePluginConfiguration: async (_, value) => { window.savedSettings = structuredClone(value); return {}; },
        ajax: async options => {
          if (options.url === 'Library/VirtualFolders') {
            if (libraryFail) throw new Error('mock library failure');
            return [{ ItemId: '11111111111111111111111111111111', Name: 'Local movies', CollectionType: 'movies' }, { ItemId: '22222222222222222222222222222222', Name: 'TV <not markup>', CollectionType: 'tvshows' }];
          }
          return options.url.includes('MusicSaves') ? { Folder: '', Libraries: [], Saves: [] } : { Monochrome: { State: 'Healthy' }, Deezer: { State: 'Healthy' }, YouTube: { State: 'Healthy' } };
        }
      };
      window.Dashboard = { showLoadingMsg() {}, hideLoadingMsg() {}, processPluginConfigurationUpdateResult() {} };
    }, { config, users, fail, libraryFail });
    await page.setContent(html);
    await page.locator('#streamfinConfig').evaluate(element => element.dispatchEvent(new Event('viewshow')));
    await page.waitForFunction(() => document.querySelector('#streamfinStatus').textContent !== 'Loading settings...');
  }
  async function save() {
    await page.locator('#streamfinForm').evaluate(form => form.requestSubmit());
    await page.waitForFunction(() => window.savedSettings !== null);
    return page.evaluate(() => window.savedSettings);
  }
  try {
    await load();
    const initialHash = await page.evaluate(() => location.hash);
    await page.getByRole('link', { name: 'User access', exact: true }).click();
    assert.equal(await page.evaluate(() => location.hash), initialHash);
    console.log('PASS section navigation scrolls without changing the Jellyfin route hash');
    assert.equal(await page.locator('#streamfinUsers .streamfinUser').count(), 2);
    assert.equal(await page.locator('#streamfinUsers input:checked').count(), 1);
    assert.equal(await page.getByLabel('App password for Alex', { exact: true }).inputValue(), 'existing-password');
    let saved = await save();
    assert.deepEqual(saved.AllowedUserIds, [users[0].Id]);
    assert.deepEqual(saved.SubsonicPasswords, [{ UserId: users[0].Id, Password: 'existing-password' }]);
    assert.deepEqual(saved.MusicPlayOrder, ['youtube', 'monochrome']);
    assert.deepEqual(saved.MusicSearchOrder, ['deezer', 'monochrome']);
    assert.equal(saved.AnimeProviderFallback, true);
    assert.equal(saved.StreamingLibraryName, 'StreamFin');
    assert.equal(await page.getByLabel('Local movies', { exact: true }).inputValue(), 'movies');
    assert.equal(await page.getByLabel('TV <not markup>', { exact: true }).inputValue(), '');
    assert.deepEqual(saved.StreamingLibraryLinks, [{ LibraryId: '11111111111111111111111111111111', Category: 'movies' }]);
    console.log('PASS unchanged save preserves allowed users, associated app passwords, source order and fallback');

    const morgan = page.locator('.streamfinUser').filter({ hasText: 'Morgan — allow StreamFin access' });
    await morgan.locator('input[type=checkbox]').check();
    await morgan.getByRole('button', { name: 'New password', exact: true }).click();
    const generated = await page.getByLabel('App password for Morgan', { exact: true }).inputValue();
    assert.equal(generated.length, 24);
    await page.locator('#animeProviderFallback').uncheck();
    await page.locator('#streamingNavigationMode').selectOption('Both');
    await page.getByLabel('Streaming library name', { exact: true }).fill('  Online media  ');
    await page.getByLabel('TV <not markup>', { exact: true }).selectOption('anime');
    await page.locator('#playOrder .streamfinDown:enabled').click();
    await page.evaluate(() => { window.savedSettings = null; });
    saved = await save();
    assert.deepEqual(saved.AllowedUserIds, users.map(user => user.Id));
    assert.equal(saved.SubsonicPasswords.find(value => value.UserId === users[1].Id).Password, generated);
    assert.equal(saved.AnimeProviderFallback, false);
    assert.equal(saved.StreamingNavigationMode, 'Both');
    assert.equal(saved.StreamingLibraryName, 'Online media');
    assert.deepEqual(saved.MusicPlayOrder, ['monochrome', 'youtube']);
    assert.equal(saved.StreamingLibraryLinks.find(link => link.LibraryId === '22222222222222222222222222222222').Category, 'anime');
    await morgan.getByRole('button', { name: 'Remove', exact: true }).click();
    await page.evaluate(() => { window.savedSettings = null; });
    saved = await save();
    assert.equal(saved.SubsonicPasswords.length, 1);
    assert.equal(saved.SubsonicPasswords[0].UserId, users[0].Id);
    console.log('PASS per-user access, password generation/removal, source reorder, navigation and fallback changes save correctly');

    // Minimal host sizing emulates Jellyfin input/button widths, not its full theme.
    await page.addStyleTag({ content: 'body{margin:0;font:16px sans-serif;background:#181818;color:white}.content-primary{padding:24px}input:not([type=checkbox]),select{box-sizing:border-box;width:100%;padding:12px;font:inherit}button{padding:12px}label{display:block}.fieldDescription{font-size:.9em}a{color:inherit}' });
    for (const width of [1440, 390, 320]) {
      await page.setViewportSize({ width, height: 1000 });
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), true, 'horizontal overflow at ' + width);
      assert.equal(await page.locator('.streamfinNav a').count(), 5);
    }
    console.log('PASS 1440px, 390px and 320px responsive geometry (mock host theme)');
    await page.locator('#streamfinConfig').evaluate(element => element.dispatchEvent(new Event('viewhide')));
    await load(false, true);
    assert.equal(await page.locator('#streamfinSave').isEnabled(), true);
    assert.match(await page.locator('#streamfinLibraryLinksStatus').innerText(), /Existing library links will be kept/);
    saved = await save();
    assert.deepEqual(saved.StreamingLibraryLinks, config.StreamingLibraryLinks);
    console.log('PASS library names are text, mappings load/save without auto-enabling, and library-load failure preserves saved links');
    await page.locator('#streamfinConfig').evaluate(element => element.dispatchEvent(new Event('viewhide')));
    await load(true);
    assert.equal(await page.locator('#streamfinSave').isDisabled(), true);
    assert.equal(await page.locator('#streamfinRefresh').isDisabled(), true);
    assert.equal(await page.locator('#streamfinClearSearch').isDisabled(), true);
    await page.locator('#streamfinForm').evaluate(form => form.dispatchEvent(new Event('submit', { cancelable: true })));
    assert.equal(await page.evaluate(() => window.savedSettings), null);
    assert.deepEqual(errors, []);
    console.log('PASS failed settings load disables actions and blocks accidental overwrite; no browser script errors');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
