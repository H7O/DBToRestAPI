namespace DBToRestAPI.Services;

/// <summary>
/// The caller's request is invalid in a way the caller can fix: an uploaded file's name, size, count
/// or extension, the shape of the files metadata, or a body of a type the engine doesn't read.
/// <see cref="ParametersBuilder.GetParamsOrErrorAsync"/> turns it into a response with
/// <see cref="StatusCode"/> whose message is this exception's message, so only throw it with text that
/// is safe and useful to show the caller.
/// </summary>
public sealed class RequestValidationException(string message, int statusCode = 400) : ArgumentException(message)
{
    public int StatusCode { get; } = statusCode;
}
