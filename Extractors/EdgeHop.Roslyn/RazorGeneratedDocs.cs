using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EdgeHop.Roslyn;

/// <summary>
/// Maps a Razor-generated syntax tree to the authored <c>.razor</c> document that
/// produced it. The Razor SDK source generator emits every component tree with
/// <c>#pragma checksum "C:\...\X.razor" "{guid}" "hash"</c> as its first directive —
/// an absolute path to the exact authored file. This is the reliable mapping key:
/// <c>#line</c> mappings are NOT usable (the class declaration sits in hidden regions
/// and mapped regions frequently point at <c>_Imports.razor</c>).
/// </summary>
internal static class RazorGeneratedDocs
{
    private const string GeneratedSuffix = "_razor.g.cs";
    private const string RazorExtension = ".razor";

    /// <summary>
    /// True iff <paramref name="tree"/> is a Razor-generated tree whose checksum names a
    /// <c>.razor</c> file; <paramref name="razorPath"/> is that (absolute) authored path.
    /// </summary>
    public static bool TryGetRazorPath(SyntaxTree tree, out string razorPath)
    {
        razorPath = "";
        if (!tree.FilePath.EndsWith(GeneratedSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (tree.GetRoot() is not CSharpSyntaxNode root)
        {
            return false;
        }

        var directive = root.GetFirstDirective(
            d => d.IsKind(SyntaxKind.PragmaChecksumDirectiveTrivia));
        if (directive is not PragmaChecksumDirectiveTriviaSyntax pragma)
        {
            return false;
        }

        var file = pragma.File.ValueText;
        if (string.IsNullOrEmpty(file)
            || !file.EndsWith(RazorExtension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        razorPath = file;
        return true;
    }
}
