using System.Text.Json;
using System.Text.Json.Serialization;

namespace Omega.Api.Contracts;

/// <summary>
/// JSON shape of every API response: camelCase names, enums as their names. Used by the host and by the contract
/// tests, so the samples the dashboard is tested against are exactly what the API sends.
/// </summary>
public static class ApiJson
{
    public static JsonSerializerOptions Options { get; } = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
