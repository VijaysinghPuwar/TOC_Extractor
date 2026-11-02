"""Build a local book of N chapters from corpus.jsonl.gz.

The corpus is the 61 chapters of Pride and Prejudice (1817, public domain,
from Wikisource). Chapters are reused in order, so chapter i carries corpus
chapter (i - 1) % 61 and every machine serves byte-identical pages.

    python3 make_site.py OUT_DIR [CHAPTERS]
"""

import gzip
import html
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))


def main() -> None:
    out = sys.argv[1]
    count = int(sys.argv[2]) if len(sys.argv) > 2 else 2334
    with gzip.open(os.path.join(HERE, "corpus.jsonl.gz"), "rt", encoding="utf-8") as f:
        corpus = [json.loads(line)["paragraphs"] for line in f]

    os.makedirs(os.path.join(out, "ch"), exist_ok=True)
    links = []
    for i in range(1, count + 1):
        paras = "".join(f"<p>{html.escape(p)}</p>" for p in corpus[(i - 1) % len(corpus)])
        with open(os.path.join(out, "ch", f"{i}.html"), "w", encoding="utf-8", newline="\n") as f:
            f.write(
                f"<!doctype html><html><head><title>Chapter {i}</title></head><body>"
                f"<h1 class='title'>Chapter {i}</h1><article class='reader'>{paras}</article>"
                "</body></html>"
            )
        links.append(f"<li><a href='/ch/{i}.html'>Chapter {i}</a></li>")

    with open(os.path.join(out, "index.html"), "w", encoding="utf-8", newline="\n") as f:
        f.write("<!doctype html><html><body><ol class='toc'>")
        f.write("".join(links))
        f.write("</ol></body></html>")


if __name__ == "__main__":
    main()
