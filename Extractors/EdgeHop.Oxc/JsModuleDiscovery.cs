using System.Text.RegularExpressions;

namespace EdgeHop.Oxc;

/// <summary>
/// Finds the JS/TS the oxc binary should parse, under a solution/repo root: standalone
/// <c>.js/.ts/.jsx/.tsx</c> modules (including collocated <c>Component.razor.js</c>), plus
/// inline <c>&lt;script&gt;</c> blocks embedded in <c>.razor/.cshtml/.html</c> files. Build
/// artifacts and third-party trees are skipped. Every module carries a repo-relative id;
/// embedded blocks get a <c>path#N</c> id but keep the authored file as their <c>sourceDoc</c>.
/// </summary>
internal static partial class JsModuleDiscovery
{
    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "obj", "bin", ".git", ".vs", ".idea", "dist", "out", "coverage", "_framework",
    };

    private static readonly HashSet<string> MarkupExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".razor", ".cshtml", ".html", ".htm",
    };

    public static IReadOnlyList<OxcModuleInput> Discover(string root, Action<string>? log)
    {
        var modules = new List<OxcModuleInput>();
        var fullRoot = Path.GetFullPath(root);

        foreach (var file in EnumerateSourceFiles(fullRoot))
        {
            var extension = Path.GetExtension(file);
            var relative = ToRelative(fullRoot, file);

            if (TryScriptLang(file, extension, out var lang))
            {
                string source;
                try
                {
                    source = File.ReadAllText(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log?.Invoke($"oxc: could not read '{relative}': {ex.Message}");
                    continue;
                }

                modules.Add(new OxcModuleInput
                {
                    ModuleId = relative,
                    SourceDoc = relative,
                    Lang = lang,
                    Source = source,
                });
            }
            else if (MarkupExtensions.Contains(extension))
            {
                string markup;
                try
                {
                    markup = File.ReadAllText(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                var index = 0;
                foreach (var script in ExtractInlineScripts(markup))
                {
                    modules.Add(new OxcModuleInput
                    {
                        ModuleId = $"{relative}#{index}",
                        SourceDoc = relative,
                        Lang = "js",
                        Source = script,
                    });
                    index++;
                }
            }
        }

        return modules;
    }

    /// <summary>Standalone script file → its oxc language, or false for a non-script /
    /// declaration-only / minified file we deliberately skip.</summary>
    private static bool TryScriptLang(string file, string extension, out string lang)
    {
        lang = extension.ToLowerInvariant() switch
        {
            ".tsx" => "tsx",
            ".jsx" => "jsx",
            ".ts" => "ts",
            ".js" or ".mjs" or ".cjs" => "js",
            _ => "",
        };

        if (lang.Length == 0)
        {
            return false;
        }

        // Declaration files carry no runtime; minified bundles are third-party noise.
        var name = Path.GetFileName(file);
        if (name.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    /// <summary>Inline <c>&lt;script&gt;</c> bodies with no <c>src</c> and a JS (or unset)
    /// <c>type</c>. HTML comments are matched and skipped, so a <c>&lt;script&gt;</c> written inside
    /// <c>&lt;!-- ... --&gt;</c> is treated as prose, not a real tag. A first-cut HTML scan — embedded
    /// JS is parser-only fidelity by design (a <c>&lt;script&gt;</c> inside a JS string or a Razor
    /// <c>@* … *@</c> comment is not special-cased).</summary>
    private static IEnumerable<string> ExtractInlineScripts(string markup)
    {
        foreach (Match match in ScriptTag().Matches(markup))
        {
            if (match.Groups["comment"].Success)
            {
                continue; // A <script> written inside an <!-- HTML comment --> is prose, not a tag.
            }

            var attributes = match.Groups["attrs"].Value;
            if (HasAttribute(attributes, "src"))
            {
                continue; // external script: the referenced file is discovered on its own.
            }

            var type = AttributeValue(attributes, "type");
            if (type is not null
                && type.Length != 0
                && !type.Equals("text/javascript", StringComparison.OrdinalIgnoreCase)
                && !type.Equals("application/javascript", StringComparison.OrdinalIgnoreCase)
                && !type.Equals("module", StringComparison.OrdinalIgnoreCase))
            {
                continue; // e.g. type="text/html" templates: not JavaScript.
            }

            var body = match.Groups["body"].Value;
            if (!string.IsNullOrWhiteSpace(body))
            {
                yield return body;
            }
        }
    }

    private static bool HasAttribute(string attributes, string name) =>
        Regex.IsMatch(attributes, $@"\b{Regex.Escape(name)}\b", RegexOptions.IgnoreCase);

    private static string? AttributeValue(string attributes, string name)
    {
        var match = Regex.Match(
            attributes,
            $@"\b{Regex.Escape(name)}\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+))",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["v"].Value : null;
    }

    private static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var directory = stack.Pop();

            string[] subdirectories;
            try
            {
                subdirectories = Directory.GetDirectories(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var subdirectory in subdirectories)
            {
                if (!SkipDirectories.Contains(Path.GetFileName(subdirectory)))
                {
                    stack.Push(subdirectory);
                }
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }
        }
    }

    private static string ToRelative(string root, string file) =>
        Path.GetRelativePath(root, file).Replace('\\', '/');

    // Matches an HTML comment OR a <script> element. Comments are listed first and captured (not
    // discarded) so a <script> written inside <!-- ... --> is consumed as comment text and never
    // mistaken for a real tag; the caller skips comment matches. Because matches are non-overlapping,
    // a <!-- or --> appearing inside a real <script> body stays part of that script.
    [GeneratedRegex(
        @"(?<comment><!--.*?-->)|<script\b(?<attrs>[^>]*)>(?<body>.*?)</script>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptTag();
}
