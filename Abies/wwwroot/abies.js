// =============================================================================
// Abies Browser Runtime — JavaScript Module
// =============================================================================
// This module is loaded by .NET WASM via JSHost.ImportAsync("Abies", "/abies.js").
//
// Responsibilities:
//   1. renderInitial(rootId, html)     — sets innerHTML for the first render
//   2. applyPatches(patchData)         — decodes binary patches, applies DOM mutations
//   3. setupEventDelegation(rootId)    — captures common events at the root
//   4. setTitle(title)                 — sets document.title
//   5. navigateTo(url)                 — history.pushState for client-side routing
//
// Binary Protocol:
//   Header: int32 patchCount
//   Per patch:
//     uint8  opCode
//     uint16 pathLength + uint16[] path
//     ...op-specific fields (strings = uint16 byteLength + UTF-8 bytes)
//
//   OpCodes:
//     0 = InsertChild(path, index, html)
//     1 = RemoveChild(path, index)
//     2 = ReplaceNode(path, html)
//     3 = SetAttribute(path, name, value)
//     4 = RemoveAttribute(path, name)
//     5 = SetText(path, value)
//
// Event Delegation:
//   A single listener per event type on the app root captures all events.
//   The handler computes the DOM path (child indices from root to target),
//   then calls the [JSExport] DispatchDomEvent(pathJson, eventName, eventData)
//   to feed the event back into the .NET automaton loop.
// =============================================================================

/** @type {HTMLElement | null} */
let appRoot = null;

/** @type {TextDecoder} */
const utf8Decoder = new TextDecoder("utf-8");

// ── Exported Functions (called from .NET via [JSImport]) ──

/**
 * Sets the innerHTML of the app root for the initial render.
 * @param {string} rootId - The DOM element ID.
 * @param {string} html - The full HTML string.
 */
export function renderInitial(rootId, html) {
    appRoot = document.getElementById(rootId);
    if (!appRoot) {
        throw new Error(`Abies: element #${rootId} not found`);
    }
    appRoot.innerHTML = html;
}

/**
 * Applies a binary-encoded batch of DOM patches.
 * The input is a MemoryView (Span<byte>) from .NET — we must .slice() it
 * to get a stable Uint8Array before the interop call returns.
 * @param {ArrayBufferView} patchData - Binary patch data from .NET.
 */
export function applyPatches(patchData) {
    // MemoryView from .NET — slice() to get a stable copy.
    const bytes = patchData.slice();
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    let offset = 0;

    const patchCount = view.getInt32(offset, true);
    offset += 4;

    for (let i = 0; i < patchCount; i++) {
        offset = applyPatch(view, bytes, offset);
    }
}

/**
 * Sets up event delegation on the app root element.
 * Captures common DOM events and routes them to .NET.
 * @param {string} rootId - The DOM element ID.
 */
export function setupEventDelegation(rootId) {
    appRoot = appRoot || document.getElementById(rootId);
    if (!appRoot) {
        throw new Error(`Abies: element #${rootId} not found`);
    }

    const eventTypes = [
        "click", "dblclick", "mousedown", "mouseup",
        "input", "change", "submit",
        "keydown", "keyup", "keypress",
        "focus", "blur",
        "touchstart", "touchend", "touchmove"
    ];

    for (const eventType of eventTypes) {
        appRoot.addEventListener(eventType, (e) => {
            handleDelegatedEvent(e, eventType);
        }, true); // Use capture phase to catch all events.
    }
}

/**
 * Sets the document title.
 * @param {string} title - The new title.
 */
export function setTitle(title) {
    document.title = title;
}

/**
 * Navigates to a URL via history.pushState.
 * @param {string} url - The target URL.
 */
export function navigateTo(url) {
    history.pushState(null, "", url);
    // Dispatch a popstate event so the app can react.
    window.dispatchEvent(new PopStateEvent("popstate"));
}

// ── Binary Patch Decoder ──

/**
 * Reads a uint16 (little-endian) from the DataView.
 * @param {DataView} view
 * @param {number} offset
 * @returns {{ value: number, offset: number }}
 */
function readUInt16(view, offset) {
    return { value: view.getUint16(offset, true), offset: offset + 2 };
}

/**
 * Reads a path (uint16 length + uint16[] indices).
 * @param {DataView} view
 * @param {number} offset
 * @returns {{ path: number[], offset: number }}
 */
function readPath(view, offset) {
    let r = readUInt16(view, offset);
    const length = r.value;
    offset = r.offset;

    const path = [];
    for (let i = 0; i < length; i++) {
        r = readUInt16(view, offset);
        path.push(r.value);
        offset = r.offset;
    }
    return { path, offset };
}

/**
 * Reads a length-prefixed UTF-8 string.
 * @param {DataView} view
 * @param {Uint8Array} bytes
 * @param {number} offset
 * @returns {{ value: string, offset: number }}
 */
function readString(view, bytes, offset) {
    const r = readUInt16(view, offset);
    const byteLength = r.value;
    offset = r.offset;
    const value = utf8Decoder.decode(bytes.subarray(offset, offset + byteLength));
    return { value, offset: offset + byteLength };
}

/**
 * Navigates the real DOM from the view root to the element at the given path.
 * The view root is appRoot's first child (the node produced by innerHTML).
 * An empty path returns the view root itself.
 * @param {number[]} path - Child indices from view root.
 * @returns {Node | null}
 */
function navigateToNode(path) {
    // Start at the view root — the first child of the app mount point.
    /** @type {Node} */
    let current = appRoot?.firstChild;
    if (!current) {
        console.warn("Abies: no view root found in appRoot");
        return null;
    }
    for (const index of path) {
        if (!current || !current.childNodes || index >= current.childNodes.length) {
            console.warn("Abies: path navigation failed", path);
            return null;
        }
        current = current.childNodes[index];
    }
    return current;
}

/**
 * Parses an HTML string into a single DOM element or text node.
 * @param {string} html
 * @returns {Node}
 */
function parseHtmlFragment(html) {
    const template = document.createElement("template");
    template.innerHTML = html;
    return template.content.firstChild;
}

/**
 * Applies a single patch from the binary stream.
 * @param {DataView} view
 * @param {Uint8Array} bytes
 * @param {number} offset
 * @returns {number} The new offset after reading this patch.
 */
function applyPatch(view, bytes, offset) {
    const opCode = view.getUint8(offset);
    offset += 1;

    switch (opCode) {
        case 0: // InsertChild
            return applyInsertChild(view, bytes, offset);
        case 1: // RemoveChild
            return applyRemoveChild(view, bytes, offset);
        case 2: // ReplaceNode
            return applyReplaceNode(view, bytes, offset);
        case 3: // SetAttribute
            return applySetAttribute(view, bytes, offset);
        case 4: // RemoveAttribute
            return applyRemoveAttribute(view, bytes, offset);
        case 5: // SetText
            return applySetText(view, bytes, offset);
        default:
            throw new Error(`Abies: unknown patch op code ${opCode}`);
    }
}

function applyInsertChild(view, bytes, offset) {
    const p = readPath(view, offset);
    offset = p.offset;

    const idx = readUInt16(view, offset);
    offset = idx.offset;

    const html = readString(view, bytes, offset);
    offset = html.offset;

    const parent = navigateToNode(p.path);
    if (parent) {
        const child = parseHtmlFragment(html.value);
        if (idx.value >= parent.childNodes.length) {
            parent.appendChild(child);
        } else {
            parent.insertBefore(child, parent.childNodes[idx.value]);
        }
    }
    return offset;
}

function applyRemoveChild(view, bytes, offset) {
    const p = readPath(view, offset);
    offset = p.offset;

    const idx = readUInt16(view, offset);
    offset = idx.offset;

    const parent = navigateToNode(p.path);
    if (parent && idx.value < parent.childNodes.length) {
        parent.removeChild(parent.childNodes[idx.value]);
    }
    return offset;
}

function applyReplaceNode(view, bytes, offset) {
    const p = readPath(view, offset);
    offset = p.offset;

    const html = readString(view, bytes, offset);
    offset = html.offset;

    const target = navigateToNode(p.path);
    if (target && target.parentNode) {
        const replacement = parseHtmlFragment(html.value);
        target.parentNode.replaceChild(replacement, target);
    }
    return offset;
}

function applySetAttribute(view, bytes, offset) {
    const p = readPath(view, offset);
    offset = p.offset;

    const name = readString(view, bytes, offset);
    offset = name.offset;

    const value = readString(view, bytes, offset);
    offset = value.offset;

    const target = navigateToNode(p.path);
    if (target && target instanceof Element) {
        target.setAttribute(name.value, value.value);
    }
    return offset;
}

function applyRemoveAttribute(view, bytes, offset) {
    const p = readPath(view, offset);
    offset = p.offset;

    const name = readString(view, bytes, offset);
    offset = name.offset;

    const target = navigateToNode(p.path);
    if (target && target instanceof Element) {
        target.removeAttribute(name.value);
    }
    return offset;
}

function applySetText(view, bytes, offset) {
    const p = readPath(view, offset);
    offset = p.offset;

    const value = readString(view, bytes, offset);
    offset = value.offset;

    const target = navigateToNode(p.path);
    if (target) {
        // If it's a text node, set textContent directly.
        // If it's an element, set its textContent (replacing all children).
        target.textContent = value.value;
    }
    return offset;
}

// ── Event Delegation ──

/**
 * Handles a delegated DOM event by computing the path and dispatching to .NET.
 * @param {Event} event
 * @param {string} eventType
 */
function handleDelegatedEvent(event, eventType) {
    if (!appRoot) return;

    let target = event.target;
    if (!target || !(target instanceof Node)) return;

    // Walk up from text nodes to the nearest Element — event handlers
    // in the virtual DOM are always on Element nodes, not text nodes.
    while (target && !(target instanceof Element) && target !== appRoot) {
        target = target.parentNode;
    }
    if (!target || target === appRoot) return;

    // Compute the path from the view root (appRoot's first child) to the
    // event target. The virtual DOM tree starts at the view root, which maps
    // to appRoot.firstChild in the real DOM. We skip appRoot itself so the
    // path indices align with the virtual tree's NavigateToNode.
    const viewRoot = appRoot.firstElementChild || appRoot.firstChild;
    if (!viewRoot) return;

    const path = computePathFrom(viewRoot, target);
    if (path === null) return;

    // Extract event data based on event type.
    const eventData = extractEventData(event, eventType);

    // Prevent default for submit events to avoid page reload.
    if (eventType === "submit") {
        event.preventDefault();
    }

    // Call the [JSExport] dispatch bridge in .NET.
    const pathJson = JSON.stringify(path);

    try {
        if (globalThis.__abies_dispatch) {
            globalThis.__abies_dispatch(pathJson, eventType, eventData);
        } else {
            console.warn("[Abies] __abies_dispatch not registered");
        }
    } catch (err) {
        console.error("Abies: dispatch error", err);
    }
}

/**
 * Computes the child-index path from the app root to the given node.
 * @param {Node} target
 * @returns {number[] | null}
 */
function computePath(target) {
    return computePathFrom(appRoot, target);
}

/**
 * Computes the child-index path from a given root to the given target node.
 * Walks up from target, counting sibling positions at each level.
 * @param {Node} root - The root node to compute the path from.
 * @param {Node} target - The target node.
 * @returns {number[] | null} - The path as child indices, or null if target is not within root.
 */
function computePathFrom(root, target) {
    if (target === root) return [];

    const indices = [];
    let current = target;

    while (current && current !== root) {
        const parent = current.parentNode;
        if (!parent) return null;

        // Count only Element siblings — text nodes in the DOM correspond to
        // virtual DOM children, but we navigate by Element child index to
        // match how NavigateToNode walks Node.Element.Children.
        let index = 0;
        let sibling = parent.firstChild;
        while (sibling && sibling !== current) {
            // Count all childNodes (including text nodes) since the virtual DOM
            // tree also includes Text nodes as children.
            index++;
            sibling = sibling.nextSibling;
        }
        indices.unshift(index);
        current = parent;
    }

    // If we didn't reach root, the target is outside our tree.
    if (current !== root) return null;

    return indices;
}

/**
 * Extracts relevant event data based on the event type.
 * @param {Event} event
 * @param {string} eventType
 * @returns {string}
 */
function extractEventData(event, eventType) {
    switch (eventType) {
        case "input":
        case "change":
            return event.target?.value ?? "";

        case "keydown":
        case "keyup":
        case "keypress":
            return event.key ?? "";

        case "submit":
            return "";

        default:
            // For click, mousedown, etc. — no specific data needed.
            return "";
    }
}
