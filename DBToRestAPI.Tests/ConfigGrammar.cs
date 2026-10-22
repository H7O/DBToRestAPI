using System.Xml.Linq;

namespace DBToRestAPI.Tests;

/// <summary>
/// Every tag the engine reads, where it reads it, written out from docs/reference/tags.md and the
/// keys the code reads. The engine ignores a tag it doesn't know without a log line, so a
/// misspelled or misplaced tag in an example or in the shipped config would go unnoticed; checking
/// them against this tree catches it.
/// </summary>
/// <remarks>
/// A line is a tag, and indentation nests. <c>*</c> is a child the configuration names (an
/// endpoint, a store, a provider). <c>+</c> after a name lets it repeat among its siblings (a
/// chain's queries); any other repeated sibling is numbered by the configuration reader, and stops
/// matching. <c>~</c> leaves its content unchecked. A line <c>@name</c> at the left margin defines a
/// block; indented, it includes the block's tags there, and after a tag it gives the tag the
/// block's tags. Attributes are read as child tags, as the configuration reader does.
/// </remarks>
internal static class ConfigGrammar
{
    private const string Text = """
        @cors
          pattern
          fallback_origin
          allowed_headers
          allow_credentials
          max_age
          exposed_headers
        @rate_limit
          max_requests
          window_seconds
          per
          enabled
          message
        @cache
          memory
            duration_in_milliseconds
            invalidators
            exclude_status_codes_from_cache
        @token_checks
          authority
          audience
          issuer
          validate_issuer
          validate_audience
          validate_lifetime
          clock_skew_seconds
          required_scopes
          required_roles
          userinfo_fallback_claims
          userinfo_cache_duration_seconds
          userinfo_timeout_seconds
        @route_authorize
          enabled
          provider
          provider_hint_header
          @token_checks
        @patterns
          json_variables_pattern
          query_string_variables_pattern
          form_data_variables_pattern
          form_variables_pattern
          route_variables_pattern
          headers_variables_pattern
          auth_variables_pattern
          settings_variables_pattern
          http_variable_pattern
        @upload
          files_json_field_or_form_field_name
          stores
          filename_field_in_payload
          base64_content_field_in_payload
          relative_file_path_structure
          max_number_of_files
          max_file_size_in_bytes
          permitted_file_extensions
          overwrite_existing_files
          pass_files_content_to_query
          accept_caller_defined_file_ids
        @route_protection
          cache @cache
          cors @cors
          rate_limit @rate_limit
          api_keys_collections
          authorize @route_authorize
          mandatory_parameters
        @endpoint
          route
          verb
          host
          query+
            connection_string_name
            json_var
            db_command_timeout
          count_query
          connection_string_name
          db_command_timeout
          buffer_rows
          response_structure
          success_status_code
          root_node
          route_variable_pattern
          @patterns
          @route_protection
          file_management
            @upload
            store
            file_variables_pattern
          openapi
            enabled
            summary
            description
            tags
            response_schema
        @gateway_route
          route
          url
          excluded_headers
          applied_headers
            *+
              name
              value
          ignore_target_route_certificate_errors
          target_route_timeout_seconds
          @route_protection
        settings
          queries
            * @endpoint
          routes
            * @gateway_route
          ConnectionStrings
            *
              provider
          cors @cors
          rate_limit @rate_limit
            client_ip_header
            client_ip_header_trusted_hops
          cache @cache
          authorize
            providers
              * @token_checks
            provider_hint_header
            userinfo_timeout_seconds
          api_keys_collections
            *
              key+
          file_management @upload
            local_file_store
              *
                base_path
                optional
            sftp_file_store
              *
                host
                port
                username
                password
                base_path
                optional
          vars
            *
          regex @patterns
          openapi
            enabled
            title
            version
            description
          static_files
            enabled
            root_path
            default
            serve_unknown_file_types
            cache_control_max_age_seconds
            spa_fallback
          settings_encryption
            encryption_prefix
            sections_to_encrypt
              section+
            data_protection_key_path
          additional_configurations
            path+
            restart_on_path_changes
          env_var_prefix
          max_payload_size_in_bytes
          debug_mode_header_value
          generic_error_message
          success_status_code
          root_node
          db_command_timeout
          buffer_rows
          route_variable_pattern
          excluded_headers
          ignore_target_route_certificate_errors
          target_route_timeout_seconds
          userinfo_timeout_seconds
          @patterns
          Kestrel ~
          Logging ~
          AllowedHosts
        """;

    internal sealed class Tag(string name)
    {
        public string Name { get; } = name;
        public Dictionary<string, Tag> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>A child the configuration names, such as an endpoint.</summary>
        public Tag? Named { get; set; }
        public bool Repeats { get; set; }
        public bool Unchecked { get; set; }

        public Tag? Child(string name) => Children.TryGetValue(name, out var tag) ? tag : Named;
    }

    /// <summary>The root <c>&lt;settings&gt;</c> element.</summary>
    public static Tag Settings { get; } = Parse();

    /// <summary>Every tag of the tree, for an example that shows an element without its parents.</summary>
    public static IReadOnlyList<Tag> All { get; } = Flatten(Settings);

    private static Tag Parse()
    {
        // Blocks first, then the tree, with each block's lines included where it is used.
        var blocks = new Dictionary<string, List<(int Depth, string Line)>>();
        var tree = new List<(int Depth, string Line)>();
        List<(int, string)>? current = null;
        foreach (var raw in Text.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            var depth = (line.Length - line.TrimStart().Length) / 2;
            var content = line.Trim();
            if (depth == 0)
            {
                current = content.StartsWith('@') ? blocks[content[1..]] = [] : tree;
                if (current == tree) tree.Add((0, content));
                continue;
            }
            current!.Add((depth - 1, content));
        }

        var root = new Tag("settings");
        Build(root, Expand(tree.Skip(1).ToList(), blocks), 0);
        return root;
    }

    private static List<(int Depth, string Line)> Expand(List<(int Depth, string Line)> lines, Dictionary<string, List<(int Depth, string Line)>> blocks)
    {
        var result = new List<(int, string)>();
        foreach (var (depth, line) in lines)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts[0].StartsWith('@'))
            {
                result.AddRange(Expand(blocks[parts[0][1..]], blocks).Select(l => (l.Depth + depth, l.Line)));
                continue;
            }
            result.Add((depth, string.Join(' ', parts.Where(p => !p.StartsWith('@')))));
            foreach (var block in parts.Where(p => p.StartsWith('@')))
                result.AddRange(Expand(blocks[block[1..]], blocks).Select(l => (l.Depth + depth + 1, l.Line)));
        }
        return result;
    }

    private static int Build(Tag parent, List<(int Depth, string Line)> lines, int index)
    {
        var depth = index < lines.Count ? lines[index].Depth : 0;
        while (index < lines.Count && lines[index].Depth == depth)
        {
            var parts = lines[index].Line.Split(' ');
            var name = parts[0].TrimEnd('+');
            var tag = new Tag(name)
            {
                Repeats = parts[0].EndsWith('+'),
                Unchecked = parts.Contains("~"),
            };
            if (name == "*")
                parent.Named = tag;
            else
                parent.Children[name] = tag;
            index++;
            if (index < lines.Count && lines[index].Depth > depth)
                index = Build(tag, lines, index);
        }
        return index;
    }

    private static List<Tag> Flatten(Tag tag)
    {
        var all = new List<Tag> { tag };
        foreach (var child in tag.Children.Values.Append(tag.Named).OfType<Tag>())
            all.AddRange(Flatten(child));
        return all;
    }

    /// <summary>
    /// What is wrong with <paramref name="element"/> read as <paramref name="tag"/>: each tag the
    /// engine doesn't read there, and each repeated sibling. Elements after a comment that starts
    /// with "Don't" are skipped up to the next comment, so a do-and-don't pair can show the mistake.
    /// </summary>
    public static List<(XObject Where, string Problem)> Check(XElement element, Tag tag, string path)
    {
        var problems = new List<(XObject, string)>();
        if (tag.Unchecked)
            return problems;

        // AGENTS.md rule 1: a query outside CDATA breaks at its first < or &.
        if (tag.Name is "query" or "count_query"
            && element.Nodes().OfType<XText>().Any(t => t is not XCData && !string.IsNullOrWhiteSpace(t.Value) && t.Value.Trim() != "..."))
            problems.Add((element, $"{path}: write the query inside <![CDATA[ ... ]]>"));

        foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration))
            if (tag.Child(attribute.Name.LocalName) is null)
                problems.Add((attribute, $"{path}: the engine reads no `{attribute.Name.LocalName}` attribute here"));

        var counts = element.Elements().GroupBy(e => e.Name.LocalName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var skipping = false;
        foreach (var node in element.Nodes())
        {
            if (node is XComment comment)
            {
                var said = comment.Value.TrimStart();
                skipping = said.StartsWith("Don't", StringComparison.OrdinalIgnoreCase)
                           || said.StartsWith("Don’t", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (node is not XElement child || skipping)
                continue;

            var name = child.Name.LocalName;
            var childPath = $"{path}/{name}";
            var childTag = tag.Child(name);
            if (childTag is null)
            {
                problems.Add((child, $"{path}: the engine reads no `<{name}>` here"));
                continue;
            }
            if (counts[name] > 1 && !childTag.Repeats)
                problems.Add((child, $"{childPath}: repeated beside another `<{name}>`, so the configuration reader numbers both and neither matches"));
            problems.AddRange(Check(child, childTag, childPath));
        }
        return problems;
    }
}
