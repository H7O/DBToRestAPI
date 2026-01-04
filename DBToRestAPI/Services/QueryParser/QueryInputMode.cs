namespace DBToRestAPI.Services.QueryParser
{
    /// <summary>
    /// Specifies how the input from a previous query should be passed to the current query.
    /// </summary>
    public enum QueryInputMode
    {
        /// <summary>
        /// Maps columns from previous query by name if the previous query returned a single row,
        /// and as a JSON array if it returned multiple rows.
        /// </summary>
        Auto,

        /// <summary>
        /// Maps columns from previous query by name. Expects previous query to return a single row;
        /// otherwise, the system will take only the first row and omit the rest.
        /// </summary>
        Single,

        /// <summary>
        /// Maps the entire result of the previous query as a JSON array.
        /// </summary>
        Array
    }
}
