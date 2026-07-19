// catalog.js — the feature-catalog data + behavior for the pure JS/HTML EdgeHop Explorer.
//
// This is the JavaScript mirror of the Blazor sample's C# domain. The oxc extractor turns it into
// graph nodes/edges (every id carries the `js|` tier tag):
//   * the module            -> a Namespace node ("catalog.js")
//   * each function / method -> a Method node
//   * the class              -> a NamedType node; its field -> a Field node
//   * `export const`         -> a Field node
//   * a bare-identifier call to a same-file function -> a CALLS edge
//
// Note: JS CALLS resolve by scope/binding WITHIN a file. A call across an `import` boundary does not
// resolve (oxc does not type-check), so the resolvable calls below are all same-module.

export const FEATURES = [
    "find_symbol",
    "get_callers",
    "get_relationships",
    "get_path",
    "graph_stats",
];

// Exported function; calls the module-private label() -> a resolved CALLS edge.
export function describeFeature(name) {
    return label(name);
}

// Module-private helper (a Method node, but not exported).
function label(name) {
    return "feature: " + name;
}

// A class -> a NamedType node; `size` is a Field node; `first` is a Method node and a CALLS source.
export class Catalog {
    size = 0;

    first() {
        return describeFeature(FEATURES[0]);
    }
}
