// Chart hover tooltip in the app's window style. The chart's invisible hit areas carry
// data-tip-title / data-tip-rx / data-tip-tx; one delegated listener shows a single tooltip element.
// No Blazor round trip, so it follows the mouse instantly (and keeps working while the live page re-renders).
(function () {
    var tip = null;

    function ensure() {
        if (tip) return tip;
        tip = document.createElement('div');
        tip.className = 'chart-tip';
        tip.setAttribute('role', 'tooltip');
        tip.hidden = true;
        document.body.appendChild(tip);
        return tip;
    }

    function hide() {
        if (tip) tip.hidden = true;
    }

    function row(label, value, swatch) {
        var r = document.createElement('div');
        r.className = 'chart-tip-row';
        var l = document.createElement('span');
        var s = document.createElement('i');
        s.className = swatch;
        l.appendChild(s);
        l.appendChild(document.createTextNode(label));
        var v = document.createElement('b');
        v.textContent = value;
        r.appendChild(l);
        r.appendChild(v);
        return r;
    }

    document.addEventListener('mousemove', function (e) {
        var target = e.target;
        if (!(target instanceof Element) || !target.classList.contains('hit')) { hide(); return; }

        var el = ensure();
        var bar = document.createElement('div');
        bar.className = 'chart-tip-bar';
        bar.textContent = target.getAttribute('data-tip-title') || '';

        el.replaceChildren(
            bar,
            row('download', target.getAttribute('data-tip-rx') || '', 'rx'),
            row('upload', target.getAttribute('data-tip-tx') || '', 'tx'));
        el.hidden = false;

        // Beside the pointer, flipped to the other side near the screen edges.
        var gap = 14, w = el.offsetWidth, h = el.offsetHeight;
        var x = e.clientX + gap, y = e.clientY + gap;
        if (x + w > window.innerWidth - 8) x = e.clientX - w - gap;
        if (y + h > window.innerHeight - 8) y = e.clientY - h - gap;
        el.style.left = Math.max(4, x) + 'px';
        el.style.top = Math.max(4, y) + 'px';
    }, { passive: true });

    document.addEventListener('mouseleave', hide);
    window.addEventListener('scroll', hide, true);
    window.addEventListener('blur', hide);
})();
