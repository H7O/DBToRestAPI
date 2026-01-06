namespace DBToRestAPI.Services.QueryParser
{
    /// <summary>
    /// Parses query configuration sections into a list of <see cref="QueryDefinition"/> objects.
    /// </summary>
    public interface IQueryConfigurationParser
    {
        /// <summary>
        /// Parses the "query" nodes from the given configuration section.
        /// </summary>
        /// <param name="section">The configuration section containing query nodes.</param>
        /// <returns>
        /// A list of <see cref="QueryDefinition"/> objects. 
        /// Returns an empty list if no query nodes are found.
        /// </returns>
        List<QueryDefinition> Parse(IConfigurationSection section);

        /// <summary>
        /// Parses the "query" nodes from the configuration section at the specified path.
        /// </summary>
        /// <param name="sectionPath">The configuration section path (e.g., "queries:hello_world").</param>
        /// <returns>
        /// A list of <see cref="QueryDefinition"/> objects.
        /// Returns an empty list if the section doesn't exist or contains no query nodes.
        /// </returns>
        List<QueryDefinition> Parse(string sectionPath);

        /// <summary>
        /// Parses query nodes with a custom node name from the given configuration section.
        /// Use this to parse "count_query" or other custom query node types.
        /// </summary>
        /// <param name="section">The configuration section containing query nodes.</param>
        /// <param name="queryNodeName">The name of the query node to parse (e.g., "query", "count_query").</param>
        /// <returns>
        /// A list of <see cref="QueryDefinition"/> objects.
        /// Returns an empty list if no matching nodes are found.
        /// </returns>
        List<QueryDefinition> Parse(IConfigurationSection section, string queryNodeName);
    }
}
