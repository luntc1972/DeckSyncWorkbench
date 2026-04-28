using System.Net.Http;
using Microsoft.Extensions.Http;

namespace DeckFlow.Web.Tests;

internal sealed class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly Dictionary<string, HttpMessageHandler> _handlers;

    public FakeHttpClientFactory(Dictionary<string, HttpMessageHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        _handlers = handlers;
    }

    public HttpClient CreateClient(string name)
    {
        if (!_handlers.TryGetValue(name, out var handler))
        {
            throw new InvalidOperationException(
                $"FakeHttpClientFactory: no handler registered for client name '{name}'.");
        }

        return new HttpClient(handler, disposeHandler: false);
    }
}
