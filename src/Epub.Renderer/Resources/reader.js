(function () {
    'use strict';

    var WRAPPER_ID = '__epub_reader_content';
    var SIDE_MARGIN = 48;        // px reading margin on each side
    var TOP_MARGIN = 24;         // px reading margin top and bottom
    var IMG_VERTICAL_SLACK = 60; // extra px to cap image height by, so ancestor wrapper margins don't push the image past column height
    var GAP = SIDE_MARGIN * 2;   // column-gap absorbs the would-be padding without leaking

    var css = [
        'html, body { width: 100%; height: 100%; margin: 0 !important; padding: 0 !important; overflow: hidden !important; touch-action: manipulation; }',
        '#' + WRAPPER_ID + ' {',
        '  height: calc(100vh - ' + (TOP_MARGIN * 2) + 'px);',
        '  margin: ' + TOP_MARGIN + 'px ' + SIDE_MARGIN + 'px;',
        '  column-gap: ' + GAP + 'px;',
        '  column-fill: auto;',
        '  transform: translateX(0);',
        '  transition: transform 200ms ease-out;',
        '}',
        'pre, blockquote, figure, h1, h2, h3, h4, h5, h6, table, aside { break-inside: avoid; }',
        'pre { overflow-x: auto; max-width: 100%; }',
        // Strip figure margin to keep cover/title figures from exceeding the column height.
        // Specificity (1,1,1) — the [id] attr selector matches our wrapper trivially but adds
        // a class-level point so we tie (or beat) publisher rules like #sbo-rt-content figure.IMG.
        '#' + WRAPPER_ID + '[id] figure { margin: 0 !important; padding: 0 !important; }',
        'img, table, video, iframe { max-width: 100%; height: auto; }',
        // Cap image height with slack so ancestor wrapper margins (.cover div etc.) don't push past column.
        '#' + WRAPPER_ID + '[id] img { display: block; max-height: calc(100vh - ' + (TOP_MARGIN * 2 + IMG_VERTICAL_SLACK) + 'px) !important; width: auto; object-fit: contain; }'
    ].join('\n');

    var PROSE_BLOCK_TAGS = {
        'p': 1, 'blockquote': 1, 'li': 1, 'pre': 1,
        'h1': 1, 'h2': 1, 'h3': 1, 'h4': 1, 'h5': 1, 'h6': 1,
        'td': 1, 'th': 1, 'figcaption': 1,
        // Some publisher templates (Engineering a Compiler etc.) wrap each
        // paragraph in a <div> rather than a <p>. Include div as a fallback
        // so right-click finds something instead of falling through to
        // Chromium's default selection-context menu.
        'div': 1
    };

    function findEnclosingProseBlock(start) {
        var node = start;
        while (node && node !== document.body) {
            if (node.nodeType === 1) {
                var tag = (node.localName || '').toLowerCase();
                if (PROSE_BLOCK_TAGS[tag]) return node;
            }
            node = node.parentNode;
        }
        return null;
    }

    function post(msg) {
        if (window.chrome && window.chrome.webview && window.chrome.webview.postMessage) {
            window.chrome.webview.postMessage(msg);
        }
    }

    var Reader = {
        currentPage: 0,
        pageCount: 1,
        wrapper: null,
        // Cached search-hit target. Populated by the #__find_ hash branch and
        // re-resolved on every recomputeAndShow so that image-loading reflows
        // (which shrink the chapter from N pages to M) don't strand us on the
        // wrong page. Cleared on user navigation so a later window resize
        // doesn't snap them back.
        lastFindHit: null,

        init: function () {
            var style = document.createElement('style');
            style.textContent = css;
            document.head.appendChild(style);

            // Wrap all body children so the wrapper (not body) is the column container.
            var wrapper = document.getElementById(WRAPPER_ID);
            if (!wrapper) {
                wrapper = document.createElement('div');
                wrapper.id = WRAPPER_ID;
                var children = Array.prototype.slice.call(document.body.childNodes);
                for (var i = 0; i < children.length; i++) {
                    wrapper.appendChild(children[i]);
                }
                document.body.appendChild(wrapper);
            }
            this.wrapper = wrapper;

            // Wrapper must be positioned for descendant offsetLeft to be relative to it.
            this.wrapper.style.position = 'relative';

            // Snapshot the hash and strip it from the URL — otherwise the browser tries
            // to scroll the anchored element into view, which shifts body.scrollLeft and
            // throws our transform-based pagination out of alignment (cover/page margin loss).
            var initialHash = window.location.hash;
            if (initialHash) {
                try { history.replaceState(null, '', window.location.pathname); } catch (e) {}
            }

            // Suppress transition for the initial render so we don't see a slide-from-zero animation.
            this.wrapper.style.transition = 'none';
            this.recompute();
            this.goToPage(this.pageForInitialHash(initialHash));
            // Make absolutely sure the browser hasn't shifted the document.
            window.scrollTo(0, 0);
            // Restore transition after one frame (subsequent page-turns animate normally).
            var self = this;
            requestAnimationFrame(function () { self.wrapper.style.transition = ''; });

            // Re-paginate after late-loading resources (images/fonts) settle.
            window.addEventListener('load', function () { Reader.recomputeAndShow(); });

            var resizeTimer = null;
            window.addEventListener('resize', function () {
                if (resizeTimer) clearTimeout(resizeTimer);
                resizeTimer = setTimeout(function () { Reader.recomputeAndShow(); }, 100);
            });

            // Right-click on a paragraph (or touch long-press, which Windows
            // synthesises into a contextmenu event for free) → ask the host
            // to show neighbors. Walk up to a paragraph-like block element
            // and use its text as the embedding probe. Skip images / tiny
            // snippets so page numbers and single-word labels don't trigger.
            // If the user has selected text first, let the default menu
            // through so Copy still works.
            document.addEventListener('contextmenu', function (e) {
                var sel = window.getSelection && window.getSelection();
                if (sel && sel.toString().length > 0) return;
                var target = findEnclosingProseBlock(e.target);
                if (!target) return;
                var text = (target.textContent || '').trim();
                if (text.length < 30) return;
                e.preventDefault();
                post({ type: 'showNeighbors', text: text.length > 800 ? text.substring(0, 800) : text });
            });

            // Same-document fragment navigation: the host calls Navigate with the
            // same path and a new hash (TOC link clicked from within the same
            // chapter, search result that happens to land in the open chapter).
            // Chromium does not reload, so init won't re-run; we have to handle
            // the hash transition explicitly.
            window.addEventListener('hashchange', function () {
                var newHash = window.location.hash;
                if (!newHash || !Reader.wrapper) return;
                try { history.replaceState(null, '', window.location.pathname); } catch (e) {}
                Reader.lastFindHit = null;
                Reader.recompute();
                Reader.goToPage(Reader.pageForInitialHash(newHash));
            });

            // WebView2 captures keys when focused, so handle nav keys here.
            document.addEventListener('keydown', function (e) {
                var t = e.target;
                if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.isContentEditable)) return;

                if (e.key === 'ArrowRight' || e.key === 'PageDown' || e.key === ' ') {
                    Reader.nextPage();
                    e.preventDefault();
                } else if (e.key === 'ArrowLeft' || e.key === 'PageUp') {
                    Reader.prevPage();
                    e.preventDefault();
                }
            });

            // Tap-to-turn-page (touch + mouse). Left 40% = prev, right 40% = next, middle 20% inert.
            // Uses pointerdown for touch (more reliable than click in WebView2) and click as a
            // fallback for mouse. Suppress double-fire by checking event type — pointerdown handles
            // touch/pen, click handles mouse.
            function tryNavOnTap(e) {
                // Don't hijack link clicks (footnotes, cross-refs).
                var t = e.target;
                while (t && t !== document.body) {
                    if (t.tagName === 'A' && t.getAttribute('href')) return;
                    t = t.parentElement;
                }
                // Don't hijack when the user is selecting text.
                var sel = window.getSelection && window.getSelection();
                if (sel && sel.toString().length > 0) return;

                var x = e.clientX;
                if (x === undefined && e.changedTouches && e.changedTouches[0]) x = e.changedTouches[0].clientX;
                if (x === undefined) return;

                var w = window.innerWidth;
                if (x < w * 0.4) Reader.prevPage();
                else if (x > w * 0.6) Reader.nextPage();
            }
            // pointerdown handles touch/pen (click is unreliable in WebView2 for finger taps);
            // click handles mouse. A touch tap also synthesizes a click event, so guard the click
            // handler with a timestamp window to avoid double-firing (which advanced 2 pages per tap).
            var lastTouchNavTime = 0;
            document.addEventListener('pointerdown', function (e) {
                if (e.pointerType === 'touch' || e.pointerType === 'pen') {
                    lastTouchNavTime = Date.now();
                    tryNavOnTap(e);
                }
            }, true);
            document.addEventListener('click', function (e) {
                if (Date.now() - lastTouchNavTime < 500) return;
                tryNavOnTap(e);
            }, true);
        },

        recomputeAndShow: function () {
            this.recompute();
            var targetPage = this.currentPage;
            if (this.lastFindHit) {
                var resolved = this._pageForCachedHit();
                if (resolved !== null) targetPage = resolved;
            }
            this.goToPage(targetPage);
        },

        _pageForCachedHit: function () {
            if (!this.lastFindHit || !this.wrapper) return null;
            try {
                var hit = this.lastFindHit;
                var rect;
                if (hit.localOffset !== undefined) {
                    // Text-node hit (search-result path) — use a Range for pixel-precise
                    // x even when the matched text is inside an inline element wrapping
                    // across a column boundary.
                    var len = (hit.node.nodeValue || '').length;
                    if (len === 0) return null;
                    var off = Math.max(0, Math.min(hit.localOffset, len - 1));
                    var range = document.createRange();
                    range.setStart(hit.node, off);
                    range.setEnd(hit.node, Math.min(off + 1, len));
                    rect = range.getBoundingClientRect();
                } else {
                    // Element hit (TOC #element-id path) — direct bounding rect.
                    rect = hit.node.getBoundingClientRect();
                }
                if (rect.width === 0 && rect.height === 0) return null;
                var wrect = this.wrapper.getBoundingClientRect();
                var x = rect.left - wrect.left;
                var stride = this.wrapper.clientWidth + GAP;
                return Math.max(0, Math.min(Math.floor((x + 4) / stride), this.pageCount - 1));
            } catch (e) { return null; }
        },

        pageForInitialHash: function (hash) {
            if (!hash || hash.length <= 1) return 0;
            if (hash === '#__last_page') return this.pageCount - 1;
            // Restored reading position — land on a specific page index within this chapter.
            // Page count depends on viewport, so clamp to whatever the current layout produces.
            var pageMatch = hash.match(/^#__page_(\d+)$/);
            if (pageMatch) {
                var p = parseInt(pageMatch[1], 10);
                return Math.max(0, Math.min(p, this.pageCount - 1));
            }
            // Search-hit text probe — walk text nodes in the rendered DOM and
            // locate the first one containing the probe text, cache the hit so
            // recomputeAndShow can re-resolve after image-loading reflows, then
            // compute the column-page from a Range built at the matched position
            // (precise even when matched text is inside an inline element that
            // wraps across a column boundary).
            var findMatch = hash.match(/^#__find_(.+)$/);
            if (findMatch) {
                var probe;
                try { probe = decodeURIComponent(findMatch[1]); }
                catch (e) { probe = ''; }
                var hit = this.findTextNodeContaining(probe);
                if (!hit || !this.wrapper) return 0;
                this.lastFindHit = hit;
                var p = this._pageForCachedHit();
                return p !== null ? p : 0;
            }
            // Element-id anchor — find element, cache it for re-resolution after
            // image-loading reflows (same trick as the search-hit path), and compute
            // the initial page from its bounding rect.
            try {
                var id = decodeURIComponent(hash.substring(1));
                var el = document.getElementById(id);
                if (!el || !this.wrapper) return 0;
                this.lastFindHit = { node: el };
                var p = this._pageForCachedHit();
                return p !== null ? p : 0;
            } catch (e) { /* malformed hash */ }
            return 0;
        },

        // Locate the first VISIBLE position in the rendered DOM that contains the
        // probe text. Returns { node, localOffset } so the caller can build a Range
        // for a pixel-precise rect. Tries three matching strategies in order:
        //   1. literal substring match
        //   2. whitespace-collapsed match (one space for any whitespace run)
        //   3. whitespace-stripped match (drop all whitespace on both sides)
        // and within each, walks every match position and returns the first whose
        // Range has a visible rect (skips display:none / hidden text). Posts a
        // diagnostic message either way so failures are debuggable.
        findTextNodeContaining: function (probe) {
            if (!this.wrapper || !probe) {
                post({ type: 'searchHitDebug', stage: 'no-input', probeLen: (probe || '').length });
                return null;
            }

            var walker = document.createTreeWalker(
                this.wrapper,
                NodeFilter.SHOW_TEXT,
                {
                    acceptNode: function (n) {
                        var p = n.parentNode;
                        while (p && p !== Reader.wrapper) {
                            var t = (p.localName || '').toLowerCase();
                            if (t === 'script' || t === 'style') return NodeFilter.FILTER_REJECT;
                            p = p.parentNode;
                        }
                        return NodeFilter.FILTER_ACCEPT;
                    }
                }
            );

            var nodes = [];
            var concat = '';
            var node;
            while ((node = walker.nextNode())) {
                nodes.push({ node: node, start: concat.length });
                concat += node.nodeValue || '';
            }

            var self = this;
            var mapIdxToHit = function (idx) {
                for (var i = 0; i < nodes.length; i++) {
                    var s = nodes[i].start;
                    var e = s + (nodes[i].node.nodeValue || '').length;
                    if (s <= idx && idx < e) return { node: nodes[i].node, localOffset: idx - s };
                }
                return null;
            };
            var firstVisibleMatch = function (text, search) {
                var start = 0;
                while (start <= text.length - search.length) {
                    var i = text.indexOf(search, start);
                    if (i < 0) return null;
                    var hit = mapIdxToHit(i);
                    if (hit && self._isHitVisible(hit)) return { hit: hit, idx: i };
                    start = i + 1;
                }
                return null;
            };

            // Stage 1: literal
            var match = firstVisibleMatch(concat, probe);
            if (match) {
                post({ type: 'searchHitDebug', stage: 'literal', idx: match.idx, probeLen: probe.length });
                return match.hit;
            }

            // Stage 2: collapse whitespace (insert single space for any run)
            var collapseMap = function (s) {
                var out = '', map = [], lastSpace = false;
                for (var k = 0; k < s.length; k++) {
                    var c = s.charCodeAt(k);
                    var isSp = c === 32 || c === 9 || c === 10 || c === 13;
                    if (isSp) {
                        if (!lastSpace) { out += ' '; map.push(k); }
                        lastSpace = true;
                    } else {
                        out += s.charAt(k);
                        map.push(k);
                        lastSpace = false;
                    }
                }
                return { out: out, map: map };
            };
            var nProbeC = collapseMap(probe).out.replace(/^ | $/g, '');
            var nConcat = collapseMap(concat);
            if (nProbeC.length > 0) {
                var nMatch = firstVisibleMatchMapped(nConcat, nProbeC);
                if (nMatch) {
                    post({ type: 'searchHitDebug', stage: 'collapsed', idx: nMatch.idx, probeLen: probe.length });
                    return nMatch.hit;
                }
            }

            // Stage 3: strip ALL whitespace (most aggressive)
            var stripMap = function (s) {
                var out = '', map = [];
                for (var k = 0; k < s.length; k++) {
                    var c = s.charCodeAt(k);
                    if (c !== 32 && c !== 9 && c !== 10 && c !== 13) {
                        out += s.charAt(k);
                        map.push(k);
                    }
                }
                return { out: out, map: map };
            };
            var sProbe = stripMap(probe).out;
            var sConcat = stripMap(concat);
            if (sProbe.length > 0) {
                var sMatch = firstVisibleMatchMapped(sConcat, sProbe);
                if (sMatch) {
                    post({ type: 'searchHitDebug', stage: 'stripped', idx: sMatch.idx, probeLen: probe.length });
                    return sMatch.hit;
                }
            }

            post({
                type: 'searchHitDebug',
                stage: 'not-found',
                probeLen: probe.length,
                concatLen: concat.length,
                probePrefix: probe.substring(0, 60),
                concatPrefix: concat.substring(0, 120).replace(/\s+/g, ' ')
            });
            return null;

            // Inner helper: same as firstVisibleMatch but operates on a mapped
            // (transformed) string, mapping the hit-index back to a real concat index.
            function firstVisibleMatchMapped(transformed, search) {
                var start = 0;
                while (start <= transformed.out.length - search.length) {
                    var i = transformed.out.indexOf(search, start);
                    if (i < 0) return null;
                    var realIdx = transformed.map[i];
                    var hit = mapIdxToHit(realIdx);
                    if (hit && self._isHitVisible(hit)) return { hit: hit, idx: realIdx };
                    start = i + 1;
                }
                return null;
            }
        },

        _isHitVisible: function (hit) {
            try {
                var len = (hit.node.nodeValue || '').length;
                if (len === 0) return false;
                var range = document.createRange();
                var startOff = Math.max(0, Math.min(hit.localOffset, len - 1));
                range.setStart(hit.node, startOff);
                range.setEnd(hit.node, Math.min(startOff + 1, len));
                var rect = range.getBoundingClientRect();
                return rect.width > 0 || rect.height > 0;
            } catch (e) { return false; }
        },

        recompute: function () {
            if (!this.wrapper) return;
            var w = this.wrapper.clientWidth;
            if (w <= 0) { this.pageCount = 1; return; }
            this.wrapper.style.columnWidth = w + 'px';
            // Total content width = pageCount*w + (pageCount-1)*GAP, so:
            this.pageCount = Math.max(1, Math.round((this.wrapper.scrollWidth + GAP) / (w + GAP)));
        },

        goToPage: function (n) {
            n = Math.max(0, Math.min(n, this.pageCount - 1));
            this.currentPage = n;
            if (this.wrapper) {
                var w = this.wrapper.clientWidth;
                this.wrapper.style.transform = 'translateX(-' + (n * (w + GAP)) + 'px)';
            }
            post({ type: 'pageChanged', page: n, total: this.pageCount });
        },

        nextPage: function () {
            this.lastFindHit = null;
            if (this.currentPage < this.pageCount - 1) {
                this.goToPage(this.currentPage + 1);
                return true;
            }
            post({ type: 'endOfChapter' });
            return false;
        },

        prevPage: function () {
            this.lastFindHit = null;
            if (this.currentPage > 0) {
                this.goToPage(this.currentPage - 1);
                return true;
            }
            post({ type: 'startOfChapter' });
            return false;
        }
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', function () { Reader.init(); });
    } else {
        Reader.init();
    }

    window.Reader = Reader;
})();
