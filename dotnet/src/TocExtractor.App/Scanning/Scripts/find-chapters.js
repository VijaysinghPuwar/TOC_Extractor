() => {
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
    return null;
  };
  const numberInUrl = (path) => {
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
  const lists = [];
  const pages = [];
  const firsts = [];
  for (const a of document.querySelectorAll('a[href]')) {
    let url;
    try { url = new URL(a.getAttribute('href'), document.baseURI); } catch (e) { continue; }
    if (!/^https?:$/.test(url.protocol) || url.hostname !== here.hostname) continue;
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
    if (parts.length === 0) continue;
    const folder = parts.slice(0, -1).join('/');
    const number = numberIn(text) ?? numberIn(tip) ?? numberInUrl(url.pathname);
    if (number === null) continue;
    // The part of the last segment before the chapter number, so one book's
    // chapters group together (book-12_1, book-12_2) and another book's
    // "latest chapter" teaser (book-99_890) does not join them.
    const last = parts[parts.length - 1];
    const at = [...last.matchAll(/\d+/g)].filter(m => Number(m[0]) === number).pop();
    const stem = at ? last.slice(0, at.index) : last.replace(/\d+/g, '#');
    const key = url.hostname + '/' + folder + '|' + stem;
    if (!groups.has(key)) groups.set(key, new Map());
    const group = groups.get(key);
    if (!group.has(href)) {
      group.set(href, { url: href, number, title: text || tip, key });
    }
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
  return JSON.stringify({
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
