// types.ts — a TypeScript module, to show that oxc handles .ts and that TS type constructs become
// NamedType nodes. (oxc resolves syntax + scope/binding but does NOT type-check, so member calls via
// an inferred type produce no edge — a documented ~10% gap. The same-module call below still
// resolves because toFeature() calls areaOf() by bare identifier.)

export type FeatureName = string;

export interface Feature {
    name: FeatureName;
    area: string;
}

export enum Area {
    Extraction,
    Storage,
    Query,
}

export function toFeature(name: FeatureName): Feature {
    return { name, area: areaOf(name) };
}

function areaOf(name: FeatureName): string {
    return name.startsWith("get") ? "Query" : "Extraction";
}
