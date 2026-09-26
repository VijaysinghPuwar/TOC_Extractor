async () => {
  // Finds the chapter list on a novel page, with no selector supplied.
  //
  // Every same-site link is read with its text, its tooltip and its address.
  // Links are grouped by the folder part of their address, since a book's
  // chapters live together (/book/chapter-1, /book/chapter-2, ...). Within a
  // group, only links that name a chapter number count. The group with the
  // most numbered chapters wins. Links inside the page header, footer and
  // navigation, and "latest release" teasers that repeat a chapter, cannot
  // win because duplicates are counted once.
  //
  // Also reports links that look like "all chapters" pages and numbered list
  // pages, so the scanner can go and read the rest of the list.
  const numberIn = (text) => {
    if (!text) return null;
    const named = text.match(/(?:chapter|chap|ch\.?|episode|ep\.?|第)\s*[#:.-]?\s*(\d{1,6})/i);
    if (named) return Number(named[1]);
    // A title that starts with its number: "12. The Gate", "12 - The Gate".
    const leading = text.match(/^(\d{1,5})\s*[.:)\-–]\s*\S/);
    if (leading) return Number(leading[1]);
    return null;
  };
  // "/chapter/4815162/the-gate": the number is the site's id for the chapter,
  // not its place in the book, and the last part is a free-text name.
  const idFolder = /\/(?:chapter|chap|ch|episode|ep)s?\/\d+\/[^/]+\/?$/i;
  const numberInUrl = (path) => {
    if (idFolder.test(path)) return null;
    const named = path.match(/(?:chapter|chap|ch|episode|ep)[-_/]?(\d{1,6})(?!.*(?:chapter|chap|ch|episode|ep)[-_/]?\d)/i);
    if (named) return Number(named[1]);
    const tail = path.match(/[_-](\d{1,6})(?:\.html?)?\/?$/i);
    return tail ? Number(tail[1]) : null;
  };
  // Zero-width characters sit between words on some sites ("Chapter\u200c 2310").
  const clean = (s) => (s || '').replace(/[\u200B-\u200D\u2060\uFEFF]/g, '').replace(/\s+/g, ' ').trim();
  const here = new URL(location.href);
  here.hash = '';

  const groups = new Map();
  const candidates = [];
  const lists = [];
  const pages = [];
  const firsts = [];
  const byId = new Map();
  const consider = (a) => {
    let url;
    try { url = new URL(a.getAttribute('href'), document.baseURI); } catch (e) { return; }
    if (!/^https?:$/.test(url.protocol) || url.hostname !== here.hostname) return;
    url.hash = '';
    const text = clean(a.innerText || a.textContent);
    const tip = clean(a.getAttribute('title'));
    const href = url.href;

    // A link to the full list says so: it names chapters or contents. "Show
    // all" alone is not enough, since "Show all comments" says the same.
    // The whole link text must name a list: "Chapter 417: ..." is a chapter.
    if (/^(?:(?:all|full|complete|view all|show all|see all|more|novel)\s+)?(?:chapters(?:\s+(?:list|index))?|chapter\s+(?:list|index)|table of contents|contents|toc)$/i.test(text)
        || /\/(?:chapters|chapter-list|toc|contents)\/?(?:$|[?#])/i.test(url.pathname + url.search)) {
      if (href !== here.href) lists.push({ url: href, text });
    }
    // "Start reading" and "First chapter" buttons lead to chapter 1, which a
    // newest-first list may never show.
    if (/^(?:start reading|read now|read first(?: chapter)?|first chapter|first|begin reading|read from (?:the )?(?:start|beginning)|read chapter 1|chapter 1)$/i.test(text)
        || /^first chapter$/i.test(tip)) {
      firsts.push({ url: href, text: text || tip });
    }
    // Candidate list pages: numbered or next/last links. The scanner keeps
    // only those whose address follows the same pattern as each other, and
    // stops paging once a page adds nothing new, so comment pages and year
    // tags that also look like numbers cost at most one visit.
    if (/^\d{1,4}$/.test(text) || /^(?:next|next page|›|»|>|>>|last|last page|»»)$/i.test(text)) {
      pages.push({ url: href, text, pattern: href.replace(/\d+/g, '#') });
    }

    const parts = url.pathname.split('/').filter(Boolean);
    if (parts.length === 0) return;
    const number = numberIn(text) ?? numberIn(tip) ?? numberInUrl(url.pathname);
    // An id address groups by everything but its id and name; its place in
    // the book is settled below, from the ids, when its title has no number.
    // The same chapter can be linked twice ("Start reading", then its row
    // in the list): one entry, with the number and title from whichever says them.
    if (idFolder.test(url.pathname)) {
      const known = byId.get(href);
      if (known) {
        if (known.number === null && number !== null) { known.number = number; known.title = text || tip; }
        return;
      }
      const id = Number(parts[parts.length - 2]);
      const folder = parts.slice(0, -2).join('/');
      const entry = { url: href, number, id, title: text || tip, keys: [url.hostname + '/' + folder + '/#|*'] };
      byId.set(href, entry);
      candidates.push(entry);
      return;
    }
    if (number === null) return;
    const folder = parts.slice(0, -1).join('/');
    // The part of the last segment before the chapter number, so one book's
    // chapters group together (book-12_1, book-12_2) and another book's
    // "latest chapter" teaser (book-99_890) does not join them. The number
    // can appear twice ("chapter-1-number-1"), so every reading is kept and
    // the one most of the book's chapters share is chosen below.
    const last = parts[parts.length - 1];
    const ats = [...last.matchAll(/\d+/g)].filter(m => Number(m[0]) === number);
    const stems = ats.length ? ats.map(m => last.slice(0, m.index)) : [last.replace(/\d+/g, '#')];
    const keys = [...new Set(stems)].map(stem => url.hostname + '/' + folder + '|' + stem);
    candidates.push({ url: href, number, title: text || tip, keys });
  };
  for (const a of document.querySelectorAll('a[href]')) consider(a);

  // A list the page pages through by itself (numbered buttons with no
  // address) shows only some of its chapters; the page as the site sent it
  // usually holds them all. Read once per page, not on every settle pass.
  const clientPager = [...document.querySelectorAll('a:not([href]), button, li[data-page], [role=button]')]
    .filter(el => /^\d{1,4}$/.test(clean(el.innerText || el.textContent))).length >= 2;
  if (clientPager) {
    try {
      if (!window.__tocExtractorServed || window.__tocExtractorServedFor !== location.href) {
        window.__tocExtractorServedFor = location.href;
        window.__tocExtractorServed = fetch(location.href, { credentials: 'include' })
          .then(r => (r.ok ? r.text() : ''))
          .catch(() => '');
      }
      const served = new DOMParser().parseFromString(await window.__tocExtractorServed, 'text/html');
      for (const a of served.querySelectorAll('a[href]')) consider(a);
    } catch (e) { /* the rendered page is all there is */ }
  }

  // Id addresses: ids rise with publication, so a title without a number,
  // or numbers that disagree with the ids, are numbered by their place.
  const withIds = candidates.filter(c => c.id !== undefined);
  for (const key of new Set(withIds.map(c => c.keys[0]))) {
    const members = withIds.filter(c => c.keys[0] === key).sort((x, y) => x.id - y.id);
    const agree = members.every((c, i) => c.number !== null && (i === 0 || c.number > members[i - 1].number));
    if (!agree) members.forEach((c, i) => { c.number = i + 1; });
  }
  for (let i = candidates.length - 1; i >= 0; i--) if (candidates[i].number === null) candidates.splice(i, 1);

  // Each link joins the reading of its address that the most links share.
  const votes = new Map();
  for (const c of candidates) for (const k of c.keys) votes.set(k, (votes.get(k) || 0) + 1);
  for (const c of candidates) {
    const lockedKey = window.__tocExtractorKey || null;
    const key = lockedKey && c.keys.includes(lockedKey)
      ? lockedKey
      : c.keys.reduce((a, b) => (votes.get(b) > votes.get(a) ? b : a));
    if (!groups.has(key)) groups.set(key, new Map());
    const group = groups.get(key);
    if (!group.has(c.url)) group.set(c.url, { url: c.url, number: c.number, title: c.title, key });
  }

  // The winning group, or the group the scanner already locked onto: once
  // the book's own chapter address pattern is known, a later page counts only
  // for links that follow it, so another book's links cannot join.
  const locked = window.__tocExtractorKey || null;
  let best = [];
  for (const [key, group] of groups) {
    if (locked && key !== locked) continue;
    const items = [...group.values()];
    const distinct = new Set(items.map(i => i.number)).size;
    if (distinct > new Set(best.map(i => i.number)).size) best = items;
  }

  const meta = document.querySelector('meta[property="og:title"]');
  // The first line of the heading: sites list alternative names under it.
  const firstLine = (s) => clean((s || '').split('\n').find(line => clean(line).length > 0));
  const title = firstLine((document.querySelector('h1') || {}).innerText)
    || clean(meta && meta.getAttribute('content'))
    || clean(document.title).replace(/\s+[-|–]\s+[^-|–]+$/, '');
  // Page titles wrap the name in site words: "Read <name> RAW English Translation".
  const bookTitle = title
    .replace(/\s+[-|–]\s+[^-|–]+$/, '')
    .replace(/^read\s+/i, '')
    .replace(/\s+(?:raw\s+)?(?:english\s+)?(?:translation|novel|online|free)(?:\s+(?:online|free))?$/i, '')
    .trim() || title;
  const text = document.body ? document.body.innerText.slice(0, 4000) : '';

  // How many chapters the site says the book has, when it says so: its own
  // data (window.__DATA__.count_all), or "Translated: 334 chapters". Only a
  // single, unambiguous figure counts.
  let total = null;
  try {
    const data = window.__DATA__;
    if (data && Number.isFinite(Number(data.count_all)) && Number(data.count_all) > 0) total = Number(data.count_all);
  } catch (e) { /* no such data */ }
  if (total === null) {
    const said = [...text.matchAll(/translated\s*[:：]?\s*(\d[\d,]{0,6})\s*chapters?/gi)]
      .map(m => Number(m[1].replace(/,/g, '')));
    if (new Set(said).size === 1) total = said[0];
  }

  return JSON.stringify({
    total,
    title: bookTitle,
    key: best.length ? best[0].key : null,
    chapters: best,
    lists,
    pages,
    firsts,
    challenge: /just a moment|verify you are (?:a )?human|checking your browser|cf-chl|g-recaptcha|h-captcha/i
      .test(document.title + ' ' + text) && best.length === 0,
    signIn: !!document.querySelector('input[type=password]') && best.length === 0,
  });
}
