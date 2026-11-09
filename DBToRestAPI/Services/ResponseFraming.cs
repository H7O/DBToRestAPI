using System.IO.Pipelines;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

namespace DBToRestAPI.Services;

/// <summary>
/// The bytes a route writes before and after its answer body, from its <c>response_prefix</c> and
/// <c>response_suffix</c> settings (or the global ones). Used for clients written against an older
/// service's exact output: for example a UTF-8 byte order mark (<c>EF BB BF</c>) that a legacy client
/// strips blindly, or a <c>)]}'</c> line some clients expect before JSON.
/// <para>
/// <see cref="Error"/> is set when a value can't be decoded; the route then answers 500 instead of
/// sending bytes its clients don't expect.
/// </para>
/// </summary>
public sealed class ResponseFraming
{
    public static readonly ResponseFraming None = new([], [], null);

    public ResponseFraming(byte[] prefix, byte[] suffix, string? error)
    {
        Prefix = prefix;
        Suffix = suffix;
        Error = error;
    }

    public byte[] Prefix { get; }
    public byte[] Suffix { get; }
    public string? Error { get; }

    /// <summary>True when the route adds nothing and has no error, so the response needs no wrapping.</summary>
    public bool IsNone => Error is null && Prefix.Length == 0 && Suffix.Length == 0;
}

/// <summary>
/// Reads every <c>response_prefix</c> / <c>response_suffix</c> in the configuration once, at start-up and on
/// each hot reload, into an immutable snapshot keyed by the route section's path. A request then costs one
/// field read when no route uses them (<see cref="InUse"/> is false), and one dictionary lookup when some do.
/// <code>
/// &lt;response_prefix encoding="hex"&gt;EF BB BF&lt;/response_prefix&gt;   &lt;!-- UTF-8 byte order mark --&gt;
/// &lt;response_suffix&gt;)&lt;/response_suffix&gt;
/// </code>
/// <c>encoding</c>: <c>text</c> (default; the text as UTF-8, XML character references allowed),
/// <c>hex</c> (byte pairs, whitespace ignored) or <c>base64</c>. A route's own tag replaces the global one;
/// an empty tag on a route turns the global one off for that route.
/// </summary>
public sealed class ResponseFramingIndex
{
    public const string PrefixKey = "response_prefix";
    public const string SuffixKey = "response_suffix";
    public const string EncodingKey = "encoding";

    private sealed record Snapshot(bool InUse, ResponseFraming Global, Dictionary<string, ResponseFraming> ByRoute);

    private static readonly Snapshot Empty =
        new(false, ResponseFraming.None, new Dictionary<string, ResponseFraming>(StringComparer.OrdinalIgnoreCase));

    private readonly IEncryptedConfiguration _configuration;
    private readonly ILogger<ResponseFramingIndex> _logger;
    private readonly Lock _reloadLock = new();
    private volatile Snapshot _snapshot = Empty;

    public ResponseFramingIndex(IEncryptedConfiguration configuration, ILogger<ResponseFramingIndex> logger)
    {
        _configuration = configuration;
        _logger = logger;
        // At start-up a value that can't be decoded stops the app: better than serving wrong bytes.
        var errors = Load();
        if (errors.Count > 0)
            throw new InvalidOperationException("Invalid response_prefix / response_suffix: " + string.Join("; ", errors));
        ChangeToken.OnChange(() => _configuration.GetReloadToken(), () =>
        {
            foreach (var error in Load())
                _logger.LogError("{Time}: {Message}. The route answers 500 until it's fixed.",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), LogText.Escape(error));
        });
    }

    /// <summary>False when no route and no global setting uses a prefix or suffix: the fast path.</summary>
    public bool InUse => _snapshot.InUse;

    /// <summary>The framing for a route section (its own, else the global one), or <see cref="ResponseFraming.None"/>.</summary>
    public ResponseFraming For(IConfigurationSection? route)
    {
        var snapshot = _snapshot;
        if (!snapshot.InUse) return ResponseFraming.None;
        if (route is not null && snapshot.ByRoute.TryGetValue(route.Path, out var framing)) return framing;
        return snapshot.Global;
    }

    private List<string> Load()
    {
        _reloadLock.Enter();
        try
        {
            var errors = new List<string>();
            var snapshot = Build(_configuration, errors);
            _snapshot = snapshot;
            return errors;
        }
        finally
        {
            _reloadLock.Exit();
        }
    }

    private static Snapshot Build(IConfiguration configuration, List<string> errors)
    {
        // every section that has one of the two tags (or only their encoding attribute)
        var owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, _) in configuration.AsEnumerable())
        {
            var owner = OwnerOf(key);
            if (owner is not null) owners.Add(owner);
        }
        if (owners.Count == 0) return Empty;

        var global = ResponseFraming.None;
        if (owners.Remove(string.Empty))
            global = Read(configuration, string.Empty, null, errors);

        var byRoute = new Dictionary<string, ResponseFraming>(StringComparer.OrdinalIgnoreCase);
        foreach (var owner in owners)
            byRoute[owner] = Read(configuration, owner, global, errors);

        return new Snapshot(true, global, byRoute);
    }

    /// <summary>The path of the section that owns this key when the key is one of the tags, else null.</summary>
    private static string? OwnerOf(string key)
    {
        foreach (var tag in new[] { PrefixKey, SuffixKey })
        {
            foreach (var tail in new[] { tag, tag + ":" + EncodingKey })
            {
                if (key.Equals(tail, StringComparison.OrdinalIgnoreCase)) return string.Empty;
                if (key.EndsWith(":" + tail, StringComparison.OrdinalIgnoreCase))
                    return key[..^(tail.Length + 1)];
            }
        }
        return null;
    }

    private static ResponseFraming Read(IConfiguration configuration, string owner, ResponseFraming? fallback, List<string> errors)
    {
        string? error = null;
        var ownPrefix = Decode(configuration, owner, PrefixKey, ref error);
        var ownSuffix = Decode(configuration, owner, SuffixKey, ref error);
        if (error is not null) errors.Add(error);
        // a tag the route doesn't have comes from the global setting, and so does that setting's error
        else if ((ownPrefix is null || ownSuffix is null) && fallback?.Error is not null) error = fallback.Error;
        return new ResponseFraming(ownPrefix ?? fallback?.Prefix ?? [], ownSuffix ?? fallback?.Suffix ?? [], error);
    }

    /// <summary>The tag's bytes, an empty array for an empty tag, or null when the section doesn't have the tag.</summary>
    private static byte[]? Decode(IConfiguration configuration, string owner, string tag, ref string? error)
    {
        var key = owner.Length == 0 ? tag : owner + ":" + tag;
        var value = configuration[key];
        var encoding = configuration[key + ":" + EncodingKey];
        if (value is null && encoding is null) return null;
        value ??= string.Empty;
        var where = (owner.Length == 0 ? "the global " : "route `" + owner + "`'s ") + tag;
        switch ((encoding ?? "text").Trim().ToLowerInvariant())
        {
            case "text":
                return System.Text.Encoding.UTF8.GetBytes(value);
            case "hex":
                var hex = string.Concat(value.Where(c => !char.IsWhiteSpace(c)));
                try
                {
                    return Convert.FromHexString(hex);
                }
                catch (FormatException)
                {
                    error ??= where + ": `" + value + "` isn't hex (byte pairs such as EF BB BF)";
                    return [];
                }
            case "base64":
                try
                {
                    return Convert.FromBase64String(string.Concat(value.Where(c => !char.IsWhiteSpace(c))));
                }
                catch (FormatException)
                {
                    error ??= where + ": the value isn't base64";
                    return [];
                }
            default:
                error ??= where + ": encoding `" + encoding + "` isn't text, hex or base64";
                return [];
        }
    }
}

/// <summary>
/// Wraps the response body feature of one request whose route has a prefix or suffix. The prefix goes out
/// just before the first byte of the body, the suffix after the last, so a response without a body
/// (HEAD, 204, 304) gets neither. A partial answer (206) and a compressed one (a <c>Content-Encoding</c>,
/// such as a gateway target's gzip) pass through unchanged: bytes added to either would corrupt it. Every
/// way of writing the body goes through it: <see cref="Stream"/>, <see cref="Writer"/> (System.Text.Json)
/// and <see cref="SendFileAsync"/> (files).
/// </summary>
public sealed class ResponseFramingBody : Stream, IHttpResponseBodyFeature
{
    private readonly IHttpResponseBodyFeature _inner;
    private readonly HttpResponse _response;
    private readonly byte[] _prefix;
    private readonly byte[] _suffix;
    private PipeWriter? _writer;
    private bool _started;   // the first body byte went out (with the prefix, unless the answer is partial)
    private bool _framed;    // the prefix was written, so the suffix is owed
    private bool _finished;

    public ResponseFramingBody(IHttpResponseBodyFeature inner, HttpResponse response, ResponseFraming framing)
    {
        _inner = inner;
        _response = response;
        _prefix = framing.Prefix;
        _suffix = framing.Suffix;
    }

    /// <summary>Adds the prefix and suffix to a Content-Length the handler set, before the headers go out.</summary>
    public void AdjustContentLength()
    {
        var length = _response.ContentLength;
        if (length is > 0 && Frames())
            _response.ContentLength = length + _prefix.Length + _suffix.Length;
    }

    /// <summary>
    /// Writes the suffix (if the prefix went out) once the handler is done, unless the connection was cut:
    /// an answer that failed part way must not end as if it were whole.
    /// </summary>
    public async Task FinishAsync()
    {
        if (_finished) return;
        _finished = true;
        if (_writer is not null) await _writer.FlushAsync();
        if (_framed && _suffix.Length > 0 && !_response.HttpContext.RequestAborted.IsCancellationRequested)
            await _inner.Stream.WriteAsync(_suffix);
    }

    /// <summary>False for an answer bytes can't be added to: a partial one (206), or a compressed one.</summary>
    private bool Frames()
        => _response.StatusCode != StatusCodes.Status206PartialContent
           && StringValues.IsNullOrEmpty(_response.Headers.ContentEncoding);

    /// <summary>Called before the first body byte: true when the prefix must go out first.</summary>
    private bool StartBody()
    {
        _started = true;
        _framed = Frames();
        return _framed && _prefix.Length > 0;
    }

    // ── IHttpResponseBodyFeature ──────────────────────────────────────────────

    Stream IHttpResponseBodyFeature.Stream => this;

    public PipeWriter Writer => _writer ??= PipeWriter.Create(this, new StreamPipeWriterOptions(leaveOpen: true));

    public void DisableBuffering() => _inner.DisableBuffering();

    public Task StartAsync(CancellationToken cancellationToken = default) => _inner.StartAsync(cancellationToken);

    // through this stream, so the prefix comes first (framed routes only; others keep the server's send-file)
    public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default)
        => SendFileFallback.SendFileAsync(this, path, offset, count, cancellationToken);

    public async Task CompleteAsync()
    {
        await FinishAsync();
        await _inner.CompleteAsync();
    }

    // ── Stream ────────────────────────────────────────────────────────────────

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Flush() => _inner.Stream.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.Stream.FlushAsync(cancellationToken);

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (count == 0) return;
        if (!_started && StartBody()) _inner.Stream.Write(_prefix);
        _inner.Stream.Write(buffer, offset, count);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty) return;
        if (!_started && StartBody()) await _inner.Stream.WriteAsync(_prefix, cancellationToken);
        await _inner.Stream.WriteAsync(buffer, cancellationToken);
    }
}
