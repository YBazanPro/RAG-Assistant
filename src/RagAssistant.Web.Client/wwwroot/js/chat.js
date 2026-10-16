// Browser helpers for the chat page (spec 013): the clipboard and focus, which Blazor cannot reach itself.

// Rejects when the browser blocks the write or has no clipboard API; the caller then shows the text in a field instead.
export async function copyText(text) {
    await navigator.clipboard.writeText(text);
}

export function focusById(id) {
    document.getElementById(id)?.focus();
}
