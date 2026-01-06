namespace DBToRestAPI.Services.QueryParser
{
    /// <summary>
    /// Represents a single query definition parsed from configuration.
    /// </summary>
    public class QueryDefinition
    {
        /// <summary>
        /// The zero-based index of this query in the chain.
        /// Useful for debugging and error messages.
        /// </summary>
        public int Index { get; init; }

        /// <summary>
        /// Indicates whether this is the last query in the chain.
        /// Only the last query's result is returned to the client.
        /// </summary>
        public bool IsLastInChain { get; init; }

        /// <summary>
        /// Indicates whether this is the first query in the chain.
        /// The first query receives parameters from the HTTP request.
        /// </summary>
        public bool IsFirstInChain => Index == 0;

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
        /// Only applicable when the previous query returns multiple rows.
        /// Defaults to "json" unless specified via the "json_var" attribute.
        /// </summary>
        public string JsonVariableName { get; init; } = "json";
    }
}
