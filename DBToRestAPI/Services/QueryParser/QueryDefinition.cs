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
        /// Specifies how the input from a previous query should be passed to this query.
        /// Defaults to <see cref="QueryInputMode.Auto"/> if not specified.
        /// </summary>
        public QueryInputMode InputMode { get; init; } = QueryInputMode.Auto;

        /// <summary>
        /// The variable name to use when passing previous query results as a JSON array.
        /// Defaults to "json" unless specified with the pattern "array=custom_var_name".
        /// </summary>
        public string ArrayVariableName { get; init; } = "json";
    }
}
