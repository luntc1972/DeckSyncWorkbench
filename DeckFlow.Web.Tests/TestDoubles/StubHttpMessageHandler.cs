using System.Net;
using System.Net.Http;

namespace DeckFlow.Web.Tests;

internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();
    public IList<HttpRequestMessage> RecordedRequests { get; } = new List<HttpRequestMessage>();
    public int CallCount => RecordedRequests.Count;
    public Exception? NextException { get; set; }

    public void Enqueue(HttpResponseMessage response) => _responses.Enqueue(response);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RecordedRequests.Add(request);

        if (NextException is not null)
        {
            var ex = NextException;
            NextException = null;
            throw ex;
        }

        var response = _responses.Count > 0
            ? _responses.Dequeue()
            : new HttpResponseMessage(HttpStatusCode.NotFound);

        return Task.FromResult(response);
    }
}
