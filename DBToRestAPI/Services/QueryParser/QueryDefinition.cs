namespace DBToRestAPI.Services.QueryParser
{
    /// <summary>
    /// Represents a single query definition parsed from configuration.
    /// </summary>
    public class QueryDefinition
    {
        /// <summary>
        /// The SQL query text content.
        /// </summary>
        public string QueryText { get; init; } = string.Empty;

        /// <summary>
        /// The connection string name to use for this query.
        /// Resolved from: query attribute → section fallback → "default".
        /// </summary>
        public string ConnectionStringName { get; init; } = "default";

        /// <summary>
        /// The variable name used to pass previous query results as a JSON array.
        /// Defaults to "json" unless specified via the "json_var" attribute.
        /// </summary>
        public string JsonVariableName { get; init; } = "json";
    }
}
