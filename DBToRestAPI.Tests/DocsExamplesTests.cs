using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace DBToRestAPI.Tests;

/// <summary>
/// The XML examples in the docs, and the shipped config, use only tags the engine reads, where it
/// reads them (<see cref="ConfigGrammar"/>), parse as XML, and keep each query in CDATA; and every
/// placeholder in the docs is one the engine knows. An agent copies the first complete example it
/// finds, and the engine ignores a tag it doesn't know without a word.
/// </summary>
public partial class DocsExamplesTests
{
    internal static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder != null && !File.Exists(Path.Combine(folder.FullName, "DBToRestAPI.sln")))
            folder = folder.Parent;
        return folder?.FullName ?? throw new InvalidOperationException("DBToRestAPI.sln not found above the test folder");
    }

    // The pages a published build holds next to the executable, laid out as in the repository: the
    // list in DBToRestAPI.csproj ("The docs"), which this one must match.
    private static readonly string[] ShippedFiles = ["AGENTS.md", "CLAUDE.md", "llms.txt", "llms.md", "README.md", "LICENSE", "docs/AUTHORING.md"];
    private static readonly string[] ShippedFolders = ["docs/reference", "docs/topics", "docs/tutorial"];

    private static bool Shipped(string path)
        => ShippedFiles.Contains(path)
           || ShippedFolders.Any(folder => path.StartsWith(folder + "/", StringComparison.Ordinal) && path.EndsWith(".md", StringComparison.Ordinal));

    /// <summary>The pages agents read, from the repository's root, as a published build holds them.</summary>
    internal static IEnumerable<string> ShippedPages()
        => ShippedFolders
            .SelectMany(folder => Directory.GetFiles(Path.Combine(Root, folder), "*.md", SearchOption.AllDirectories))
            .Select(p => Path.GetRelativePath(Root, p).Replace('\\', '/'))
            .Concat(ShippedFiles.Where(f => f.EndsWith(".md") || f.EndsWith(".txt")))
            .Order(StringComparer.Ordinal);

    public static TheoryData<string> Pages()
    {
        var pages = new TheoryData<string>();
        foreach (var page in ShippedPages())
            pages.Add(page);
        return pages;
    }

    [GeneratedRegex(@"``[^\n]*?``|`[^`\n]*`")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"\]\((?<target>[^)\s]+)\)")]
    private static partial Regex Link();

    [Fact]
    public void EveryLinkInTheShippedPages_ReachesAShippedFile()
    {
        // A link that works in a clone but not in a release folder, such as one to TODO.md or to the
        // engine's source, is broken for an agent that has only the release.
        var broken = new List<string>();
        foreach (var page in ShippedPages())
        {
            var text = File.ReadAllText(Path.Combine(Root, page)).Replace("\r\n", "\n");
            text = InlineCode().Replace(CodeBlock().Replace(text, ""), "");
            foreach (Match link in Link().Matches(text))
            {
                var target = link.Groups["target"].Value.Split('#')[0];
                if (target.Length == 0 || target.Contains(':'))
                    continue;   // an anchor on the same page, or a URL
                var full = Path.GetFullPath(Path.Combine(Root, Path.GetDirectoryName(page)!, target));
                var path = Path.GetRelativePath(Root, full).Replace('\\', '/').TrimEnd('/');
                var reaches = File.Exists(full) ? Shipped(path) : Directory.Exists(full) && (path == "docs" || ShippedFolders.Contains(path));
                if (!reaches)
                    broken.Add($"{page}: `{link.Groups["target"].Value}` is not in a release folder");
            }
        }

        Assert.True(broken.Count == 0, string.Join(Environment.NewLine, broken));
    }

    /// <summary>XML that isn't the engine's configuration: a project file, IIS's web.config, a page.</summary>
    private static readonly HashSet<string> OtherXml = new(StringComparer.OrdinalIgnoreCase)
    {
        "Project", "PropertyGroup", "ItemGroup", "None", "Content", "configuration", "system.webServer", "location", "html",
    };

    // The placeholders the engine resolves; {{name}} has no kind.
    private static readonly HashSet<string> PlaceholderKinds =
        ["auth", "pq", "http", "s", "settings", "type", "date", "file", "fs", "j", "qs", "h", "r", "f"];

    [GeneratedRegex(@"^(?<indent>[ \t]*)(?<fence>```+)(?<lang>[\w-]*)[^\n]*\n(?<code>.*?)^[ \t]*\k<fence>[ \t]*$", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex CodeBlock();

    [GeneratedRegex(@"\{(?<kind>[a-z_]+)\{")]
    private static partial Regex Placeholder();

    internal static IEnumerable<(string Lang, string Code, int Line)> Blocks(string text)
    {
        foreach (Match block in CodeBlock().Matches(text))
        {
            var code = block.Groups["code"];
            yield return (block.Groups["lang"].Value, code.Value, text[..code.Index].Count(c => c == '\n') + 1);
        }
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void EveryXmlExample_UsesOnlyTagsTheEngineReads(string page)
    {
        var problems = new List<string>();
        var text = File.ReadAllText(Path.Combine(Root, page)).Replace("\r\n", "\n");
        foreach (var (lang, code, line) in Blocks(text).Where(b => b.Lang == "xml"))
        {
            XElement wrapper;
            try
            {
                wrapper = XElement.Parse($"<example>\n{code}</example>", LoadOptions.SetLineInfo);
            }
            catch (XmlException ex)
            {
                problems.Add($"{page}:{line + ex.LineNumber - 2}: not valid XML: {ex.Message}");
                continue;
            }
            if (wrapper.Elements().Any(e => OtherXml.Contains(e.Name.LocalName)))
                continue;

            foreach (var (where, problem) in CheckExample(wrapper))
                problems.Add($"{page}:{line + ((IXmlLineInfo)where).LineNumber - 2}: {problem}");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// An example's elements: under <c>&lt;settings&gt;</c> from the root, otherwise each one where
    /// it fits best, since an example often shows an endpoint or a block without its parents.
    /// </summary>
    private static List<(XObject, string)> CheckExample(XElement wrapper)
    {
        var problems = new List<(XObject, string)>();
        var skipping = false;
        foreach (var node in wrapper.Nodes())
        {
            if (node is XComment comment)
            {
                var said = comment.Value.TrimStart();
                skipping = said.StartsWith("Don't", StringComparison.OrdinalIgnoreCase) || said.StartsWith("Don’t", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (node is not XElement element || skipping)
                continue;

            var name = element.Name.LocalName;
            if (name == "settings")
            {
                problems.AddRange(ConfigGrammar.Check(element, ConfigGrammar.Settings, "settings"));
                continue;
            }
            var best = ConfigGrammar.All
                .Select(parent => parent.Child(name) is { } tag ? ConfigGrammar.Check(element, tag, $"{parent.Name}/{name}") : null)
                .OfType<List<(XObject, string)>>()
                .MinBy(found => found.Count);
            problems.AddRange(best ?? [(element, $"the engine reads no `<{name}>` anywhere")]);
        }
        return problems;
    }

    [Fact]
    public void TheShippedConfig_UsesOnlyTagsTheEngineReads()
    {
        var problems = new List<string>();
        // encryption_test.xml holds demo values for settings encryption, which nothing reads.
        foreach (var file in Directory.GetFiles(Path.Combine(Root, "DBToRestAPI", "config"), "*.xml")
                     .Where(f => !Path.GetFileName(f).Equals("encryption_test.xml", StringComparison.OrdinalIgnoreCase)))
        {
            var settings = XElement.Load(file, LoadOptions.SetLineInfo);
            foreach (var (where, problem) in ConfigGrammar.Check(settings, ConfigGrammar.Settings, "settings"))
                problems.Add($"{Path.GetFileName(file)}:{((IXmlLineInfo)where).LineNumber}: {problem}");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void EveryPlaceholder_IsOneTheEngineResolves(string page)
    {
        var text = File.ReadAllText(Path.Combine(Root, page)).Replace("\r\n", "\n");
        var unknown = Blocks(text)
            .SelectMany(b => Placeholder().Matches(b.Code).Select(m => (b.Line, Kind: m.Groups["kind"].Value)))
            .Where(p => !PlaceholderKinds.Contains(p.Kind))
            .Select(p => $"{page}: block at line {p.Line}: unknown placeholder `{{{p.Kind}{{`")
            .ToList();

        Assert.True(unknown.Count == 0, string.Join(Environment.NewLine, unknown));
    }

    [Theory]
    [InlineData("<orders><route>orders</route><respone_structure>array</respone_structure><query><![CDATA[SELECT 1]]></query></orders>", "respone_structure")]
    [InlineData("<orders><route>orders</route><api_keys><key>x</key></api_keys><query><![CDATA[SELECT 1]]></query></orders>", "api_keys")]
    [InlineData("<settings><cors><pattern>a</pattern></cors><cors><max_age>1</max_age></cors></settings>", "repeated")]
    [InlineData("<settings><routes><g><url>https://x.example</url><verb>GET</verb></g></routes></settings>", "verb")]
    [InlineData("<settings><queries><q><rate_limit><client_ip_header>X</client_ip_header></rate_limit></q></queries></settings>", "client_ip_header")]
    [InlineData("<settings><file_management><local_file_store><a><base_pth>x</base_pth></a></local_file_store></file_management></settings>", "base_pth")]
    [InlineData("<orders><route>orders</route><query>SELECT 1</query></orders>", "CDATA")]
    [InlineData("<orders><route>orders</route><query connection_sting_name=\"x\"><![CDATA[SELECT 1]]></query></orders>", "connection_sting_name")]
    public void TheCheck_FindsATagTheEngineWouldIgnore(string example, string problem)
    {
        var found = CheckExample(XElement.Parse($"<example>{example}</example>", LoadOptions.SetLineInfo));

        Assert.Contains(found, f => f.Item2.Contains(problem));
    }

    [Fact]
    public void TheCheck_LetsADontExampleShowTheMistake()
    {
        var found = CheckExample(XElement.Parse("<example><!-- Don't: --><orders><api_keys/></orders><!-- Do: --><orders><api_keys_collections>a</api_keys_collections></orders></example>"));

        Assert.Empty(found);
    }

    [Fact]
    public void TheGrammar_HoldsEveryTagOfTheShippedConfig()
    {
        // A guard for the grammar itself: tags it must know, from each part of the tree.
        Assert.NotNull(ConfigGrammar.Settings.Child("queries")?.Named?.Child("query"));
        Assert.True(ConfigGrammar.Settings.Child("queries")!.Named!.Child("query")!.Repeats);
        Assert.NotNull(ConfigGrammar.Settings.Child("queries")!.Named!.Child("cors")?.Child("exposed_headers"));
        Assert.NotNull(ConfigGrammar.Settings.Child("routes")?.Named?.Child("authorize")?.Child("clock_skew_seconds"));
        Assert.NotNull(ConfigGrammar.Settings.Child("rate_limit")?.Child("client_ip_header_trusted_hops"));
        Assert.Null(ConfigGrammar.Settings.Child("queries")!.Named!.Child("rate_limit")!.Child("client_ip_header"));
        Assert.NotNull(ConfigGrammar.Settings.Child("file_management")?.Child("sftp_file_store")?.Named?.Child("password"));
    }
}
