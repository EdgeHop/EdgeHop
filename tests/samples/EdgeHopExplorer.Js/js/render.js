// render.js — DOM rendering helpers for the pure JS/HTML EdgeHop Explorer.
//
// Demonstrates a resolved same-module CALLS chain: renderAll() calls renderItem().

export function renderAll(names) {
    let html = "";
    for (const name of names) {
        html += renderItem(name);
    }
    return html;
}

// Module-private helper reached by a bare-identifier call -> a CALLS edge from renderAll.
function renderItem(name) {
    return "<li>" + name + "</li>";
}
