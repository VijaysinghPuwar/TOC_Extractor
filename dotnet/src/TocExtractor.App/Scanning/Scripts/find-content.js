() => {
  // Finds the parts of a chapter page, with no selector supplied: the block
  // holding the story, the chapter title, and the next and previous links.
  // Returns CSS selectors for each, built to match the same part on the
  // book's other chapter pages (ids and stable class names, not positions).
  // Zero-width characters sit between words on some sites ("Chapter\u200c 2310").
  const clean = (s) => (s || '').replace(/[\u200B-\u200D\u2060\uFEFF]/g, '').replace(/\s+/g, ' ').trim();
  // A number, or a Roman one ("Part XI").
  const chapterWord = /(?:chapter|chap|ch\.?|episode|ep\.?|page|part|第)\s*[#:.-]?\s*(?:\d|[IVXLC]+\b)/i;

  const unique = (selector) => {
    try { return document.querySelectorAll(selector).length === 1; } catch (e) { return false; }
  };
  const stableClasses = (el) => [...el.classList].filter(c =>
    /^[a-z][\w-]{1,40}$/i.test(c) && !/\d{3,}|active|selected|show|open|hidden|js-|font_|mt-|mb-|pt-|pb-|col-|row|d-/i.test(c));
  const selectorFor = (el) => {
    if (!el) return null;
    if (el.id && /^[a-z][\w-]{0,60}$/i.test(el.id) && !/\d{4,}/.test(el.id) && unique('#' + el.id)) return '#' + el.id;
    const tag = el.tagName.toLowerCase();
    const classes = stableClasses(el);
    for (let n = 1; n <= classes.length; n++) {
      const candidate = tag + classes.slice(0, n).map(c => '.' + CSS.escape(c)).join('');
      if (unique(candidate)) return candidate;
    }
    const parent = el.parentElement;
    if (parent && parent !== document.body) {
      const above = selectorFor(parent);
      if (above) {
        const candidate = above + ' > ' + (classes.length ? tag + '.' + CSS.escape(classes[0]) : tag);
        if (unique(candidate)) return candidate;
      }
    }
    // Not unique: the same button at the top and bottom of a chapter. The
    // fullest class chain still names the right thing, and the first match is
    // as good as the second.
    return classes.length ? tag + classes.map(c => '.' + CSS.escape(c)).join('') : null;
  };

  // The story: the block with the most paragraph text of its own, less the
  // text of any links inside it. Paragraph text counts from <p> children,
  // from <div> children that hold only text (some sites write each paragraph
  // as a div), and from bare text between <br> tags.
  const textOnly = (el) => el.tagName === 'DIV'
    && !el.querySelector('div, p, section, article, ul, ol, table, h1, h2, h3, h4, h5, h6, img, iframe, form, nav, button');
  let content = null, best = 0;
  for (const el of document.querySelectorAll('article, main, section, div, td')) {
    let own = 0;
    for (const child of el.children) {
      if (child.tagName === 'P' || textOnly(child)) own += (child.innerText || '').length;
    }
    for (const node of el.childNodes) {
      if (node.nodeType === 3) own += node.textContent.trim().length;
    }
    let linked = 0;
    for (const a of el.querySelectorAll('a')) linked += (a.innerText || '').length;
    const score = own - 2 * linked;
    if (score > best) { best = score; content = el; }
  }

  // The title: the smallest heading that names a chapter, so a title block
  // holding the book name and the chapter name yields the chapter name.
  // Else the first heading.
  // Only headings a reader can see: not a hidden label, and not part of a
  // link, button, form or menu ("Find" on a search button is not a title).
  const visible = (el) => !!(el.offsetParent || el.getClientRects().length)
    && getComputedStyle(el).visibility !== 'hidden'
    && !el.closest('a, button, form, nav, [hidden], [aria-hidden="true"]');
  const headings = [...document.querySelectorAll('h1, h2, h3, h4, h5, h6, [class*=title], [class*=Title]')]
    .filter(h => clean(h.innerText).length > 0 && clean(h.innerText).length < 300 && visible(h));
  const naming = headings.filter(h => chapterWord.test(clean(h.innerText)))
    .sort((x, y) => clean(x.innerText).length - clean(y.innerText).length);
  const level = (h) => /^H[1-6]$/.test(h.tagName) ? Number(h.tagName[1]) : 7;
  // A part the site itself calls the chapter's title, when no heading names a chapter.
  const labelled = headings.find(h => /chapter[-_ ]?(?:title|name)/i.test(h.getAttribute('class') || ''));
  const titled = naming[0] || labelled || [...headings].sort((x, y) => level(x) - level(y))[0] || null;

  // Next and previous: rel first, then ids, classes and words that say so.
  const link = (words, rel) => {
    const direct = document.querySelector(`a[rel~="${rel}"][href]:not([href^="javascript"]):not([href="#"])`);
    if (direct) return { el: direct, selector: `a[rel~="${rel}"]` };
    for (const a of document.querySelectorAll('a[href]')) {
      const label = clean(a.innerText) + ' ' + (a.id || '') + ' ' + (a.getAttribute('class') || '')
        + ' ' + (a.getAttribute('title') || '') + ' ' + (a.getAttribute('aria-label') || '');
      const href = a.getAttribute('href') || '';
      if (!href || href.startsWith('#') || /^javascript:/i.test(href)) continue;
      if (words.test(label)) return { el: a, selector: selectorFor(a) };
    }
    return null;
  };
  const next = link(/(?:^|\b|_|-)(?:next|nextchap|next[-_ ]chapter|下一章|›|»|→)(?:\b|_|-|$)/i, 'next');
  const prev = link(/(?:^|\b|_|-)(?:prev|previous|prevchap|prev[-_ ]chapter|上一章|‹|«|←)(?:\b|_|-|$)/i, 'prev');
  const hrefOf = (found) => {
    if (!found) return null;
    const raw = found.el.getAttribute('href') || '';
    if (!raw || raw.startsWith('#') || /^javascript:/i.test(raw)) return null;
    try { return new URL(raw, document.baseURI).href; } catch (e) { return null; }
  };

  // The page's own address. A "first chapter" button redirects, and the
  // address a page claims for itself survives that where the one typed does not.
  const canonical = document.querySelector('link[rel="canonical"]')?.href
    || document.querySelector('meta[property="og:url"]')?.getAttribute('content') || null;

  // Part of the chapter, then a sign-in wall: the rest is for members.
  const lockWords = /log ?in to (?:access|read|continue|unlock|view)|sign in to (?:access|read|continue|unlock|view)|unlock (?:this|the) chapter|this chapter is locked|register to (?:read|continue)/i;
  const locked = [...document.querySelectorAll('div, section, p, span, h2, h3, h4')].some(el => {
    const text = (el.innerText || '').trim();
    if (text.length > 400 || !lockWords.test(text)) return false;
    const box = el.closest('section, div') || el;
    return !!box.querySelector('a[href*="login"], a[href*="signin"], a[href*="sign-in"], a[href*="auth"], button, form, input[type=password]');
  });

  return JSON.stringify({
    canonical,
    locked,
    content: selectorFor(content),
    contentChars: content ? clean(content.innerText).length : 0,
    title: selectorFor(titled),
    titleText: titled ? clean(titled.innerText).split(' ').slice(0, 30).join(' ') : clean(document.title),
    next: next ? next.selector : null,
    nextUrl: hrefOf(next),
    prev: prev ? prev.selector : null,
    prevUrl: hrefOf(prev),
    challenge: /just a moment|verify you are (?:a )?human|checking your browser/i.test(document.title),
  });
}
