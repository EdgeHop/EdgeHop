// The entry module of a pure JS project (no .sln, no .csproj) — the directory-index anchor.
export function greet(name) {
    // Same-module call: a resolved JS-internal CALLS edge (greet -> decorate).
    return decorate(name);
}

// Module-private helper — a node, but not an export.
function decorate(text) {
    return "Hello, " + text + "!";
}
