using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace DBToRestAPI.Tests;

/// <summary>
/// Answers the engine's outgoing HTTP calls in process. Once installed, every client the engine
/// takes from IHttpClientFactory (OIDC metadata, UserInfo, the gateway, {http{...}} calls, http file
/// downloads) sends its requests here instead of to the network. A URL without a mapped answer gets
/// a 404. Every request is recorded, with its body.
/// </summary>
public sealed class FakeHttpServer
{
    public sealed record Request(HttpMethod Method, Uri Uri, string? Authorization, string? Body)
    {
        /// <summary>Every request and content header sent, each name with its values joined by commas.</summary>
        public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    }

    private readonly ConcurrentDictionary<string, Func<Request, CancellationToken, Task<HttpResponseMessage>>> _answers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _requestAnswers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<Request> _requests = new();

    public IReadOnlyCollection<Request> Requests => _requests;

    /// <summary>
    /// Answers every request to <paramref name="url"/>: its scheme, host and path, with any query
    /// string or user name.
    /// </summary>
    public void Map(string url, Func<Request, HttpResponseMessage> answer)
        => _answers[Key(new Uri(url))] = (request, _) => Task.FromResult(answer(request));

    /// <summary>
    /// As <see cref="Map"/>, for an answer that takes its time: it gets the caller's token, so a
    /// caller that gives up cancels it.
    /// </summary>
    public void MapAsync(string url, Func<Request, CancellationToken, Task<HttpResponseMessage>> answer)
        => _answers[Key(new Uri(url))] = answer;

    /// <summary>
    /// As <see cref="MapAsync"/>, but the answer gets the request message itself, with its content
    /// not yet read: it reads the body when it chooses, as a real server does once its connection is
    /// up. The recorded request has no body.
    /// </summary>
    public void MapRequest(string url, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer)
        => _requestAnswers[Key(new Uri(url))] = answer;

    /// <summary>
    /// The number of requests made so far to <paramref name="url"/>, with any query string or user name.
    /// </summary>
    public int Count(string url) => _requests.Count(r => Key(r.Uri) == Key(new Uri(url)));

    public static HttpResponseMessage Text(string body, string mediaType = "text/plain", HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    public static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK)
        => Text(JsonSerializer.Serialize(body), "application/json", status);

    /// <summary>
    /// Sends every named and unnamed client's requests here. Registered after the engine's own
    /// client setup, so it replaces the handler each of them configures.
    /// </summary>
    public void Install(IServiceCollection services)
        => services.ConfigureAll<HttpClientFactoryOptions>(options =>
            options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = new Handler(this)));

    private static string Key(Uri uri) => uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);

    // A new handler for each one the factory builds, because it disposes the handlers it retires.
    private sealed class Handler(FakeHttpServer server) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken cancellationToken)
        {
            if (server._requestAnswers.TryGetValue(Key(message.RequestUri!), out var requestAnswer))
            {
                server._requests.Enqueue(new Request(message.Method, message.RequestUri!, message.Headers.Authorization?.ToString(), null));
                var answered = await requestAnswer(message, cancellationToken);
                answered.RequestMessage = message;
                return answered;
            }

            var request = new Request(
                message.Method,
                message.RequestUri!,
                message.Headers.Authorization?.ToString(),
                message.Content == null ? null : await message.Content.ReadAsStringAsync(cancellationToken))
            {
                Headers = message.Headers.Concat(message.Content?.Headers.AsEnumerable() ?? [])
                    .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase),
            };
            server._requests.Enqueue(request);

            var response = server._answers.TryGetValue(Key(request.Uri), out var answer)
                ? await answer(request, cancellationToken)
                : new HttpResponseMessage(HttpStatusCode.NotFound);
            response.RequestMessage = message;
            return response;
        }
    }
}
