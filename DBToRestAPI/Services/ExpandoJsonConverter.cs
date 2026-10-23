using System.Dynamic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DBToRestAPI.Services;

/// <summary>
/// Writes an <see cref="ExpandoObject"/> exactly as System.Text.Json writes it on its own, in less
/// time: each object of a JSON column (<c>{type{json{name}}}</c>), and the object a route's
/// <c>root_node</c> wraps its answer in. A query's rows are Com.H.Data.Common's DbQueryRows, which
/// that library writes the same way with a converter of its own. On its own, the serializer writes
/// an ExpandoObject as a dictionary of <see cref="object"/>: it looks up each value's converter from
/// the value's type, and takes its slower resumable path while a response is streamed. This
/// converter writes the values databases return most itself, with the writer methods the
/// serializer's own converters call, and hands every other value to the serializer.
/// </summary>
public sealed class ExpandoJsonConverter : JsonConverter<ExpandoObject>
{
    /// <summary>
    /// The serializer's default options with this converter, for results the engine serializes
    /// itself: a chained query's rows and a cached response.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new() { Converters = { new ExpandoJsonConverter() } };

    // The engine reads no ExpandoObject from JSON. Reading one is the serializer's own, with its
    // defaults: an object's values are JsonElements whatever the options.
    public override ExpandoObject? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => JsonSerializer.Deserialize<ExpandoObject>(ref reader, JsonSerializerOptions.Default);

    public override void Write(Utf8JsonWriter writer, ExpandoObject expando, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (name, value) in expando)
        {
            writer.WritePropertyName(name);
            switch (value)
            {
                case null: writer.WriteNullValue(); break;
                case string text: writer.WriteStringValue(text); break;
                case int number: writer.WriteNumberValue(number); break;
                case long number: writer.WriteNumberValue(number); break;
                case decimal number: writer.WriteNumberValue(number); break;
                // NaN and the infinities go to the serializer, which refuses them unless the
                // options allow them.
                case double number when double.IsFinite(number): writer.WriteNumberValue(number); break;
                case bool flag: writer.WriteBooleanValue(flag); break;
                case DateTime time: writer.WriteStringValue(time); break;
                case Guid id: writer.WriteStringValue(id); break;
                default: JsonSerializer.Serialize(writer, value, value.GetType(), options); break;
            }
        }
        writer.WriteEndObject();
    }
}
