namespace DBToRestAPI.Services.QueryParser
{
    /// <summary>
    /// Parses query configuration sections into a list of <see cref="QueryDefinition"/> objects.
    /// </summary>
    public class QueryConfigurationParser : IQueryConfigurationParser
    {
        private readonly IConfiguration _configuration;

        private const string DefaultConnectionStringName = "default";
        private const string DefaultJsonVariableName = "json";
        private const string QueryKey = "query";
        private const string ConnectionStringNameKey = "connection_string_name";
        private const string JsonVarKey = "json_var";

        public QueryConfigurationParser(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        /// <inheritdoc />
        public List<QueryDefinition> Parse(string sectionPath)
        {
            var section = _configuration.GetSection(sectionPath);
            return Parse(section);
        }

        /// <inheritdoc />
        public List<QueryDefinition> Parse(IConfigurationSection section)
        {
            return Parse(section, QueryKey);
        }

        /// <inheritdoc />
        public List<QueryDefinition> Parse(IConfigurationSection section, string queryNodeName)
        {
            if (section == null || !section.Exists())
            {
                return [];
            }

            // Get the fallback connection string name from the section level
            var fallbackConnectionStringName = section[ConnectionStringNameKey]?.Trim();
            if (string.IsNullOrEmpty(fallbackConnectionStringName))
            {
                fallbackConnectionStringName = DefaultConnectionStringName;
            }

            var querySection = section.GetSection(queryNodeName);

            if (!querySection.Exists())
            {
                return [];
            }

            // Check if it's a single query (has a value) or multiple queries (has children)
            var queryValue = querySection.Value;

            if (!string.IsNullOrEmpty(queryValue))
            {
                // Single query - it's both first and last in the chain
                return
                [
                    ParseQueryNode(querySection, fallbackConnectionStringName, index: 0, isLastInChain: true)
                ];
            }

            // Multiple query nodes - collect all children first to determine total count
            var children = querySection.GetChildren().ToList();
            var totalCount = children.Count;

            return children
                .Select((child, index) => ParseQueryNode(
                    child,
                    fallbackConnectionStringName,
                    index,
                    isLastInChain: index == totalCount - 1))
                .ToList();
        }

        /// <summary>
        /// Parses a single query configuration node into a <see cref="QueryDefinition"/>.
        /// </summary>
        private static QueryDefinition ParseQueryNode(
            IConfigurationSection queryNode,
            string fallbackConnectionStringName,
            int index,
            bool isLastInChain)
        {
            var queryText = queryNode.Value?.Trim() ?? string.Empty;

            var connectionStringName = queryNode[ConnectionStringNameKey]?.Trim();
            if (string.IsNullOrEmpty(connectionStringName))
            {
                connectionStringName = fallbackConnectionStringName;
            }

            var jsonVariableName = queryNode[JsonVarKey]?.Trim();
            if (string.IsNullOrEmpty(jsonVariableName))
            {
                jsonVariableName = DefaultJsonVariableName;
            }

            return new QueryDefinition
            {
                Index = index,
                IsLastInChain = isLastInChain,
                QueryText = queryText,
                ConnectionStringName = connectionStringName,
                JsonVariableName = jsonVariableName
            };
        }
    }
}
