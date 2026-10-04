namespace DBToRestAPI.Services;

/// <summary>
/// The caller's request is invalid in a way the caller can fix: an uploaded file's name, size, count
/// or extension, or the shape of the files metadata. <see cref="ParametersBuilder.GetParamsOrErrorAsync"/>
/// turns it into a 400 whose message is this exception's message, so only throw it with text that is
/// safe and useful to show the caller.
/// </summary>
public sealed class RequestValidationException(string message) : ArgumentException(message);
