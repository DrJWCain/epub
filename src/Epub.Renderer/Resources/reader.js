(function () {
    'use strict';

    var WRAPPER_ID = '__epub_reader_content';
    var SIDE_MARGIN = 48;        // px reading margin on each side
    var TOP_MARGIN = 24;         // px reading margin top and bottom
    var IMG_VERTICAL_SLACK = 60; // extra px to cap image height by, so ancestor wrapper margins don't push the image past column height
    var GAP = SIDE_MARGIN * 2;   // column-gap absorbs the would-be padding without leaking

    var css = [
        'html, body { width: 100%; height: 100%; margin: 0 !important; padding: 0 !important; overflow: hidden !important; }',
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

            // Suppress transition for the initial render so we don't see a slide-from-zero animation.
            this.wrapper.style.transition = 'none';
            this.recompute();
            var initialPage = (window.location.hash === '#__last_page') ? this.pageCount - 1 : 0;
            this.goToPage(initialPage);
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
        },

        recomputeAndShow: function () {
            this.recompute();
            this.goToPage(this.currentPage);
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
