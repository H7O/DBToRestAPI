using Com.H.Data.Common;
using DBToRestAPI.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DBToRestAPI.Settings.Extensinos
{
    public static class ParametersExt
    {

        #region name lists
        /// <summary>
        /// Splits a list of input names, as written in mandatory_parameters and in a cache's
        /// invalidators: on commas, or on `|` when the list contains one (so that a name can hold a
        /// comma), and always on line breaks. Spaces never separate names: a name may contain spaces
        /// (a JSON key such as "first name", read as {{first name}}).
        /// </summary>
        public static string[] SplitNames(string? list)
            => list?.Split([NameSeparator(list), '\n', '\r'],
                   StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

        /// <summary>
        /// The character that separates the names in a list: `|` when the list contains one, else a comma.
        /// </summary>
        public static char NameSeparator(string? list) => list?.Contains('|') == true ? '|' : ',';
        #endregion

        #region mandatory parameters
        // Split with SplitNames. Before 1.7.8 a space also separated names, so "first name"
        // required "first" and "name". OpenApiDocumentBuilder reads the list through this method too.
        public static string[]? GetMandatoryParameters(
            this IConfigurationSection serviceQuerySection)
        {
            var value = serviceQuerySection.GetSection("mandatory_parameters")?.Value;
            return value == null ? null : SplitNames(value);
        }
        public static ObjectResult? GetFailedMandatoryParamsCheckIfAny(
            this IConfigurationSection serviceQuerySection,
            List<DbQueryParams> qParams,
            string[]? mandatoryParameters = null
            )
        {
            if (mandatoryParameters == null || mandatoryParameters.Length < 1)
                return null;

            List<string> keys = new List<string>();
            foreach (var qParam in qParams)
            {
                IDictionary<string, object>? model = qParam.DataModel?.GetDataModelParameters();
                if (model == null) continue;

                keys = keys.Union(model.Keys).ToList();
            }

            var missingMandatoryParams = mandatoryParameters.Where(x => !(keys.Contains(x) == true)).ToArray();

            if (missingMandatoryParams.Length > 0)
                // return a response with status code 400 (similar to BadRequest)
                return new ObjectResult(new
                {
                    success = false,
                    // Joined with the list's own separator, so a name with a comma reads back whole.
                    message = $"Missing mandatory parameters: {string.Join(NameSeparator(serviceQuerySection.GetSection("mandatory_parameters")?.Value), missingMandatoryParams)}"
                })
                {
                    StatusCode = 400
                };

            //return BadRequest(new
            //    {
            //        success = false,
            //        message = $"Missing mandatory parameters: {string.Join(",", missingMandatoryParams)}"
            //    });

            return null;

        }

        #endregion






    }
}
