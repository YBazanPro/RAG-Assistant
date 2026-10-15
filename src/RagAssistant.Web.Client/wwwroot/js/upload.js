// Uploads the file of an <input type="file"> straight from the browser (spec 010): the bytes never pass through the .NET runtime,
// so a 100 MiB PDF does not occupy the WebAssembly heap. XMLHttpRequest is used because fetch cannot report upload progress.

const requests = new Map();

// Status codes below zero are this module's own: -1 no file was found in the input, -2 the upload was aborted.
// 0 is the usual XMLHttpRequest value for a network failure.
export function upload(id, inputSelector, url, fields, headers, reporter) {
    return new Promise((resolve) => {
        const input = document.querySelector(inputSelector);
        const file = input && input.files && input.files[0];
        if (!file) {
            resolve({ status: -1, body: null });
            return;
        }

        const form = new FormData();
        for (const field of fields) {
            form.append(field.name, field.value);
        }

        form.append('file', file, file.name);

        const xhr = new XMLHttpRequest();
        requests.set(id, xhr);
        xhr.open('POST', url);
        for (const [name, value] of Object.entries(headers)) {
            xhr.setRequestHeader(name, value);
        }

        const finish = (status, body) => {
            requests.delete(id);
            resolve({ status, body });
        };

        xhr.upload.onprogress = (event) => {
            if (event.lengthComputable) {
                reporter.invokeMethodAsync('Report', event.loaded, event.total);
            }
        };
        xhr.onload = () => finish(xhr.status, xhr.responseText);
        xhr.onerror = () => finish(0, null);
        xhr.ontimeout = () => finish(0, null);
        xhr.onabort = () => finish(-2, null);
        xhr.send(form);
    });
}

export function abort(id) {
    const xhr = requests.get(id);
    if (xhr) {
        xhr.abort();
    }
}
