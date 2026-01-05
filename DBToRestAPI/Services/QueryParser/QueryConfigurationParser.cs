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
            var result = new List<QueryDefinition>();

            if (section == null || !section.Exists())
            {
                return result;
            }

            // Get the fallback connection string name from the section level
            var fallbackConnectionStringName = section[ConnectionStringNameKey]?.Trim();
            if (string.IsNullOrEmpty(fallbackConnectionStringName))
            {
                fallbackConnectionStringName = DefaultConnectionStringName;
            }

            // Get all query children - they could be indexed as "query" (single) or "query:0", "query:1", etc. (multiple)
            var querySection = section.GetSection(QueryKey);

            if (!querySection.Exists())
            {
                return result;
            }

            // Check if it's a single query (has a value) or multiple queries (has children)
            var queryValue = querySection.Value;

            if (!string.IsNullOrEmpty(queryValue))
            {
                // Single query node with direct value
                var queryDef = ParseQueryNode(querySection, fallbackConnectionStringName);
                result.Add(queryDef);
                return result;
            }

            // Multiple query nodes - iterate through children
            foreach (var queryChild in querySection.GetChildren())
            {
                var queryDef = ParseQueryNode(queryChild, fallbackConnectionStringName);
                result.Add(queryDef);
            }

            return result;
        }

        /// <summary>
        /// Parses a single query configuration node into a <see cref="QueryDefinition"/>.
        /// </summary>
        private QueryDefinition ParseQueryNode(IConfigurationSection queryNode, string fallbackConnectionStringName)
        {
            // Get query text
            var queryText = queryNode.Value?.Trim() ?? string.Empty;

            // Get connection string name: attribute on query → fallback → default
            var connectionStringName = queryNode[ConnectionStringNameKey]?.Trim();
            if (string.IsNullOrEmpty(connectionStringName))
            {
                connectionStringName = fallbackConnectionStringName;
            }

            // Get json variable name: attribute on query → default "json"
            var jsonVariableName = queryNode[JsonVarKey]?.Trim();
            if (string.IsNullOrEmpty(jsonVariableName))
            {
                jsonVariableName = DefaultJsonVariableName;
            }

            return new QueryDefinition
            {
                QueryText = queryText,
                ConnectionStringName = connectionStringName,
                JsonVariableName = jsonVariableName
            };
        }
    }
}
