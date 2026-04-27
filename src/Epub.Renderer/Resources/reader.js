(function () {
    'use strict';

    var WRAPPER_ID = '__epub_reader_content';
    var SIDE_MARGIN = 48;        // px reading margin on each side
    var TOP_MARGIN = 24;         // px reading margin top and bottom
    var IMG_VERTICAL_SLACK = 60; // extra px to cap image height by, so ancestor wrapper margins don't push the image past column height
    var GAP = SIDE_MARGIN * 2;   // column-gap absorbs the would-be padding without leaking

    // Block-tag list MUST match Epub.Search.SpineTextExtractor.BlockTags exactly.
    // Both walkers (this one and the C# extractor) emit a single '\n' on the close
    // of these elements; any divergence breaks search-result navigation alignment.
    var BLOCK_TAGS = {
        'p': 1, 'div': 1, 'h1': 1, 'h2': 1, 'h3': 1, 'h4': 1, 'h5': 1, 'h6': 1,
        'li': 1, 'blockquote': 1, 'pre': 1, 'td': 1, 'th': 1, 'tr': 1, 'br': 1,
        'section': 1, 'article': 1, 'aside': 1, 'figure': 1, 'figcaption': 1, 'hr': 1
    };

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

    function post(msg) {
        if (window.chrome && window.chrome.webview && window.chrome.webview.postMessage) {
            window.chrome.webview.postMessage(msg);
        }
    }

    var Reader = {
        currentPage: 0,
        pageCount: 1,
        wrapper: null,

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
            this.goToPage(this.currentPage);
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
            // Search-hit char-offset anchor — walk text nodes accumulating lengths
            // (with '\n' for block closes, mirroring SpineTextExtractor) until we
            // pass the target, then compute the column-page from the parent
            // element's bounding rect.
            var offsetMatch = hash.match(/^#__offset_(\d+)$/);
            if (offsetMatch) {
                var target = parseInt(offsetMatch[1], 10);
                var found = this.findNodeAtOffset(target);
                if (!found || !this.wrapper) return 0;
                var orect = found.element.getBoundingClientRect();
                var owrect = this.wrapper.getBoundingClientRect();
                var ox = orect.left - owrect.left;
                var ostride = this.wrapper.clientWidth + GAP;
                return Math.max(0, Math.min(Math.floor((ox + 4) / ostride), this.pageCount - 1));
            }
            // Element-id anchor — find element, compute which page contains it.
            try {
                var id = decodeURIComponent(hash.substring(1));
                var el = document.getElementById(id);
                if (!el || !this.wrapper) return 0;

                // getBoundingClientRect forces a layout flush so positions are fresh, then
                // gives the element's viewport-x relative to wrapper's current viewport-x.
                // (Wrapper has no transform applied yet — this runs in init before goToPage.)
                var rect = el.getBoundingClientRect();
                var wrect = this.wrapper.getBoundingClientRect();
                var x = rect.left - wrect.left;
                var stride = this.wrapper.clientWidth + GAP;
                // Small tolerance for subpixel column-boundary rounding.
                return Math.max(0, Math.min(Math.floor((x + 4) / stride), this.pageCount - 1));
            } catch (e) { /* malformed hash */ }
            return 0;
        },

        // Walks the wrapper subtree accumulating text-node lengths plus a '\n' for
        // each closing block-level element. Skips script/style/comment subtrees
        // entirely. Stops when the running total crosses `target`, returning the
        // text node's parent element and the offset within the matched node.
        // Contract MUST match Epub.Search.SpineTextExtractor.
        findNodeAtOffset: function (target) {
            if (!this.wrapper) return null;
            var state = { offset: 0, found: null };
            this._walkForOffset(this.wrapper, target, state);
            return state.found;
        },

        _walkForOffset: function (node, target, state) {
            if (state.found) return;

            var nt = node.nodeType;
            if (nt === 3 /* TEXT_NODE */) {
                var len = (node.nodeValue || '').length;
                if (state.offset + len > target) {
                    state.found = {
                        element: node.parentElement || node.parentNode,
                        localOffset: target - state.offset
                    };
                }
                state.offset += len;
                return;
            }
            if (nt === 8 /* COMMENT_NODE */) return;

            if (nt !== 1 /* ELEMENT_NODE */) {
                var kids = node.childNodes;
                for (var i = 0; i < kids.length; i++) {
                    this._walkForOffset(kids[i], target, state);
                    if (state.found) return;
                }
                return;
            }

            var tag = (node.localName || '').toLowerCase();
            if (tag === 'script' || tag === 'style') return;

            var children = node.childNodes;
            for (var j = 0; j < children.length; j++) {
                this._walkForOffset(children[j], target, state);
                if (state.found) return;
            }

            if (BLOCK_TAGS[tag]) {
                // Block close emits '\n'. If target lands exactly on this '\n',
                // resolve to the block element itself (rare; only happens for
                // empty-paragraph offsets that the chunker shouldn't emit anyway).
                if (state.offset === target) {
                    state.found = { element: node, localOffset: 0 };
                }
                state.offset += 1;
            }
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
            if (this.currentPage < this.pageCount - 1) {
                this.goToPage(this.currentPage + 1);
                return true;
            }
            post({ type: 'endOfChapter' });
            return false;
        },

        prevPage: function () {
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
