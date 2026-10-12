// Spreadsheet uploads from phones. Android hands files from Google Drive (and other cloud apps) to
// the browser as links that are only read when the form is sent; by then Drive often serves them
// slightly differently and Chrome refuses with ERR_UPLOAD_FILE_CHANGED. So the chosen file is read
// into memory right away, and that copy is what the form sends. Without DataTransfer support the
// form just sends the file as picked.
(() => {
    if (window.sheetUploadReady) return;  // the card can appear more than once on a page
    window.sheetUploadReady = true;

    document.addEventListener('change', async e => {
        const input = e.target;
        if (!(input instanceof HTMLInputElement) || input.type !== 'file' || !input.closest('form.upload')) return;
        const file = input.files?.[0];
        if (!file || typeof DataTransfer === 'undefined') return;

        const button = input.form.querySelector('button');
        if (button) button.disabled = true;
        try {
            const copy = new File([await file.arrayBuffer()], file.name, { type: file.type, lastModified: Date.now() });
            const files = new DataTransfer();
            files.items.add(copy);
            input.files = files.files;  // doesn't fire "change" again
            input.setCustomValidity('');
        } catch {
            input.setCustomValidity('Não foi possível ler este arquivo. Baixe a planilha para o celular e escolha de novo.');
            input.reportValidity();
        } finally {
            if (button) button.disabled = false;
        }
    });
})();
