/* StreamFin Manga: embedded Jellyfin UI, no external runtime or service. */
(() => {
    'use strict';
    if (window.StreamFinManga) return;
    let root, main, session, current = 'library', poll, reader, generation = 0, pageAbort, pageObserver;
    let progressWrites = Promise.resolve();
    let pdfUrl = null;
    let librarySort = 'updated';
    const blobUrls = new Map();
    const coverUrls = new Set(); let coverObserver;
    const element = (tag, cls, text) => { const e = document.createElement(tag); if (cls) e.className = cls; if (text !== undefined) e.textContent = text; return e; };
    const button = (text, action, cls = '') => { const b = element('button', cls, text); b.type = 'button'; b.onclick = async () => { b.disabled = true; try { await action(); } catch (e) { message(e.message, true); } finally { b.disabled = false; } }; return b; };
    const icon = name => { const span = element('span', 'material-icons notranslate', name); span.setAttribute('aria-hidden', 'true'); return span; };
    const iconButton = (name, label, action, cls = '') => { const b = button('', action, ('jm-icon-button ' + cls).trim()); b.append(icon(name)); b.title = label; b.setAttribute('aria-label', label); return b; };
    const sessionHeaders = () => { const headers = {}; ApiClient.setRequestHeaders(headers); return headers; };
    const api = async (path, method = 'GET', body, signal) => {
        const response = await fetch(ApiClient.getUrl('StreamFin/Manga/' + path), { method, credentials: 'same-origin', headers: { ...sessionHeaders(), ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}) }, body: body === undefined ? undefined : JSON.stringify(body), signal });
        if (!response.ok) { const data = await response.json().catch(() => ({})); throw new Error(data.error || data.Message || `Request failed (${response.status})`); }
        return response.status === 204 ? null : response.json();
    };
    function cover(series) {
        const frame = element('div', 'jm-cover'); const fallback = element('span', 'jm-cover-fallback', 'Loading cover…'); frame.append(fallback);
        frame.dataset.coverId = series.id; frame.dataset.coverTitle = series.title;
        if (!coverObserver) coverObserver = new IntersectionObserver(entries => {
            for (const entry of entries) {
                if (!entry.isIntersecting) continue;
                coverObserver.unobserve(entry.target); loadCover(entry.target);
            }
        }, { root, rootMargin: '400px', threshold: 0 });
        coverObserver.observe(frame); return frame;
    }
    async function loadCover(frame) {
        const signal = pageAbort.signal, fallback = frame.querySelector('.jm-cover-fallback');
        try {
            const response = await fetch(ApiClient.getUrl('StreamFin/Manga/covers/' + encodeURIComponent(frame.dataset.coverId)), { credentials: 'same-origin', headers: sessionHeaders(), signal });
            if (!response.ok) throw new Error('Cover unavailable');
            const data = await response.blob(); if (signal.aborted || !frame.isConnected) return;
            const url = URL.createObjectURL(data); coverUrls.add(url);
            const image = element('img'); image.alt = frame.dataset.coverTitle + ' thumbnail'; image.decoding = 'async';
            image.onload = () => { fallback.hidden = true; };
            image.onerror = () => { image.remove(); fallback.hidden = false; fallback.textContent = 'Cover unavailable'; coverUrls.delete(url); URL.revokeObjectURL(url); };
            image.src = url; frame.append(image);
        } catch (error) { if (error.name !== 'AbortError' && frame.isConnected) fallback.textContent = 'Cover unavailable'; }
    }
    // Source names come from the server once; the reader shows "MangaDex" rather than the internal id.
    let sourceNames = new Map();
    async function loadSourceNames() { try { const list = await api('sources'); sourceNames = new Map(list.map(s => [s.id, s.name])); } catch (_) { /* keep ids */ } }
    const sourceName = id => sourceNames.get(id) || id;
    function sourceBadge(series) {
        const badge = element('a', 'jm-badge', 'From ' + sourceName(series.sourceId));
        if (typeof series.url === 'string' && /^https:\/\//.test(series.url)) { badge.href = series.url; badge.target = '_blank'; badge.rel = 'noopener noreferrer'; badge.title = 'Open the series on ' + sourceName(series.sourceId); }
        return badge;
    }
    function message(text, error = false) { if (!main) return; let box = main.querySelector('.jm-status'); if (!box) { box = element('div'); box.setAttribute('role', 'status'); main.prepend(box); } box.className = 'jm-status' + (error ? ' jm-error' : ''); box.textContent = text; }
    function empty(text) { main.append(element('div', 'jm-empty', text)); }
    function heading(title, subtitle) { main.append(element('div', 'jm-kicker', 'YOUR NEXT CHAPTER'), element('h1', '', title)); if (subtitle) main.append(element('p', 'jm-lede', subtitle)); }
    function releaseImages() { if (pdfUrl) { URL.revokeObjectURL(pdfUrl); pdfUrl = null; } coverObserver?.disconnect(); coverObserver = null; for (const url of coverUrls) URL.revokeObjectURL(url); coverUrls.clear(); pageObserver?.disconnect(); pageAbort?.abort(); pageAbort = new AbortController(); for (const value of blobUrls.values()) URL.revokeObjectURL(value); blobUrls.clear(); }
    function close(push = true) { const embedded = root?.dataset.embedded === '1'; generation++; clearInterval(poll); reader = null; releaseImages(); root?.remove(); root = null; main = null; document.removeEventListener('keydown', keys); if (push && !embedded && history.state?.streamfinManga) history.back(); }
    async function open(tab = 'library', options = {}) {
        if (typeof ApiClient === 'undefined' || !ApiClient.accessToken()) return;
        if (tab === 'admin' && !options.host) { location.hash = '#/configurationpage?name=streamfin&tab=manga'; return; }
        if (root) { if ((options.host && root.parentNode === options.host) || (!options.host && root.dataset.embedded !== '1')) { await navigate(tab); return; } close(false); }
        root = element('section'); root.id = 'jm-app'; root.setAttribute('aria-label', 'StreamFin Manga'); if (options.host) { root.className = 'jm-embedded'; root.dataset.embedded = '1'; }
        const header = element('header', 'jm-header'); const brand = element('div', 'jm-brand', 'StreamFin'); brand.append(element('span', '', ' Manga'));
        const nav = element('nav', 'jm-nav'); nav.setAttribute('aria-label', 'Manga navigation'); if (options.host) nav.hidden = true;
        header.append(brand, nav, button('Back to Jellyfin', () => close(), 'jm-close')); main = element('main', 'jm-main'); root.append(header, main); (options.host || document.body).append(root); if (options.host) header.querySelector('.jm-close').hidden = true;
        try {
            session = await api('session');
            for (const [id, label] of (options.host ? [] : [['discover', 'Discover'], ['library', 'My library'], ['recent', 'Recent updates'], ['continue', 'Continue reading'], ['downloads', 'Downloads']])) {
                const b = button(label, () => navigate(id)); b.dataset.tab = id; nav.append(b);
            }
            if (!options.host) history.pushState({ ...(history.state || {}), streamfinManga: true }, '', location.href);
            document.addEventListener('keydown', keys); await navigate(tab);
        } catch (e) { message(e.message, true); }
    }
    async function navigate(tab, seriesId = null) {
        clearInterval(poll); generation++; reader = null; releaseImages(); current = tab; if (!main) return;
        root.querySelector('.jm-header').hidden = false; main.className = 'jm-main'; main.replaceChildren(); root.scrollTop = 0;
        root.querySelectorAll('[data-tab]').forEach(b => { if (b.dataset.tab === tab) b.setAttribute('aria-current', 'page'); else b.removeAttribute('aria-current'); });
        const version = generation;
        try {
            if (tab === 'discover') await discover(version);
            else if (['library', 'recent', 'continue'].includes(tab)) await library(tab, version);
            else if (tab === 'downloads') await downloads(version, seriesId);
            else if (tab === 'admin' && session.admin) await admin('Overview', version);
        } catch (e) { if (version === generation) message(e.message, true); }
    }
    async function discover(version) {
        heading('Search manga', 'Search your enabled sources. Request a series once; new English chapters arrive automatically.');
        const sources = await api('sources'); if (version !== generation) return;
        const form = element('form', 'jm-search'); const input = element('input'); input.placeholder = 'Search manga, manhwa, webtoons…'; input.setAttribute('aria-label', 'Search manga'); input.required = true; input.minLength = 2;
        const select = element('select'); select.setAttribute('aria-label', 'Source'); select.append(new Option('All enabled sources', '')); sources.forEach(s => select.append(new Option(s.name, s.id)));
        const submit = button('Search', () => form.requestSubmit(), 'jm-primary'); form.append(input, select, submit); main.append(form);
        const results = element('div', 'jm-grid'); main.append(results);
        if (!sources.length) empty('No sources are enabled. Ask your administrator to configure and test sources.');
        let searchNumber = 0;
        form.onsubmit = async e => {
            e.preventDefault(); const number = ++searchNumber; submit.disabled = true; results.replaceChildren(); message('Searching sources…');
            try {
                const data = await api('search?query=' + encodeURIComponent(input.value) + (select.value ? '&sourceId=' + encodeURIComponent(select.value) : ''));
                if (version !== generation || number !== searchNumber) return;
                message(`${data.results.length} results${data.errors.length ? ` · ${data.errors.length} sources could not respond` : ''}`);
                for (const s of data.results) {
                    const card = element('article', 'jm-card'); card.append(cover(s), element('div', 'jm-badge', sources.find(x => x.id === s.sourceId)?.name || s.sourceId), element('h3', '', s.title), element('p', '', s.description || 'English chapters · automatic release monitoring'));
                    const downloaded = data.downloadedCounts?.[s.id] ?? 0;
                    if (downloaded > 0) card.append(element('div', 'jm-badge', downloaded === 1 ? '1 chapter downloaded' : downloaded + ' chapters downloaded'));
                    const chapterInfo = element('p', 'jm-muted');
                    chapterInfo.setAttribute('aria-live', 'polite');
                    card.append(chapterInfo);
                    const details = button('Chapter details', async () => {
                        chapterInfo.textContent = 'Loading chapter details…';
                        let info;
                        try {
                            info = await api('search/' + encodeURIComponent(s.id) + '/details');
                        } catch (_) {
                            chapterInfo.textContent = 'Chapter details unavailable. Try again.';
                            return;
                        }
                        chapterInfo.textContent = info.chapterCount + ' English chapter listings' + (info.latestChapter ? ' · Latest: ' + info.latestChapter : '');
                        if (info.firstChapterNumber != null && info.lastChapterNumber != null)
                            chapterInfo.textContent += ' · Listed range: ' + info.firstChapterNumber + '–' + info.lastChapterNumber;
                        if (info.latestChapterDate)
                            chapterInfo.textContent += ' · Source date: ' + new Date(info.latestChapterDate).toLocaleDateString();
                        if (info.missingStart)
                            card.append(element('p', 'jm-error', 'Missing the start · first numbered chapter is ' + info.firstChapterNumber));
                        if (info.description) card.querySelector('p').textContent = info.description;
                        details.remove();
                    });
                    card.append(details);
                    const actions = element('div', 'jm-actions');
                    const progressLink = button('View download progress', () => navigate('downloads', s.id));
                    actions.append(button('Request & monitor', async () => {
                        await api('requests', 'POST', { sourceId: s.sourceId, url: s.url });
                        if (version !== generation || number !== searchNumber) return;
                        message('“' + s.title + '” requested. Its chapters are listed by a background task and appear under Downloads shortly.');
                        actions.append(progressLink);
                    }, 'jm-primary'));
                    card.append(actions);
                    results.append(card);
                }
                if (data.errors.length) { const errors = element('details'); errors.append(element('summary', '', 'Source diagnostics'), element('pre', '', data.errors.map(x => x.sourceId + ': ' + x.error).join('\n'))); main.append(errors); }
            } catch (ex) { if (version === generation) message(ex.message, true); } finally { submit.disabled = false; }
        };
    }
    async function library(tab, version) {
        const continuing = tab === 'continue', recent = tab === 'recent';
        if (continuing) heading('Pick up where you left off.', 'Your progress follows your Jellyfin account across devices.');
        else if (recent) heading('Recent updates', 'See the latest additions to your manga library.');
        else heading('Manga library', 'Downloaded chapters and monitored series from all your sources.');
        await loadSourceNames();
        let data = await api('library');
        if (version !== generation) return;
        if (continuing) data = data.filter(item => item.progress).sort((a, b) => b.progress.updatedAt.localeCompare(a.progress.updatedAt));
        if (recent) data = data.filter(item => item.lastUpdatedAt);
        if (!data.length) {
            empty(continuing ? 'Start reading a chapter to see it here.' : recent ? 'Downloaded updates will appear here.' : 'Your library is waiting. Discover a series to start downloading.');
            return;
        }
        const sort = element('select');
        if (!continuing && !recent) {
            const label = element('label', 'jm-actions', 'Sort library');
            for (const [value, title] of [['updated', 'Recently updated'], ['oldest', 'Oldest update'], ['title', 'Title A–Z'], ['title-desc', 'Title Z–A'], ['chapters', 'Most downloaded chapters']])
                sort.append(new Option(title, value));
            sort.value = librarySort;
            label.append(sort);
            main.append(label);
        }
        const grid = element('div', 'jm-grid');
        main.append(grid);
        const cards = new Map();
        for (const item of data) {
            const card = element('article', 'jm-card');
            // The cover is the main action: it resumes the last chapter, opens the latest addition on Recent, or lists chapters.
            const frame = cover(item.series); frame.classList.add('jm-cover-link'); frame.tabIndex = 0; frame.setAttribute('role', 'button');
            const openCover = recent && item.latestUpdatedChapter ? () => read(item.latestUpdatedChapter.id) : item.progress ? () => read(item.progress.chapterId, item.progress.page, item.progress.bookmarked) : () => chapterList(item.series);
            frame.title = recent && item.latestUpdatedChapter ? 'Read latest addition' : item.progress ? 'Continue reading' : 'View chapters';
            frame.setAttribute('aria-label', frame.title + ': ' + item.series.title);
            frame.onclick = () => openCover().catch(e => message(e.message, true));
            frame.onkeydown = e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); frame.onclick(); } };
            card.append(frame, sourceBadge(item.series), element('h3', '', item.series.title), element('p', 'jm-card-meta', item.downloaded + ' chapters downloaded · ' + (item.monitoring ? 'Monitoring' : 'Not monitoring')));
            if (item.progress) card.append(element('p', 'jm-card-resume', 'Resume · page ' + (item.progress.page + 1)));
            const pending = Object.entries(item.downloadStatus || {})
                .filter(([status]) => status !== 'Complete').reduce((sum, [, count]) => sum + count, 0);
            if (pending > 0) card.append(element('p', 'jm-card-download', pending + ' pending download' + (pending === 1 ? '' : 's')));
            if (item.lastUpdatedAt) {
                const date = element('time', 'jm-muted', new Date(item.lastUpdatedAt).toLocaleString());
                date.dateTime = item.lastUpdatedAt;
                const update = element('p', '', item.archiveDateOnly ? 'Archive updated ' : 'Last downloaded ');
                update.append(date);
                if (item.latestUpdatedChapter?.title) update.append(' · ' + item.latestUpdatedChapter.title);
                card.append(update);
            }
            const actions = element('div', 'jm-actions jm-card-actions');
            if (recent && item.latestUpdatedChapter)
                actions.append(iconButton('play_arrow', 'Read latest addition', () => read(item.latestUpdatedChapter.id)));
            if (item.downloadStatus?.Queued || item.downloadStatus?.Downloading)
                actions.append(iconButton('download', 'View download progress', () => navigate('downloads', item.series.id)));
            if (item.progress)
                actions.append(iconButton('play_arrow', 'Continue reading', () => read(item.progress.chapterId, item.progress.page, item.progress.bookmarked)));
            actions.append(iconButton('format_list_bulleted', item.progress ? 'Chapters' : 'View chapters', () => chapterList(item.series)));
            if (session.admin) actions.append(iconButton('delete', 'Stop monitoring and delete', async () => {
                if (!window.confirm('Stop monitoring and delete downloaded chapters and reading progress for “' + item.series.title + '” from the shared library?')) return;
                await api('library/' + encodeURIComponent(item.series.id), 'DELETE');
                await navigate(tab);
            }));
            card.append(actions);
            cards.set(item.series.id, card);
        }
        function render() {
            const ordered = [...data];
            if (!continuing) ordered.sort((a, b) => {
                const title = a.series.title.localeCompare(b.series.title);
                const order = recent ? 'updated' : librarySort;
                if (order === 'title') return title;
                if (order === 'title-desc') return -title;
                if (order === 'chapters') return b.downloaded - a.downloaded || title;
                if (!a.lastUpdatedAt || !b.lastUpdatedAt)
                    return Number(Boolean(b.lastUpdatedAt)) - Number(Boolean(a.lastUpdatedAt)) || title;
                const byDate = Date.parse(a.lastUpdatedAt) - Date.parse(b.lastUpdatedAt);
                return (order === 'oldest' ? byDate : -byDate) || title;
            });
            grid.replaceChildren(...ordered.map(item => cards.get(item.series.id)));
        }
        sort.onchange = () => { librarySort = sort.value; render(); };
        render();
    }
    async function chapterList(series) {
        generation++; const version = generation; main.replaceChildren(); heading(series.title, series.description); main.append(button('Back to library', () => navigate('library')));
        const jobs = await api('library/' + series.id + '/chapters'); if (version !== generation) return;
        const panel = element('div', 'jm-panel'); main.append(panel);
        for (const job of jobs) {
            const row = element('div', 'jm-row');
            row.append(element('div', '', job.chapter.title));
            if (job.status === 'Complete') {
                const actions = element('div', 'jm-actions');
                actions.append(button('Read', () => read(job.chapter.id)));
                if (session.admin) actions.append(button('Redownload', async () => {
                    await api('admin/downloads/' + encodeURIComponent(job.chapter.id) + '/redownload', 'POST');
                    if (version === generation) await chapterList(series);
                }));
                row.append(actions);
            } else row.append(element('small', '', job.status));
            panel.append(row);
        }
    }
    async function downloads(version, seriesId) {
        main.className = 'jm-main jm-downloads';
        heading('Downloads', seriesId ? 'Downloads for this series.' : 'Everything in the queue.');
        if (seriesId) main.append(button('All downloads', () => navigate('downloads')));

        let filter = 'all', query = '', page = 0, pageSize = 25, jobsCache = [];
        const toolbar = element('div', 'jm-dl-toolbar');
        const chips = element('div', 'jm-dl-chips');
        const search = element('input', 'jm-dl-search'); search.type = 'search';
        search.placeholder = 'Filter series or chapter'; search.setAttribute('aria-label', 'Filter downloads');
        const size = element('select', 'jm-dl-size'); size.setAttribute('aria-label', 'Downloads per page');
        for (const value of [25, 50, 100]) size.append(new Option(value + ' per page', String(value)));
        size.value = String(pageSize);
        const count = element('span', 'jm-dl-count');
        toolbar.append(chips, search, size, count);
        const list = element('div', 'jm-dl-list');
        const pager = element('div', 'jm-dl-pager');
        main.append(toolbar, list, pager);
        let refreshing = false;

        const matches = job => (filter === 'all' ? true
            : filter === 'active' ? (job.status === 'Downloading' || job.status === 'Queued')
                : job.status === filter)
            && (!query || ((job.seriesTitle || '') + ' ' + job.chapter.title + ' ' + job.sourceId).toLowerCase().includes(query));
        const rank = job => job.status === 'Downloading' ? 0 : job.status === 'Queued' ? 1 : job.status === 'Failed' ? 2 : job.status === 'Paused' ? 3 : 4;

        function jobRow(job) {
            const row = element('div', 'jm-dl-row');
            const info = element('div', 'jm-dl-info');
            info.append(element('strong', '', job.chapter.title));
            const meta = element('small', 'jm-muted');
            meta.textContent = (seriesId ? '' : (job.seriesTitle || job.sourceId) + ' · ') + sourceName(job.sourceId);
            info.append(meta);
            const progress = job.progress && job.progress.total > 0 ? job.progress : null;
            const active = job.status === 'Downloading' || job.status === 'Paused' || job.status === 'Queued';
            if (progress && active) {
                const percent = Math.round(progress.done / progress.total * 100);
                const line = element('div', 'jm-dl-progress');
                const bar = element('progress');
                bar.max = progress.total; bar.value = progress.done;
                bar.setAttribute('aria-label', job.chapter.title + ' page progress');
                line.append(bar, element('span', 'jm-dl-pct', percent + '%'), element('span', 'jm-dl-pages', progress.done + ' / ' + progress.total + ' pages'));
                info.append(line);
            } else if (job.status === 'Complete' && job.pages > 0) {
                info.append(element('small', 'jm-dl-pages', job.pages + ' pages'));
            }
            if (job.error && job.status !== 'Complete') info.append(element('small', 'jm-dl-error', job.error));
            const side = element('div', 'jm-dl-side');
            side.append(element('span', 'jm-pill jm-pill-' + job.status.toLowerCase(), job.status));
            if (job.status === 'Complete') side.append(button('Read', () => read(job.chapter.id), 'jm-dl-action'));
            if (job.status === 'Failed' && session.admin) side.append(button('Retry', async () => {
                await api('admin/downloads/' + encodeURIComponent(job.chapter.id) + '/retry', 'POST');
                await refresh();
            }, 'jm-dl-action'));
            row.append(info, side);
            return row;
        }

        function drawToolbar() {
            chips.replaceChildren();
            const counts = { all: jobsCache.length, active: 0 };
            for (const job of jobsCache) {
                counts[job.status] = (counts[job.status] || 0) + 1;
                if (job.status === 'Downloading' || job.status === 'Queued') counts.active++;
            }
            for (const [key, label] of [['all', 'All'], ['active', 'Active'], ['Complete', 'Complete'], ['Failed', 'Failed'], ['Queued', 'Queued']]) {
                const chip = button(label + ' · ' + (counts[key] || 0), () => { filter = key; page = 0; draw(); }, 'jm-dl-chip' + (filter === key ? ' is-active' : ''));
                chip.setAttribute('aria-pressed', String(filter === key));
                chips.append(chip);
            }
        }

        function draw() {
            drawToolbar();
            const visible = jobsCache.filter(matches).sort((a, b) => rank(a) - rank(b)
                || (a.seriesTitle || '').localeCompare(b.seriesTitle || '') || a.chapter.number - b.chapter.number);
            const pages = Math.max(1, Math.ceil(visible.length / pageSize));
            page = Math.min(Math.max(0, page), pages - 1);
            const shown = visible.slice(page * pageSize, page * pageSize + pageSize);
            count.textContent = visible.length + ' of ' + jobsCache.length;
            list.replaceChildren();
            if (!shown.length) list.append(element('div', 'jm-empty', jobsCache.length ? 'Nothing matches this filter.' : 'No downloads requested yet.'));
            else shown.forEach(job => list.append(jobRow(job)));
            pager.replaceChildren();
            if (pages > 1) {
                const previous = button('‹ Previous', () => { page--; draw(); }, 'jm-dl-pagebtn');
                previous.disabled = page === 0;
                const next = button('Next ›', () => { page++; draw(); }, 'jm-dl-pagebtn');
                next.disabled = page >= pages - 1;
                pager.append(previous, element('span', 'jm-dl-page', 'Page ' + (page + 1) + ' of ' + pages), next);
            }
        }

        search.oninput = () => { query = search.value.trim().toLowerCase(); page = 0; draw(); };
        size.onchange = () => { pageSize = Number(size.value) || 25; page = 0; draw(); };

        async function refresh() {
            if (refreshing) return;
            refreshing = true;
            try {
                const jobs = await api('downloads' + (seriesId ? '?seriesId=' + encodeURIComponent(seriesId) : ''));
                if (version !== generation) return;
                jobsCache = jobs;
                draw();
            } finally { refreshing = false; }
        }

        await refresh();
        if (version === generation) poll = setInterval(() => refresh().catch(e => message(e.message, true)), 5000);
    }

    async function admin(section, version = generation) {
        if (!session.admin || root?.dataset.embedded !== '1') throw new Error('Open administration from Jellyfin Dashboard > StreamFin Manga.');
        main.replaceChildren(); heading('Manga control room.', 'Sources, downloads, storage, and access in one place.');
        const tabs = element('nav', 'jm-admin-tabs'); tabs.setAttribute('aria-label', 'Administration');
        for (const label of ['Overview', 'Sources', 'Downloads', 'Monitored series', 'Storage', 'Users', 'Settings', 'Diagnostics']) tabs.append(button(label, () => { generation++; return admin(label); }, label === section ? 'jm-primary' : ''));
        main.append(tabs); const panel = element('section', 'jm-panel'); main.append(panel);
        const overview = await api('admin/overview'); if (version !== generation) return;
        if (section === 'Overview') {
            const stats = element('div', 'jm-stats');
            for (const [label, value] of [['Downloaded', overview.downloads.Complete || 0], ['Queued', overview.downloads.Queued || 0], ['Failed', overview.downloads.Failed || 0], ['Verified sources', overview.verifiedSources ?? 0]]) { const stat = element('div', 'jm-stat'); stat.append(element('b', '', String(value)), element('span', 'jm-muted', label)); stats.append(stat); }
            panel.append(stats, element('p', '', overview.injection ? 'File Transformation: registered' : 'File Transformation: not registered. Install the dependency and restart Jellyfin.'), element('p', '', 'Storage used: ' + (overview.usedBytes == null ? 'Not configured' : (overview.usedBytes / 1073741824).toFixed(2) + ' GiB')));
            if (overview.storageError) panel.append(element('p', 'jm-error', overview.storageError));
            for (const check of overview.checks) panel.append(element('p', 'jm-muted', `${check.sourceId}: ${check.error || (check.download ? 'Complete chapter checked' : 'Download not tested')} · ${new Date(check.checkedAt).toLocaleString()}`));
        } else if (section === 'Diagnostics') { const logs = await api('admin/logs'); if (version !== generation) return; if (!logs.length) panel.append(element('p', 'jm-muted', 'No diagnostic events yet.')); for (const entry of logs) { const row = element('div', 'jm-row'); const info = element('div'); info.append(element('strong', '', entry.kind), element('p', 'jm-muted', entry.message), element('small', '', new Date(entry.at).toLocaleString())); row.append(info); panel.append(row); }
        } else if (section === 'Sources') await sourceAdmin(panel, version);
        else if (section === 'Downloads') {
            const config = await api('admin/settings'); panel.append(button(config.paused ? 'Resume queue & monitoring' : 'Pause queue & monitoring', async () => { await api('admin/settings', 'PUT', { ...config, paused: !config.paused }); await admin(section); }));
            const jobs = await api('downloads'); if (version !== generation) return;
            for (const job of jobs) { const row = element('div', 'jm-row'); const info = element('div'); info.append(element('strong', '', job.chapter.title), element('p', 'jm-muted', job.status + (job.error ? ' · ' + job.error : ''))); const actions = element('div', 'jm-actions'); if (job.status !== 'Complete') for (const action of ['retry', 'pause', 'cancel']) actions.append(button(action, async () => { await api(`admin/downloads/${job.chapter.id}/${action}`, 'POST'); await admin(section); })); row.append(info, actions); panel.append(row); }
        } else if (section === 'Monitored series') {
            for (const s of overview.monitored) { const row = element('div', 'jm-row'); const info = element('div'); info.append(element('strong', '', s.series.title), element('p', 'jm-muted', s.error || (s.lastChecked ? 'Checked ' + new Date(s.lastChecked).toLocaleString() : 'Not checked yet'))); row.append(info, button(s.monitoring ? 'Stop monitoring' : 'Monitor releases', async () => { await api('admin/series/' + s.series.id, 'PUT', { monitoring: !s.monitoring }); await admin(section); })); panel.append(row); }
        } else await settings(panel, overview, section, version);
    }
    function field(container, text, value, type = 'text') { const label = element('label', '', text); const input = element('input'); input.type = type; if (type === 'checkbox') input.checked = Boolean(value); else input.value = value ?? ''; label.append(input); container.append(label); return input; }
    async function settings(panel, overview, section, version) {
        const config = await api('admin/settings'); if (version !== generation) return; const form = element('form', 'jm-form'); panel.append(form);
        const controls = {};
        if (section === 'Settings' || section === 'Storage') {
            const storageLabel = element('label', '', 'Manga storage folder'); const selectedFolder = element('output', 'jm-folder-path', config.storagePath || 'No folder selected'); selectedFolder.value = config.storagePath || ''; selectedFolder.setAttribute('aria-label', 'Selected manga folder'); controls.storagePath = selectedFolder;
            const choose = button('Choose folder', () => directoryPicker(selectedFolder.value, overview.libraries, path => { selectedFolder.value = path; choose.focus(); })); storageLabel.append(selectedFolder, choose); form.append(storageLabel); const label = element('label', '', 'Jellyfin library controlling user access'); const select = element('select'); select.append(new Option('Select library…', '')); for (const lib of overview.libraries) select.append(new Option(lib.name, lib.itemId)); select.value = config.libraryId; label.append(select); form.append(label); controls.libraryId = select;
            controls.storageLimitBytes = field(form, 'Storage limit (GiB, 0 = unlimited)', config.storageLimitBytes / 1073741824, 'number'); controls.storageLimitBytes.min = '0';
        }
        if (section === 'Settings') { controls.enabled = field(form, 'Enable StreamFin Manga', config.enabled ?? false, 'checkbox'); controls.monitorMinutes = field(form, 'Check releases every (minutes)', config.monitorMinutes, 'number'); controls.downloadConcurrency = field(form, 'Global concurrent chapter downloads (1–8)', config.downloadConcurrency, 'number'); controls.downloadConcurrency.min = '1'; controls.downloadConcurrency.max = '8'; controls.downloadConcurrency.step = '1'; form.append(element('p', 'jm-muted', 'The global limit applies across all websites. Set each website’s limit in Sources. Lower limits take effect as active downloads finish.')); controls.convertToAvif = field(form, 'Convert downloaded pages to AVIF', config.convertToAvif ?? true, 'checkbox'); controls.avifQuality = field(form, 'AVIF quality (1–100)', config.avifQuality ?? 65, 'number'); controls.avifQuality.min = '1'; controls.avifQuality.max = '100'; controls.avifQuality.step = '1'; form.append(element('p', 'jm-muted', 'Default quality 65. Keeps full resolution. Requires libaom-av1 in Jellyfin FFmpeg. Existing AVIF and GIF pages are retained; changing quality affects future downloads.')); controls.paused = field(form, 'Pause queue and monitoring', config.paused, 'checkbox'); }
        const blocked = [];
        if (section === 'Users') { form.append(element('p', 'jm-muted', 'Users with access to the configured Jellyfin library can search, read, and request immediately. Block individual accounts below. Administrators retain access.')); for (const user of overview.users) { const input = field(form, 'Block ' + user.username, config.deniedUsers.some(id => id.replaceAll('-', '') === user.id.replaceAll('-', '')), 'checkbox'); blocked.push([user.id, input]); } }
        const save = button('Save settings', () => form.requestSubmit(), 'jm-primary'); form.append(save);
        form.onsubmit = async e => { e.preventDefault(); try { const update = { ...config }; for (const [key, input] of Object.entries(controls)) update[key] = input.type === 'checkbox' ? input.checked : input.type === 'number' ? Number(input.value) * (key === 'storageLimitBytes' ? 1073741824 : 1) : input.value; if (section === 'Users') update.deniedUsers = blocked.filter(x => x[1].checked).map(x => x[0]); await api('admin/settings', 'PUT', update); message('Settings saved.'); } catch (ex) { message(ex.message, true); } };
    }
    async function serverApi(path) {
        const response = await fetch(ApiClient.getUrl(path), { credentials: 'same-origin', headers: sessionHeaders() });
        const text = await response.text(); let data; try { data = text ? JSON.parse(text) : null; } catch (_) { data = text; }
        if (!response.ok) throw new Error(data?.error || data?.Message || 'Jellyfin cannot browse this folder. Check the server account permissions.');
        return data;
    }
    async function directoryPicker(initial, libraries, select) {
        const dialog = element('dialog', 'jm-directory-picker'); dialog.setAttribute('aria-label', 'Choose manga folder on server');
        const title = element('h2', '', 'Choose manga folder'); const help = element('p', 'jm-muted', 'Browse folders on your Jellyfin server.');
        const location = element('output', 'jm-folder-path', 'Server roots'); location.setAttribute('aria-label', 'Current server folder');
        const status = element('p', 'jm-muted'); status.setAttribute('role', 'status'); const list = element('div', 'jm-directory-list'); const toolbar = element('div', 'jm-actions'); const actions = element('div', 'jm-actions');
        let path = '', revision = 0;
        const choose = button('Use this folder', () => { if (path) { select(path); dialog.close(); } }, 'jm-primary'); choose.disabled = true;
        async function visit(next) {
            const request = ++revision; choose.disabled = true; list.replaceChildren(); status.textContent = 'Loading folders...';
            try {
                const items = await serverApi(next ? 'Environment/DirectoryContents?includeDirectories=true&includeFiles=false&path=' + encodeURIComponent(next) : 'Environment/Drives');
                if (!dialog.open || request !== revision) return;
                path = next; location.value = path || 'Server roots'; status.textContent = items.length ? '' : 'No subfolders. You can select this folder.';
                for (const item of items) { const name = item.Name ?? item.name; const folder = item.Path ?? item.path; const entry = button(name, () => visit(folder), 'jm-directory-entry'); const icon = element('span', 'material-icons notranslate', 'folder'); icon.setAttribute('aria-hidden', 'true'); entry.prepend(icon); list.append(entry); }
                choose.disabled = !path;
            } catch (error) { if (request === revision) status.textContent = error.message; }
        }
        toolbar.append(button('Server roots', () => visit('')), button('Up', async () => { if (!path) return; try { await visit(await serverApi('Environment/ParentPath?path=' + encodeURIComponent(path)) || ''); } catch (error) { status.textContent = error.message; } }));
        const shortcuts = element('div', 'jm-actions');
        for (const library of libraries) for (const folder of (library.locations || library.Locations || [])) shortcuts.append(button(library.name || library.Name || folder, () => visit(folder)));
        actions.append(button('Cancel', () => dialog.close()), choose); dialog.append(title, help, toolbar, shortcuts, location, status, list, actions); root.append(dialog);
        dialog.addEventListener('close', () => { revision++; dialog.remove(); }); dialog.showModal(); await visit(initial || '');
    }
    async function sourceAdmin(panel, version) {
        const sources = await api('admin/sources'); if (version !== generation) return;
        const actions = element('div', 'jm-actions'); actions.append(button('Add custom source', () => editSource({ id: '', name: '', baseUrl: 'https://example.org', kind: 'html', enabled: false, delayMilliseconds: 1500, maxPagination: 5, allowedHosts: [], searchPath: '/?s={query}', searchItems: '.result', searchLink: 'a', searchCover: 'img', titleSelector: 'h1', descriptionSelector: '.description', chaptersPath: '{series}', chapterItems: '.chapters a', chapterLink: '', pagesSelector: '.reader img', imageAttribute: 'src' })), button('Export definitions', () => { const blob = new Blob([JSON.stringify(sources, null, 2)], { type: 'application/json' }); const url = URL.createObjectURL(blob); const a = element('a'); a.href = url; a.download = 'streamfin-manga-sources.json'; a.click(); setTimeout(() => URL.revokeObjectURL(url), 1000); }), button('Import definitions', () => { const input = element('input'); input.type = 'file'; input.accept = '.json'; input.onchange = async () => { try { if (!input.files[0] || input.files[0].size > 1024 * 1024) throw new Error('Choose a JSON file smaller than 1 MiB.'); await api('admin/sources/import', 'POST', JSON.parse(await input.files[0].text())); await admin('Sources'); } catch (e) { message(e.message, true); } }; input.click(); })); panel.append(actions);
        for (const source of sources) {
            const row = element('div', 'jm-row'); const info = element('div'); const enabledLabel = element('small', '', source.enabled ? 'Enabled' : 'Disabled'); info.append(element('strong', '', source.name), element('div', 'jm-muted', source.status?.startsWith('Verified') ? 'Verified' : source.status?.startsWith('Failed') ? 'Verification failed' : source.status), enabledLabel); const controls = element('div', 'jm-actions');
            const limitForm = element('form', 'jm-form'); const limit = field(limitForm, 'Max concurrent downloads · ' + source.name, source.maxConcurrentDownloads ?? 1, 'number'); limit.min = '1'; limit.max = '8'; limit.step = '1';
            limitForm.append(button('Save download limit', () => limitForm.requestSubmit()));
            limitForm.onsubmit = async e => { e.preventDefault(); try { await api('admin/sources/' + encodeURIComponent(source.id) + '/downloads', 'PUT', { maxConcurrentDownloads: Number(limit.value) }); source.maxConcurrentDownloads = Number(limit.value); message('Website download limit saved. The global limit and request delay still apply.'); } catch (ex) { message(ex.message, true); } };
            info.append(limitForm);
            const sourceRoute = 'admin/sources/' + encodeURIComponent(source.id);
            const toggle = button(source.enabled ? 'Disable' : 'Enable', async () => {
                Object.assign(source, await api(sourceRoute + '/enabled', 'PUT', !source.enabled));
                enabledLabel.textContent = source.enabled ? 'Enabled' : 'Disabled';
                toggle.textContent = source.enabled ? 'Disable' : 'Enable';
            });
            controls.append(button('Edit / rules', () => editSource(source)), toggle, button('Test', () => testSource(source)), button('Remove', async () => {
                await api(sourceRoute, 'DELETE');
                row.remove();
            }));
            row.append(info, controls);
            panel.append(row);
        }
    }
    function editSource(source) {
        main.replaceChildren(); heading(source.id ? 'Edit source rules.' : 'Add a source.', 'Use CSS selectors for HTML or dotted property paths for JSON. Explicitly allow each image CDN host. Saving resets verification.');
        main.append(button('← Sources', () => admin('Sources'))); const form = element('form', 'jm-panel'); const textarea = element('textarea'); textarea.setAttribute('aria-label', 'Source definition JSON'); textarea.value = JSON.stringify(source, null, 2); form.append(textarea, button('Save source', () => form.requestSubmit(), 'jm-primary')); main.append(form);
        form.onsubmit = async e => { e.preventDefault(); try { const value = JSON.parse(textarea.value); await api('admin/sources/' + encodeURIComponent(value.id), 'PUT', value); await admin('Sources'); message('Source saved. Test it before relying on downloads.'); } catch (ex) { message(ex.message, true); } };
    }
    function testSource(source) {
        main.replaceChildren(); heading('Test ' + source.name, 'Test search, details, chapter extraction, and optionally a complete chapter download. Test archives are deleted afterward.'); main.append(button('← Sources', () => admin('Sources')));
        const form = element('form', 'jm-panel'); const query = field(form, 'Search query', 'One Piece'); const url = field(form, 'Optional exact series URL', ''); const download = field(form, 'Download and validate a complete chapter (requires configured storage)', false, 'checkbox'); const submit = button('Run test', () => form.requestSubmit(), 'jm-primary'); form.append(submit); main.append(form);
        form.onsubmit = async e => { e.preventDefault(); submit.disabled = true; message('Testing source. A full chapter can take several minutes.'); try { const result = await api('admin/sources/' + source.id + '/test', 'POST', { query: query.value, seriesUrl: url.value || null, download: download.checked }); message(result.error || (result.download ? 'Source verified.' : 'Extraction checked. Run a full download test to verify.'), !!result.error); } catch (ex) { message(ex.message, true); } finally { submit.disabled = false; } };
    }
    async function imageFor(chapter, page, signal) {
        const key = chapter + ':' + page; if (blobUrls.has(key)) return blobUrls.get(key);
        const response = await fetch(ApiClient.getUrl(`StreamFin/Manga/chapters/${chapter}/pages/${page}`), { credentials: 'same-origin', headers: sessionHeaders(), signal });
        if (!response.ok) throw new Error('Could not load page ' + (page + 1)); const url = URL.createObjectURL(await response.blob());
        if (signal.aborted) { URL.revokeObjectURL(url); throw new DOMException('Aborted', 'AbortError'); } blobUrls.set(key, url); return url;
    }
    async function read(id, start, bookmarked) {
        generation++; const version = generation; clearInterval(poll); releaseImages(); main.replaceChildren(); main.className = 'jm-reader'; root.querySelector('.jm-header').hidden = true;
        try {
            const manifest = await api('chapters/' + id + '/manifest'); const progress = await api('progress'); if (version !== generation) return;
            const saved = progress.find(p => p.chapterId === id); let preferences = {}; try { preferences = JSON.parse(localStorage.getItem('jm-reader:' + session.userId) || '{}'); } catch (_) { /* invalid local preference */ }
            if (manifest.pdf) {
                const bar = element('div', 'jm-reader-toolbar');
                bar.append(button('Back to library', () => navigate('library')), element('strong', '', manifest.chapter.title), button('Fullscreen', async () => { if (document.fullscreenElement) await document.exitFullscreen(); else await root.requestFullscreen(); }));
                const frame = element('iframe', 'jm-pdf'); frame.title = manifest.chapter.title;
                main.append(bar, frame);
                // The viewer cannot send the Jellyfin token, so fetch with it and hand the browser a blob URL.
                const response = await fetch(ApiClient.getUrl('StreamFin/Manga/chapters/' + encodeURIComponent(id) + '/file'), { credentials: 'same-origin', headers: sessionHeaders() });
                if (!response.ok) throw new Error('The PDF could not be opened.');
                if (pdfUrl) URL.revokeObjectURL(pdfUrl);
                pdfUrl = URL.createObjectURL(await response.blob());
                if (version === generation) frame.src = pdfUrl;
                return;
            }
            reader = { id, chapter: manifest.chapter, total: manifest.pages, page: Math.min(start ?? saved?.page ?? 0, manifest.pages - 1), bookmarked: bookmarked ?? saved?.bookmarked ?? false, mode: preferences.mode || 'rtl', zoom: 100, render: 0 };
            const bar = element('div', 'jm-reader-toolbar'); const mode = element('select'); mode.setAttribute('aria-label', 'Reading mode'); for (const [value, text] of [['rtl', 'Right to left'], ['ltr', 'Left to right'], ['double', 'Double page'], ['vertical', 'Vertical scroll']]) mode.append(new Option(text, value)); mode.value = reader.mode;
            mode.onchange = () => { reader.mode = mode.value; localStorage.setItem('jm-reader:' + session.userId, JSON.stringify({ mode: reader.mode })); drawPages(); };
            bar.append(button('Back to library', () => navigate('library')), element('strong', '', manifest.chapter.title), mode, button('−', () => { reader.zoom = Math.max(50, reader.zoom - 10); drawPages(); }), button('+', () => { reader.zoom = Math.min(200, reader.zoom + 10); drawPages(); }), button('Fullscreen', async () => { if (document.fullscreenElement) await document.exitFullscreen(); else await root.requestFullscreen(); }), button(reader.bookmarked ? 'Bookmarked' : 'Bookmark', async () => { reader.bookmarked = !reader.bookmarked; await saveProgress(); bar.querySelector('[data-bookmark]').textContent = reader.bookmarked ? 'Bookmarked' : 'Bookmark'; })); bar.lastChild.dataset.bookmark = '1';
            const pages = element('div', 'jm-reader-pages'); pages.tabIndex = 0; pages.setAttribute('aria-label', 'Chapter pages');
            let touchX = 0; pages.addEventListener('touchstart', e => { touchX = e.changedTouches[0].clientX; }, { passive: true }); pages.addEventListener('touchend', e => { if (!reader || reader.mode === 'vertical') return; const dx = e.changedTouches[0].clientX - touchX; if (Math.abs(dx) > 70) move((dx < 0 ? 1 : -1) * (reader.mode === 'rtl' ? -1 : 1)); }, { passive: true });
            const bottom = element('div', 'jm-reader-bottom'); const position = element('input'); position.type = 'number'; position.min = '1'; position.max = String(manifest.pages); position.setAttribute('aria-label', 'Page number'); position.style.width = '80px'; position.onchange = () => move(Number(position.value) - 1 - reader.page);
            bottom.append(button('Previous', () => move(-1)), position, element('span', '', '/ ' + manifest.pages), button('Next', () => move(1)), button('Next chapter', nextChapter)); main.append(bar, pages, bottom); await drawPages();
        } catch (e) { if (version === generation) { main.append(button('Back to library', () => navigate('library'))); message(e.message, true); } }
    }
    async function saveProgress() { if (!reader) return; const r = reader; const snapshot = { seriesId: r.chapter.seriesId, chapterId: r.id, page: r.page, bookmarked: r.bookmarked, updatedAt: new Date().toISOString() }; progressWrites = progressWrites.catch(() => {}).then(() => api('progress', 'PUT', snapshot)); await progressWrites; }
    async function drawPages() {
        if (!reader) return; const r = reader; const render = ++r.render; const area = main.querySelector('.jm-reader-pages'); if (!area) return;
        pageObserver?.disconnect(); area.replaceChildren(); area.className = 'jm-reader-pages jm-' + r.mode; main.querySelector('.jm-reader-bottom input').value = r.page + 1;
        const count = r.mode === 'double' && innerWidth > 700 ? 2 : 1;
        if (r.mode === 'vertical') { drawVertical(r, render, area); await saveProgress(); return; }
        const indices = Array.from({ length: Math.min(count, r.total - r.page) }, (_, i) => r.page + i);
        if (r.mode === 'rtl' && count === 2) indices.reverse();
        for (const page of indices) { const img = element('img'); img.alt = 'Page ' + (page + 1); img.dataset.page = page; img.loading = 'lazy'; img.style.width = r.zoom + '%'; area.append(img); imageFor(r.id, page, pageAbort.signal).then(url => { if (reader === r && render === r.render) img.src = url; }).catch(e => { if (e.name !== 'AbortError' && reader === r) message(e.message, true); }); }
        for (const [key, value] of blobUrls) { const [chapter, page] = key.split(':'); if (chapter !== r.id || Math.abs(Number(page) - r.page) > 6) { URL.revokeObjectURL(value); blobUrls.delete(key); } }
        if (r.page + count < r.total) imageFor(r.id, r.page + count, pageAbort.signal).catch(() => {});
        await saveProgress();
    }
    function drawVertical(r, render, area) {
        for (const url of blobUrls.values()) URL.revokeObjectURL(url); blobUrls.clear();
        const slots = [], nearby = new Set(), queued = new Set(), loading = new Map();
        let disposed = false, frame = 0;
        r.pageSizes ??= new Map();
        const alive = () => !disposed && reader === r && r.render === render;
        function discard(page) {
            const key = r.id + ':' + page, url = blobUrls.get(key);
            slots[page].replaceChildren();
            if (url) { URL.revokeObjectURL(url); blobUrls.delete(key); }
        }
        function updateProgress() {
            frame = 0; if (!alive()) return;
            const line = root.getBoundingClientRect().top + Math.min(160, root.clientHeight * .25);
            let low = 0, high = slots.length - 1;
            while (low < high) { const middle = (low + high) >> 1; if (slots[middle].getBoundingClientRect().bottom <= line) low = middle + 1; else high = middle; }
            if (r.page !== low) { r.page = low; main.querySelector('.jm-reader-bottom input').value = low + 1; saveProgress().catch(e => message(e.message, true)); }
        }
        function onScroll() { if (!frame) frame = requestAnimationFrame(updateProgress); }
        function pump() {
            if (!alive()) return;
            while (loading.size < 2 && queued.size) {
                const page = queued.values().next().value; queued.delete(page);
                if (!nearby.has(page) || loading.has(page) || slots[page].querySelector('img')) continue;
                const abort = new AbortController(); loading.set(page, abort);
                imageFor(r.id, page, abort.signal).then(async url => {
                    if (!alive() || !nearby.has(page) || abort.signal.aborted) return;
                    const image = element('img'); image.alt = 'Page ' + (page + 1); image.decoding = 'async'; image.src = url;
                    await image.decode();
                    if (!alive() || !nearby.has(page) || abort.signal.aborted) return;
                    const ratio = image.naturalWidth + ' / ' + image.naturalHeight;
                    r.pageSizes.set(page, ratio); slots[page].style.aspectRatio = ratio;
                    slots[page].replaceChildren(image); onScroll();
                }).catch(error => {
                    if (error.name !== 'AbortError' && alive() && nearby.has(page)) {
                        const retry = button('Retry page ' + (page + 1), () => { slots[page].replaceChildren(); queued.add(page); pump(); });
                        slots[page].replaceChildren(retry);
                    }
                }).finally(() => {
                    loading.delete(page);
                    if (!disposed && !nearby.has(page)) discard(page);
                    if (alive() && nearby.has(page) && abort.signal.aborted) queued.add(page);
                    pump();
                });
            }
        }
        for (let page = 0; page < r.total; page++) {
            const slot = element('div', 'jm-vertical-page'); slot.dataset.page = page;
            slot.setAttribute('aria-label', 'Page ' + (page + 1));
            slot.style.width = r.zoom + '%'; slot.style.aspectRatio = r.pageSizes.get(page) || '2 / 3';
            slots.push(slot); area.append(slot);
        }
        // Keep placeholders for the entire chapter; only nearby pages retain image data.
        const observer = new IntersectionObserver(entries => {
            if (!alive()) return;
            for (const entry of entries) {
                const page = Number(entry.target.dataset.page);
                if (entry.isIntersecting) { nearby.add(page); queued.add(page); }
                else { nearby.delete(page); queued.delete(page); loading.get(page)?.abort(); discard(page); }
            }
            pump(); onScroll();
        }, { root, rootMargin: '1200px 0px', threshold: 0 });
        pageObserver = { disconnect() {
            disposed = true; observer.disconnect(); root?.removeEventListener('scroll', onScroll);
            cancelAnimationFrame(frame); for (const abort of loading.values()) abort.abort();
            for (let page = 0; page < slots.length; page++) discard(page);
        } };
        root.addEventListener('scroll', onScroll, { passive: true });
        root.scrollTop += slots[r.page].getBoundingClientRect().top - root.getBoundingClientRect().top - 65;
        slots.forEach(slot => observer.observe(slot));
    }
    async function move(delta) { if (!reader) return; const step = reader.mode === 'double' && innerWidth > 700 ? 2 : 1; reader.page = Math.max(0, Math.min(reader.total - 1, reader.page + delta * (Math.abs(delta) === 1 ? step : 1))); await drawPages(); }
    async function nextChapter() { if (!reader) return; const r = reader; const jobs = (await api('library/' + r.chapter.seriesId + '/chapters')).filter(j => j.status === 'Complete'); const next = jobs[jobs.findIndex(j => j.chapter.id === r.id) + 1]; if (next) await read(next.chapter.id, 0); else message('You are up to date with downloaded chapters.'); }
    function keys(e) { if (!root || root.querySelector('dialog[open]')) return; if (e.key === 'Escape' && !document.fullscreenElement) { close(); return; } if (!reader || ['INPUT', 'TEXTAREA', 'SELECT'].includes(e.target.tagName)) return; if (e.key === 'ArrowRight' || e.key === 'ArrowLeft') { e.preventDefault(); move((e.key === 'ArrowRight' ? 1 : -1) * (reader.mode === 'rtl' ? -1 : 1)).catch(ex => message(ex.message, true)); } }
    // Scope observers to adding our own entries; never rewrite/remove another plugin's navigation.
    let scheduled = false;
    function headerActions(toolbar) {
        const children = [...toolbar.children];
        const known = children.find(child => child.querySelector('button[aria-label="SyncPlay"], button[aria-label="Search"], button[aria-label="Cast"], button[aria-label="Playback"]'));
        return known ?? children.filter(child => child.querySelector('button')).at(-1) ?? null;
    }
    function mountNavigation() {
        scheduled = false; if (typeof ApiClient === 'undefined' || !ApiClient.accessToken()) return;
        // Jellyfin 12.4 uses a Material UI toolbar; older 10.x builds use .headerRight.
        // SyncPlay's button only renders when SyncPlay is available, so it cannot be the only anchor.
        // One button for the whole page. The anchor changes as SyncPlay or Cast render, and some builds have both header
        // layouts, so the check is page-wide: extra copies are removed and a button in the wrong place is moved.
        const toolbar = document.querySelector('header .MuiToolbar-root') ?? document.querySelector('.MuiToolbar-root');
        const target = toolbar ? headerActions(toolbar) : document.querySelector('.skinHeader .headerRight, .headerRight, .appHeader .headerActions');
        const existing = [...document.querySelectorAll('[data-streamfin-manga-header]')];
        existing.slice(1).forEach(extra => extra.remove());
        if (!target) return;
        const legacy = !toolbar;
        let entry = existing[0];
        if (entry && (entry.dataset.streamfinMangaHeader === 'legacy') !== legacy) { entry.remove(); entry = undefined; }
        if (!entry) {
            if (legacy) {
                entry = button('', () => open('library')); entry.className = 'paper-icon-button-light headerButton headerButtonRight emby-button jm-header-icon';
                const glyph = element('span', 'material-icons notranslate', 'menu_book'); glyph.setAttribute('aria-hidden', 'true'); entry.append(glyph);
            } else {
                // Clone a native toolbar button so MUI's generated padding, icon size and alignment apply unchanged.
                // Prefer a plain icon button: the user menu's button holds an avatar circle we must not clone.
                const donor = [...target.querySelectorAll('button')].find(b => b.querySelector(':scope > svg') && !b.querySelector('.MuiAvatar-root') && !b.hasAttribute('data-streamfin-manga-header'))
                    ?? [...target.querySelectorAll('button')].find(b => b.querySelector('svg') && !b.querySelector('.MuiAvatar-root') && !b.hasAttribute('data-streamfin-manga-header'));
                entry = donor ? donor.cloneNode(true) : element('button', 'MuiButtonBase-root MuiIconButton-root MuiIconButton-colorInherit MuiIconButton-sizeLarge');
                entry.querySelectorAll('.MuiAvatar-root').forEach(node => node.remove());
                entry.querySelectorAll('[id]').forEach(node => node.removeAttribute('id'));
                for (const name of ['aria-controls', 'aria-haspopup', 'aria-expanded', 'aria-pressed']) entry.removeAttribute(name);
                entry.type = 'button';
                const glyph = entry.querySelector('svg');
                const path = '<path fill="currentColor" d="M18 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V4a2 2 0 0 0-2-2zm0 18H6V4h2v8l2.5-1.5L13 12V4h5v16z"></path>';
                if (glyph) glyph.innerHTML = path;
                else entry.innerHTML = '<svg class="MuiSvgIcon-root MuiSvgIcon-fontSizeMedium" focusable="false" aria-hidden="true" viewBox="0 0 24 24">' + path + '</svg>';
                entry.onclick = () => open('library');
            }
            entry.dataset.streamfinMangaHeader = legacy ? 'legacy' : 'toolbar'; entry.title = 'StreamFin Manga'; entry.setAttribute('aria-label', 'StreamFin Manga');
        }
        // Only move when the parent is wrong; reordering inside it would fight other plugins' header buttons.
        if (entry.parentNode !== target) target.prepend(entry);
    }
    const observer = new MutationObserver(() => { if (!scheduled) { scheduled = true; setTimeout(mountNavigation, 150); } }); observer.observe(document.documentElement, { childList: true, subtree: true });
    window.addEventListener('popstate', () => { if (root && root.dataset.embedded !== '1') close(false); });
    window.addEventListener('hashchange', () => { if (root && root.dataset.embedded !== '1') close(false); });
    window.addEventListener('beforeunload', () => releaseImages());
    document.addEventListener('visibilitychange', () => { if (reader && document.hidden) saveProgress().catch(() => {}); });
    window.StreamFinManga = { open, close, closeEmbedded: host => { if (root?.parentNode === host) close(false); } }; mountNavigation();
})();
