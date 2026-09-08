// Category tree: the group checkbox is a pure UI control. Only the leaf boxes are
// named "Categories", so the server keeps receiving a simple flat list and doesn't
// need to know the tree exists.
document.querySelectorAll('.cat-group').forEach(group => {
    const parent = group.querySelector('.cat-parent');
    const children = [...group.querySelectorAll('.cat-child input[type="checkbox"]')];
    if (!parent || children.length === 0) return;

    const paint = () => {
        const checked = children.filter(c => c.checked).length;
        parent.checked = checked === children.length;
        parent.indeterminate = checked > 0 && checked < children.length;
        children.forEach(c => c.closest('.cat-row').classList.toggle('on', c.checked));
    };

    // Without this, clicking the box would also expand/collapse the <details>.
    parent.addEventListener('click', e => e.stopPropagation());

    parent.addEventListener('change', () => {
        children.forEach(c => { c.checked = parent.checked; });
        paint();
    });

    children.forEach(c => c.addEventListener('change', paint));
    paint();
});

document.querySelectorAll('.cat-single input[type="checkbox"]').forEach(box => {
    box.addEventListener('change', () =>
        box.closest('.cat-row').classList.toggle('on', box.checked));
});

// Price-per-kilo lookups hit the Products EAN API, which allows 100 calls a day —
// so each one is an explicit button press on a single row, never a page-wide sweep.
document.querySelectorAll('.kilo-btn').forEach(button => {
    button.addEventListener('click', async () => {
        const cell = button.parentElement;
        button.disabled = true;
        button.textContent = '…';

        try {
            const url = `?handler=UnitPrice&ean=${encodeURIComponent(button.dataset.ean)}`
                      + `&storeId=${encodeURIComponent(button.dataset.store)}`
                      + `&price=${encodeURIComponent(button.dataset.price)}`;
            const response = await fetch(url, { headers: { 'Accept': 'application/json' } });
            const data = await response.json();

            const result = document.createElement('div');
            if (data.ok) {
                result.className = 'unit-price exact';
                result.textContent = data.unitPrice;
                result.title = data.detail || 'From the Products EAN API';
            } else {
                result.className = 'unit-price failed';
                result.textContent = 'no weight';
                result.title = data.error || 'Lookup failed';
            }
            button.replaceWith(result);

            if (typeof data.used === 'number') {
                const counter = document.querySelector('[data-quota]');
                if (counter) counter.textContent = `${data.used} / ${data.limit} lookups used today`;
            }
        } catch {
            button.disabled = false;
            button.textContent = 'retry';
        }
    });
});
