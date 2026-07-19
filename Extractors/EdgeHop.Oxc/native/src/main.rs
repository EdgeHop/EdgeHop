// edgehop-oxc — the EdgeHop JS/TS extractor binary.
//
// Reads a JSON request from stdin ({ "modules": [ { moduleId, sourceDoc, lang, source } ] })
// and writes a JSON graph to stdout ({ nodes, edges, interopExports, diagnostics }). All node
// ids carry a mandatory `js|` tier tag so they can never collide with a C# (Roslyn) id. The
// .NET plugin stamps `branch` and reconciles the rows into the same store as the C# graph.
//
// Coverage: full JS/TS/JSX syntax (oxc_parser) + scope/binding resolution (oxc_semantic).
// CALLS edges are emitted for calls that bind to a declared function/class (imports, in-scope
// functions). Type-checker-driven member resolution (foo.bar() via foo's inferred type) is
// intentionally out of scope — oxc does not type-check; see the EdgeHop oxc decision.

use std::collections::HashMap;
use std::io::Read;

use oxc::allocator::Allocator;
use oxc::ast::ast::*;
use oxc::ast_visit::{walk, Visit};
use oxc::parser::Parser;
use oxc::semantic::{Scoping, SemanticBuilder, SymbolId};
use oxc::span::{SourceType, Span};
use oxc::syntax::scope::ScopeFlags;
use serde::{Deserialize, Serialize};

#[derive(Deserialize)]
struct Request {
    modules: Vec<ModuleInput>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct ModuleInput {
    /// Stable module identity used in node ids (e.g. "src/api/client.ts" or
    /// "Pages/Home.razor#0" for an embedded <script> block).
    module_id: String,
    /// The authored document a node maps to (the .razor/.cshtml/.html/.js/.ts path).
    source_doc: String,
    /// "ts" | "tsx" | "js" | "jsx".
    lang: String,
    source: String,
}

#[derive(Serialize, Default)]
#[serde(rename_all = "camelCase")]
struct Response {
    nodes: Vec<Node>,
    edges: Vec<Edge>,
    interop_exports: Vec<InteropExport>,
    dotnet_calls: Vec<DotNetCall>,
    diagnostics: Vec<String>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Node {
    id: String,
    name: String,
    kind: String,
    source_doc: String,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Edge {
    #[serde(rename = "type")]
    edge_type: String,
    from_id: String,
    to_id: String,
    source_doc: String,
}

/// A JS symbol callable from C# JS-interop: a module-scoped exported function/const. The .NET
/// side matches C# `IJSRuntime.InvokeAsync("<name>")` call sites against these to emit JS_CALLS.
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct InteropExport {
    name: String,
    module_id: String,
    symbol_id: String,
    source_doc: String,
}

/// A JS -> C# interop call site: `DotNet.invokeMethod[Async]("Assembly", "Identifier", ...)`
/// (static) or `objRef.invokeMethod[Async]("Identifier", ...)` (instance). The .NET side matches
/// these against C# `[JSInvokable]` methods to emit JS_INVOKES. `caller_id` is the enclosing JS
/// function's node id; `assembly` is present only for the static form.
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct DotNetCall {
    caller_id: String,
    assembly: Option<String>,
    identifier: String,
    is_static: bool,
    source_doc: String,
}

const NAMED_TYPE: &str = "NamedType";
const METHOD: &str = "Method";
const FIELD: &str = "Field";
const NAMESPACE: &str = "Namespace";
const CONTAINS: &str = "CONTAINS";
const CALLS: &str = "CALLS";

fn main() {
    let mut input = String::new();
    if std::io::stdin().read_to_string(&mut input).is_err() {
        eprintln!("edgehop-oxc: failed to read stdin");
        std::process::exit(1);
    }

    let request: Request = match serde_json::from_str(&input) {
        Ok(r) => r,
        Err(e) => {
            eprintln!("edgehop-oxc: invalid request json: {e}");
            std::process::exit(1);
        }
    };

    let mut response = Response::default();
    for module in &request.modules {
        extract_module(module, &mut response);
    }

    match serde_json::to_string(&response) {
        Ok(json) => println!("{json}"),
        Err(e) => {
            eprintln!("edgehop-oxc: failed to serialize response: {e}");
            std::process::exit(1);
        }
    }
}

/// Per-module accumulator: the shared response plus the two maps the CALLS pass needs — symbol
/// id -> node id (to resolve a call's TARGET) and function span -> node id (to attribute a call
/// to its enclosing function/method without recomputing the id, so caller ids never diverge).
struct Collector<'a> {
    out: &'a mut Response,
    symbol_to_node: HashMap<SymbolId, String>,
    span_to_node: HashMap<Span, String>,
    module_id: &'a str,
    source_doc: &'a str,
}

fn source_type_for(lang: &str) -> SourceType {
    match lang {
        "tsx" => SourceType::tsx(),
        "ts" => SourceType::ts(),
        "jsx" => SourceType::jsx(),
        _ => SourceType::mjs(),
    }
}

fn extract_module(module: &ModuleInput, out: &mut Response) {
    let allocator = Allocator::default();
    let source_type = source_type_for(&module.lang);
    let parsed = Parser::new(&allocator, &module.source, source_type).parse();

    for diagnostic in &parsed.diagnostics {
        out.diagnostics
            .push(format!("{}: {}", module.source_doc, diagnostic.message));
    }

    // Semantic analysis populates the AST's symbol_id / reference_id cells (interior mutability),
    // which the structural walk and the CALLS pass then read.
    let semantic = SemanticBuilder::new().build(&parsed.program).semantic;

    // The module itself is a Namespace container; every top-level declaration is CONTAINed by it.
    let module_node_id = format!("{NAMESPACE}:js|{}", module.module_id);
    out.nodes.push(Node {
        id: module_node_id.clone(),
        name: leaf_name(&module.module_id),
        kind: NAMESPACE.to_string(),
        source_doc: module.source_doc.clone(),
    });

    let mut col = Collector {
        out,
        symbol_to_node: HashMap::new(),
        span_to_node: HashMap::new(),
        module_id: &module.module_id,
        source_doc: &module.source_doc,
    };

    // Pass 1 — structure: nodes, CONTAINS, interop exports, and the two id maps.
    for statement in &parsed.program.body {
        extract_statement(&mut col, statement, &module_node_id, "");
    }

    // Pass 2 — calls: a full-tree walk that resolves each bound call to a CALLS edge.
    let Collector {
        out,
        symbol_to_node,
        span_to_node,
        source_doc,
        ..
    } = col;
    let mut resolver = CallResolver {
        scoping: semantic.scoping(),
        symbol_to_node: &symbol_to_node,
        span_to_node: &span_to_node,
        source_doc,
        edges: &mut out.edges,
        dotnet_calls: &mut out.dotnet_calls,
        enclosing: Vec::new(),
    };
    resolver.visit_program(&parsed.program);
}

/// Walks a top-level statement, emitting a node + CONTAINS edge for each declaration and
/// recursing into class bodies. `qualifier` is the dotted container-name path ("" at module
/// scope, "WidgetClient" inside that class) so nested same-named symbols get distinct ids.
fn extract_statement(col: &mut Collector, statement: &Statement, container_id: &str, qualifier: &str) {
    match statement {
        Statement::FunctionDeclaration(func) => handle_function(col, func, container_id, qualifier, false),
        Statement::ClassDeclaration(class) => handle_class(col, class, container_id, qualifier, false),
        Statement::VariableDeclaration(var) => handle_variable(col, var, container_id, qualifier, false),
        Statement::TSTypeAliasDeclaration(alias) => {
            emit_member(col, NAMED_TYPE, alias.id.name.as_str(), container_id, qualifier, false);
        }
        Statement::TSInterfaceDeclaration(iface) => {
            emit_member(col, NAMED_TYPE, iface.id.name.as_str(), container_id, qualifier, false);
        }
        Statement::TSEnumDeclaration(enom) => {
            emit_member(col, NAMED_TYPE, enom.id.name.as_str(), container_id, qualifier, false);
        }
        Statement::ExportNamedDeclaration(export) => {
            if let Some(declaration) = &export.declaration {
                extract_declaration(col, declaration, container_id, qualifier, true);
            }
        }
        Statement::ExportDefaultDeclaration(export) => match &export.declaration {
            ExportDefaultDeclarationKind::FunctionDeclaration(func) => {
                handle_function(col, func, container_id, qualifier, true)
            }
            ExportDefaultDeclarationKind::ClassDeclaration(class) => {
                handle_class(col, class, container_id, qualifier, true)
            }
            _ => {}
        },
        _ => {}
    }
}

/// The `Declaration` payload of an `export` — the same declaration kinds as the statement walk.
fn extract_declaration(
    col: &mut Collector,
    declaration: &Declaration,
    container_id: &str,
    qualifier: &str,
    exported: bool,
) {
    match declaration {
        Declaration::FunctionDeclaration(func) => handle_function(col, func, container_id, qualifier, exported),
        Declaration::ClassDeclaration(class) => handle_class(col, class, container_id, qualifier, exported),
        Declaration::VariableDeclaration(var) => handle_variable(col, var, container_id, qualifier, exported),
        Declaration::TSTypeAliasDeclaration(alias) => {
            emit_member(col, NAMED_TYPE, alias.id.name.as_str(), container_id, qualifier, exported);
        }
        Declaration::TSInterfaceDeclaration(iface) => {
            emit_member(col, NAMED_TYPE, iface.id.name.as_str(), container_id, qualifier, exported);
        }
        Declaration::TSEnumDeclaration(enom) => {
            emit_member(col, NAMED_TYPE, enom.id.name.as_str(), container_id, qualifier, exported);
        }
        _ => {}
    }
}

fn handle_function(col: &mut Collector, func: &Function, container_id: &str, qualifier: &str, exported: bool) {
    let Some(name) = func.name() else {
        return;
    };
    let id = emit_member(col, METHOD, name.as_str(), container_id, qualifier, exported);
    if let Some(binding) = &func.id {
        if let Some(symbol) = binding.symbol_id.get() {
            col.symbol_to_node.insert(symbol, id.clone());
        }
    }
    col.span_to_node.insert(func.span, id);
}

fn handle_variable(col: &mut Collector, var: &VariableDeclaration, container_id: &str, qualifier: &str, exported: bool) {
    for declarator in &var.declarations {
        if let Some(name) = declarator.id.get_identifier_name() {
            emit_member(col, FIELD, name.as_str(), container_id, qualifier, exported);
        }
    }
}

fn handle_class(col: &mut Collector, class: &Class, container_id: &str, qualifier: &str, exported: bool) {
    let Some(class_name) = class.name() else {
        return;
    };
    let class_id = emit_member(col, NAMED_TYPE, class_name.as_str(), container_id, qualifier, exported);
    if let Some(binding) = &class.id {
        if let Some(symbol) = binding.symbol_id.get() {
            col.symbol_to_node.insert(symbol, class_id.clone());
        }
    }

    let inner_qualifier = qualified(qualifier, class_name.as_str());
    for element in &class.body.body {
        match element {
            ClassElement::MethodDefinition(method) => {
                if let Some(name) = method.key.static_name() {
                    let id = emit_member(col, METHOD, name.as_ref(), &class_id, &inner_qualifier, false);
                    // Attribute calls made inside this method to it (its Function's span).
                    col.span_to_node.insert(method.value.span, id);
                }
            }
            ClassElement::PropertyDefinition(property) => {
                if let Some(name) = property.key.static_name() {
                    emit_member(col, FIELD, name.as_ref(), &class_id, &inner_qualifier, false);
                }
            }
            _ => {}
        }
    }
}

/// Emits a member node and its CONTAINS edge; records an interop export when `exported` and it
/// is a module-scoped callable/const. Returns the new node id (so a class can contain members).
fn emit_member(
    col: &mut Collector,
    kind: &str,
    name: &str,
    container_id: &str,
    qualifier: &str,
    exported: bool,
) -> String {
    let qualified_name = qualified(qualifier, name);
    let id = format!("{kind}:js|{}#{qualified_name}", col.module_id);

    col.out.nodes.push(Node {
        id: id.clone(),
        name: name.to_string(),
        kind: kind.to_string(),
        source_doc: col.source_doc.to_string(),
    });
    col.out.edges.push(Edge {
        edge_type: CONTAINS.to_string(),
        from_id: container_id.to_string(),
        to_id: id.clone(),
        source_doc: col.source_doc.to_string(),
    });

    if exported && qualifier.is_empty() && (kind == METHOD || kind == FIELD) {
        col.out.interop_exports.push(InteropExport {
            name: name.to_string(),
            module_id: col.module_id.to_string(),
            symbol_id: id.clone(),
            source_doc: col.source_doc.to_string(),
        });
    }

    id
}

fn qualified(qualifier: &str, name: &str) -> String {
    if qualifier.is_empty() {
        name.to_string()
    } else {
        format!("{qualifier}.{name}")
    }
}

fn leaf_name(module_id: &str) -> String {
    module_id
        .rsplit(['/', '\\'])
        .next()
        .unwrap_or(module_id)
        .to_string()
}

/// Pass 2: walks the whole tree, tracking the enclosing named function/method (via
/// `span_to_node`) and emitting a CALLS edge for each call whose callee binds (via
/// oxc_semantic) to a declared function/class we emitted a node for.
struct CallResolver<'a> {
    scoping: &'a Scoping,
    symbol_to_node: &'a HashMap<SymbolId, String>,
    span_to_node: &'a HashMap<Span, String>,
    source_doc: &'a str,
    edges: &'a mut Vec<Edge>,
    dotnet_calls: &'a mut Vec<DotNetCall>,
    enclosing: Vec<String>,
}

impl<'a> Visit<'a> for CallResolver<'a> {
    fn visit_function(&mut self, func: &Function<'a>, flags: ScopeFlags) {
        let pushed = match self.span_to_node.get(&func.span) {
            Some(node) => {
                self.enclosing.push(node.clone());
                true
            }
            None => false,
        };
        walk::walk_function(self, func, flags);
        if pushed {
            self.enclosing.pop();
        }
    }

    fn visit_call_expression(&mut self, call: &CallExpression<'a>) {
        match &call.callee {
            // `foo(...)` — a bare identifier callee resolves to a declared function/class (CALLS).
            Expression::Identifier(ident) => {
                if let Some(caller) = self.enclosing.last().cloned() {
                    if let Some(reference_id) = ident.reference_id.get() {
                        if let Some(symbol) = self.scoping.get_reference(reference_id).symbol_id() {
                            if let Some(target) = self.symbol_to_node.get(&symbol) {
                                self.edges.push(Edge {
                                    edge_type: CALLS.to_string(),
                                    from_id: caller,
                                    to_id: target.clone(),
                                    source_doc: self.source_doc.to_string(),
                                });
                            }
                        }
                    }
                }
            }
            // `X.invokeMethod[Async](...)` — a Blazor JS -> C# interop call. `DotNet.<m>` is the
            // static form (assembly + identifier); any other receiver is the instance form.
            Expression::StaticMemberExpression(member) => {
                self.collect_dotnet_call(member, &call.arguments);
            }
            _ => {}
        }
        walk::walk_call_expression(self, call);
    }
}

impl<'a> CallResolver<'a> {
    fn collect_dotnet_call(
        &mut self,
        member: &StaticMemberExpression<'a>,
        arguments: &oxc::allocator::Vec<'a, Argument<'a>>,
    ) {
        let method = member.property.name.as_str();
        if method != "invokeMethod" && method != "invokeMethodAsync" {
            return;
        }

        let Some(caller) = self.enclosing.last().cloned() else {
            return; // a call outside any named function has no JS node to attribute to.
        };

        let is_static = matches!(&member.object, Expression::Identifier(id) if id.name == "DotNet");
        // Static: DotNet.invokeMethodAsync("Assembly", "Identifier", ...args).
        // Instance: dotNetRef.invokeMethodAsync("Identifier", ...args).
        let (assembly, identifier) = if is_static {
            (string_arg(arguments, 0), string_arg(arguments, 1))
        } else {
            (None, string_arg(arguments, 0))
        };

        if let Some(identifier) = identifier {
            self.dotnet_calls.push(DotNetCall {
                caller_id: caller,
                assembly,
                identifier,
                is_static,
                source_doc: self.source_doc.to_string(),
            });
        }
    }
}

/// The string-literal value of the `i`th call argument, or None when it is absent, spread, or not
/// a string literal (a computed identifier/assembly is not statically knowable).
fn string_arg<'a>(arguments: &oxc::allocator::Vec<'a, Argument<'a>>, i: usize) -> Option<String> {
    match arguments.get(i) {
        Some(Argument::StringLiteral(literal)) => Some(literal.value.as_str().to_string()),
        _ => None,
    }
}
