// Runs inside the CA Service Desk page. Only finds, fills and reads fields.
// It never submits a form, never clicks a save/submit button and never sends keyboard events.
(function () {
  const clean = s => (s || '').replace(/[‎‏‪-‮⁦-⁩]/g, '').replace(/\s+/g, ' ').trim();
  const label = s => clean(s).replace(/[*:]/g, '').trim();

  // All same-origin frames, outermost first (CA is built from nested framesets).
  function frames() {
    const found = [];
    (function walk(w) {
      try { if (w.document) found.push(w); } catch (e) { return; }
      for (let i = 0; i < w.frames.length; i++) { try { walk(w.frames[i]); } catch (e) { } }
    })(window.top);
    return found;
  }

  function visible(el) {
    if (!el || !el.getClientRects().length) return false;
    return el.ownerDocument.defaultView.getComputedStyle(el).visibility !== 'hidden';
  }

  const isField = el => el && /^(INPUT|SELECT|TEXTAREA)$/.test(el.tagName) &&
    !/^(hidden|button|submit|image|reset|checkbox|radio|file|password)$/i.test(el.type || '') &&
    !el.disabled && visible(el);

  const firstField = root => root ? Array.from(root.querySelectorAll('input,select,textarea')).find(isField) : undefined;

  function byLabel(doc, wanted) {
    for (const el of doc.querySelectorAll('label,th,td,span,a')) {
      if (el.querySelector('input,select,textarea,table') || !wanted.includes(label(el.textContent))) continue;
      if (el.htmlFor && isField(doc.getElementById(el.htmlFor))) return doc.getElementById(el.htmlFor);
      const cell = el.closest('td,th');
      if (!cell) continue;
      const own = firstField(cell);
      if (own) return own;
      // CA detail forms put the captions in one row and the fields in the next row, in the same column.
      const row = cell.parentElement;
      const index = Array.from(row.cells).indexOf(cell);
      const below = row.nextElementSibling;
      const under = below && below.cells && below.cells.length === row.cells.length ? firstField(below.cells[index]) : undefined;
      if (under && cell.tagName === 'TH') return under;
      const beside = firstField(cell.nextElementSibling);
      if (beside) return beside;
      if (under) return under;
    }
    return null;
  }

  function find(locator) {
    const list = frames();
    for (const selector of locator.selectors) {
      for (const w of list) {
        let hits;
        try { hits = Array.from(w.document.querySelectorAll(selector)); } catch (e) { continue; }
        const hit = hits.find(isField);
        if (hit) return hit;
      }
    }
    const wanted = locator.labels.map(label);
    for (const w of list) {
      const hit = wanted.length ? byLabel(w.document, wanted) : null;
      if (hit) return hit;
    }
    return null;
  }

  const current = el => el.tagName === 'SELECT'
    ? (el.selectedIndex >= 0 ? el.options[el.selectedIndex].text : '') : el.value;

  function expected(el, value, separator) {
    return el.tagName === 'TEXTAREA' ? value : value.split(/\r?\n/).filter(x => x.trim()).join(separator);
  }

  function fire(el, name) {
    el.dispatchEvent(new el.ownerDocument.defaultView.Event(name, { bubbles: true }));
  }

  return {
    state() {
      const list = frames();
      let sid = '';
      for (const w of list) {
        try { if (w.cfgSID) { sid = String(w.cfgSID); break; } } catch (e) { }
        const m = /[?+&]SID=(\d+)/.exec(w.location.href);
        if (m) { sid = m[1]; break; }
      }
      return {
        login: list.some(w => Array.from(w.document.querySelectorAll('input[type=password]')).some(visible)),
        sid,
        ready: list.every(w => w.document.readyState === 'complete') && /^https?:/.test(location.href),
        origin: location.origin
      };
    },

    // A new ticket form: summary + customer + category are editable and the summary is still empty.
    isNewForm(a) {
      if (!frames().every(w => w.document.readyState === 'complete')) return false;
      const summary = find(a.summary);
      return !!summary && !clean(summary.value) && !!find(a.customer) && !!find(a.category);
    },

    clickMenu(a) {
      const wanted = a.texts.map(label);
      for (const w of frames()) {
        const hits = Array.from(w.document.querySelectorAll('a,td,li,span,div,button'))
          .filter(el => wanted.includes(label(el.textContent)));
        // The innermost match; menus that are still collapsed may hold hidden items with a click handler.
        const hit = hits.reverse().find(visible) || hits[0];
        if (hit && !hit.closest('form button, form [type=submit]')) { hit.click(); return true; }
      }
      return false;
    },

    open(a) {
      return !!window.open(a.url, '_blank', 'width=1100,height=800,resizable=yes,scrollbars=yes');
    },

    set(a) {
      const el = find(a.locator);
      if (!el) return { status: 'missing' };
      if (el.tagName === 'SELECT') {
        const option = Array.from(el.options).find(o => clean(o.text) === clean(a.value));
        if (!option) return { status: 'nooption' };
        el.focus();
        el.selectedIndex = option.index;
        fire(el, 'change');
      } else {
        if (el.readOnly) return { status: 'readonly' };
        el.focus();
        el.value = expected(el, a.value, a.separator);
        fire(el, 'input');
        fire(el, 'change');
      }
      el.blur();
      return { status: 'set' };
    },

    read(a) {
      const el = find(a.locator);
      if (!el) return { status: 'missing' };
      const want = expected(el, a.value, a.separator);
      const exact = s => el.tagName === 'TEXTAREA' ? s.replace(/\r\n/g, '\n').trim() : clean(s);
      return {
        status: exact(current(el)) === exact(want) ? 'ok' : 'mismatch',
        actual: current(el),
        flattened: el.tagName !== 'TEXTAREA' && /\n/.test(a.value),
        tooLong: el.maxLength > 0 && want.length > el.maxLength
      };
    },

    settled() {
      return frames().every(w => w.document.readyState === 'complete');
    }
  };
})()
