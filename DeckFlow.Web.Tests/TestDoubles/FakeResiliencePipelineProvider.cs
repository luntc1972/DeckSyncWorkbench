using Polly;
using Polly.Registry;

namespace DeckFlow.Web.Tests;

internal sealed class FakeResiliencePipelineProvider : ResiliencePipelineProvider<string>
{
    public override ResiliencePipeline<T> GetPipeline<T>(string key) =>
        ResiliencePipeline<T>.Empty;

    public override bool TryGetPipeline<T>(string key, out ResiliencePipeline<T> pipeline)
    {
        pipeline = ResiliencePipeline<T>.Empty;
        return true;
    }

    public override bool TryGetPipeline(string key, out ResiliencePipeline pipeline)
    {
        pipeline = ResiliencePipeline.Empty;
        return true;
    }
}
