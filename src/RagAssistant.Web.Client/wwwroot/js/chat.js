// Browser helpers for the chat page (spec 013): the clipboard, focus, scroll following, Enter handling and the draft.

// Rejects when the browser blocks the write or has no clipboard API; the caller then shows the text in a field instead.
export async function copyText(text) {
    await navigator.clipboard.writeText(text);
}

export function focusById(id) {
    document.getElementById(id)?.focus();
}

// Scroll following: the thread sticks to the bottom while the reader is within this many pixels of it.
const FOLLOW_THRESHOLD = 64;
const watchers = new WeakMap();

export function watchThread(element, callback) {
    unwatchThread(element);
    const watcher = { following: true, callback };
    watcher.onScroll = () => {
        const following = element.scrollHeight - element.scrollTop - element.clientHeight <= FOLLOW_THRESHOLD;
        if (following !== watcher.following) {
            watcher.following = following;
            callback.invokeMethodAsync('OnFollowingChanged', following);
        }
    };
    element.addEventListener('scroll', watcher.onScroll, { passive: true });
    watchers.set(element, watcher);
}

export function unwatchThread(element) {
    const watcher = watchers.get(element);
    if (watcher) {
        element.removeEventListener('scroll', watcher.onScroll);
        watchers.delete(element);
    }
}

export function scrollToBottom(element, force) {
    const watcher = watchers.get(element);
    if (force || !watcher || watcher.following) {
        element.scrollTop = element.scrollHeight;
        watcher?.onScroll();
    }
}

// Enter sends, Shift+Enter adds a line, and Enter that confirms an IME candidate (isComposing, or keyCode 229 in some
// browsers) is left alone. The listener sits on a wrapper: key events from the Fluent text area's shadow DOM are composed.
const enterHandlers = new WeakMap();

export function attachEnter(container, callback) {
    detachEnter(container);
    const handler = event => {
        if (event.key === 'Enter' && !event.shiftKey && !event.isComposing && event.keyCode !== 229) {
            event.preventDefault();
            callback.invokeMethodAsync('OnEnter');
        }
    };
    container.addEventListener('keydown', handler);
    enterHandlers.set(container, handler);
}

export function detachEnter(container) {
    const handler = enterHandlers.get(container);
    if (handler) {
        container.removeEventListener('keydown', handler);
        enterHandlers.delete(container);
    }
}

// sessionStorage can throw (blocked site data); the C# side treats any failure as "no draft".
export function readDraft(key) {
    return sessionStorage.getItem(key);
}

export function writeDraft(key, text) {
    if (text) {
        sessionStorage.setItem(key, text);
    } else {
        sessionStorage.removeItem(key);
    }
}

export function clearDraft(key) {
    sessionStorage.removeItem(key);
}
