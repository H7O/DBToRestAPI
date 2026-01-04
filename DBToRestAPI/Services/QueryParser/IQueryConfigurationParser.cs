namespace DBToRestAPI.Services.QueryParser
{
    /// <summary>
    /// Parses query configuration sections into a list of <see cref="QueryDefinition"/> objects.
    /// </summary>
    public interface IQueryConfigurationParser
    {
        /// <summary>
        /// Parses the given configuration section and returns a list of query definitions.
        /// </summary>
        /// <param name="section">The configuration section containing query nodes.</param>
        /// <returns>
        /// A list of <see cref="QueryDefinition"/> objects. 
        /// Returns an empty list if no query nodes are found.
        /// </returns>
        List<QueryDefinition> Parse(IConfigurationSection section);

        /// <summary>
        /// Parses the configuration section at the specified path and returns a list of query definitions.
        /// </summary>
        /// <param name="sectionPath">The configuration section path (e.g., "queries:hello_world").</param>
        /// <returns>
        /// A list of <see cref="QueryDefinition"/> objects.
        /// Returns an empty list if the section doesn't exist or contains no query nodes.
        /// </returns>
        List<QueryDefinition> Parse(string sectionPath);
    }
}
