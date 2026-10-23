using System.Dynamic;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DBToRestAPI.Services;

namespace DBToRestAPI.Tests;

/// <summary>
/// ExpandoJsonConverter writes an ExpandoObject byte for byte as the serializer writes it without
/// the converter, for every type a database driver returns, whatever the options.
/// </summary>
public class ExpandoJsonConverterTests
{
    // The options MVC writes a response with (its output formatter takes the relaxed encoder when
    // none is set), the serializer's defaults, which the engine writes a chained query's rows and a
    // cached result with, and the web defaults with the default encoder.
    public static TheoryData<string> OptionSets => new() { "mvc", "default", "web" };

    private static JsonSerializerOptions Plain(string set) => set switch
    {
        "mvc" => new(JsonSerializerDefaults.Web) { MaxDepth = 32, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping },
        "default" => new(),
        _ => new(JsonSerializerDefaults.Web),
    };

    private static JsonSerializerOptions WithConverter(JsonSerializerOptions options)
    {
        var copy = new JsonSerializerOptions(options);
        copy.Converters.Add(new ExpandoJsonConverter());
        return copy;
    }

    private static ExpandoObject Row(params (string Name, object? Value)[] columns)
    {
        var row = new ExpandoObject();
        foreach (var (name, value) in columns)
            ((IDictionary<string, object?>)row).Add(name, value);
        return row;
    }

    private static (string Name, object? Value)[] EveryType() =>
    [
        ("null", null),
        ("empty string", ""),
        ("string to escape", "é \"quoted\" <b> & ' \\ / 😀 \u2028 \t \u0001 +"),
        ("int", int.MinValue),
        ("long", long.MaxValue),
        ("beyond a double", 9007199254740993L),
        ("short", (short)-7),
        ("byte", (byte)255),
        ("decimal with its scale", 1.50m),
        ("money's scale", 1.5000m),
        ("small decimal", -0.0001m),
        ("largest decimal", decimal.MaxValue),
        ("double", 2.5),
        ("double fraction", 0.1),
        ("negative zero", -0.0),
        ("large double", 1e300),
        ("smallest double", double.Epsilon),
        ("float", 0.1f),
        ("true", true),
        ("false", false),
        ("DateTime", new DateTime(2026, 1, 15, 10, 20, 30, 123)),
        ("DateTime UTC", new DateTime(2026, 1, 15, 10, 20, 30, DateTimeKind.Utc)),
        ("DateTime local", new DateTime(2026, 1, 15, 10, 20, 30, DateTimeKind.Local)),
        ("DateTime ticks", new DateTime(2026, 1, 15, 10, 20, 30).AddTicks(1234567)),
        ("midnight", new DateTime(2026, 1, 15)),
        ("earliest DateTime", DateTime.MinValue),
        ("DateTimeOffset", new DateTimeOffset(2026, 1, 15, 10, 20, 30, TimeSpan.FromHours(4))),
        ("TimeSpan", new TimeSpan(12, 30, 5)),
        ("DateOnly", new DateOnly(2026, 1, 15)),
        ("TimeOnly", new TimeOnly(12, 30, 5)),
        ("Guid", Guid.Parse("6F9619FF-8B86-D011-B42D-00C04FC964FF")),
        ("bytes", new byte[] { 1, 2, 255 }),
        ("no bytes", Array.Empty<byte>()),
        ("char", 'x'),
        ("nested row", Row(("a", 1.50m), ("b", null), ("é", "<c>"))),
        ("list", new List<object?> { 1m, "a", null, true, Row(("c", "d")) }),
        ("JsonElement", JsonDocument.Parse("""{"x":[1,2.50,"yé"]}""").RootElement),
        // Column names the writer escapes
        ("é", 1),
        ("a\"b", 2),
        ("<tag>", 3),
        ("", 4),
    ];

    [Theory]
    [MemberData(nameof(OptionSets))]
    public void EveryType_IsWrittenAsTheSerializerWritesIt(string set)
    {
        var plain = Plain(set);
        var converted = WithConverter(plain);

        // Each column alone, so a difference names its type, then the whole row.
        foreach (var column in EveryType())
            Assert.Equal(JsonSerializer.Serialize(Row(column), plain), JsonSerializer.Serialize(Row(column), converted));
        var row = Row(EveryType());
        Assert.Equal(JsonSerializer.Serialize(row, plain), JsonSerializer.Serialize(row, converted));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NaNAndTheInfinities_AreHandledAsTheSerializerHandlesThem(double value)
    {
        var row = Row(("x", value));
        var plain = Plain("mvc");

        var refused = Record.Exception(() => JsonSerializer.Serialize(row, plain));
        Assert.NotNull(refused);
        Assert.IsType(refused.GetType(), Record.Exception(() => JsonSerializer.Serialize(row, WithConverter(plain))));

        // Options that allow them write them the same way.
        var allowing = new JsonSerializerOptions(plain) { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
        Assert.Equal(JsonSerializer.Serialize(row, allowing), JsonSerializer.Serialize(row, WithConverter(allowing)));
    }

    // The engine streams a result: rows (and a bare value, for one column without a name) written
    // as they are read, through the serializer's resumable path, flushed as its buffer fills.
    [Theory]
    [InlineData(3)]
    [InlineData(5000)]
    public async Task AStreamedResult_IsWrittenAsBefore(int rows)
    {
        async IAsyncEnumerable<object?> Result()
        {
            for (var i = 0; i < rows; i++)
            {
                await Task.Yield();
                yield return i % 3 == 2 ? (i % 2 == 0 ? 5L : null) : Row(EveryType());
            }
        }

        async Task<string> WriteAsync(JsonSerializerOptions options)
        {
            using var stream = new MemoryStream();
            await JsonSerializer.SerializeAsync(stream, Result(), options);
            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }

        var plain = Plain("mvc");
        Assert.Equal(await WriteAsync(plain), await WriteAsync(WithConverter(plain)));
    }

    [Fact]
    public void ReadingARow_IsTheSerializersOwn()
    {
        const string json = """{"a":1,"b":[true,null],"c":{"d":"e"},"f":"g"}""";
        var plain = Plain("mvc");

        var read = JsonSerializer.Deserialize<ExpandoObject>(json, WithConverter(plain))!;

        Assert.All((IDictionary<string, object?>)read, column => Assert.IsType<JsonElement>(column.Value));
        Assert.Equal(
            JsonSerializer.Serialize(JsonSerializer.Deserialize<ExpandoObject>(json, plain), plain),
            JsonSerializer.Serialize(read, plain));
    }

    [Fact]
    public void TheEngineOptions_AreTheDefaultsWithTheConverter()
    {
        Assert.Single(ExpandoJsonConverter.Options.Converters.OfType<ExpandoJsonConverter>());
        var row = Row(EveryType());
        Assert.Equal(JsonSerializer.Serialize(row), JsonSerializer.Serialize(row, ExpandoJsonConverter.Options));
    }
}
