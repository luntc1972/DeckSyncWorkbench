using DeckFlow.Core.Knowledge.StatedRulesExtraction;
using Xunit;

namespace DeckFlow.Core.Tests.StatedRulesExtraction;

public sealed class TranscriptChunkerTests
{
    [Fact]
    public void Chunk_ShortTranscript_ReturnsSingleChunkEqualToInput()
    {
        var transcript = "[00:00] Keep hands with at least two lands and one ramp piece.";

        IReadOnlyList<string> chunks = TranscriptChunker.Chunk(transcript);

        string only = Assert.Single(chunks);
        Assert.Equal(transcript, only);
    }

    [Fact]
    public void Chunk_LongTranscript_SplitsOnTimestampBoundaries()
    {
        var transcript = BuildTranscript(segmentCount: 40, sentencesPerSegment: 45);

        IReadOnlyList<string> chunks = TranscriptChunker.Chunk(transcript);

        Assert.True(chunks.Count > 1);
        foreach (string chunk in chunks)
        {
            Assert.Matches("^\\[\\d{2}:\\d{2}\\]", chunk);
            Assert.DoesNotContain("segment 20 sentence 10 [", chunk, StringComparison.Ordinal);
            Assert.True(TranscriptChunker.CountTimestampMarkers(chunk) >= 1);
        }
    }

    [Fact]
    public void Chunk_AdjacentChunksCarryTrailingSentenceOverlap()
    {
        var transcript = BuildTranscript(segmentCount: 36, sentencesPerSegment: 45);

        IReadOnlyList<string> chunks = TranscriptChunker.Chunk(transcript);

        Assert.True(chunks.Count > 1);
        for (int index = 1; index < chunks.Count; index++)
        {
            string overlap = TranscriptChunker.GetTrailingSentencesForTests(chunks[index - 1], TranscriptChunker.OverlapSentences);
            Assert.StartsWith(overlap, chunks[index], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Chunk_NeverExceedsMaxChunks()
    {
        var transcript = BuildTranscript(segmentCount: TranscriptChunker.MaxChunks + 25, sentencesPerSegment: 80);

        IReadOnlyList<string> chunks = TranscriptChunker.Chunk(transcript);

        Assert.Equal(TranscriptChunker.MaxChunks, chunks.Count);
    }

    private static string BuildTranscript(int segmentCount, int sentencesPerSegment)
    {
        var segments = new List<string>(segmentCount);
        for (int segment = 0; segment < segmentCount; segment++)
        {
            var minute = segment / 60;
            var second = segment % 60;
            var sentences = Enumerable.Range(1, sentencesPerSegment)
                .Select(sentence => $"Segment {segment} sentence {sentence} explains mulligan heuristics and sequencing decisions clearly.");
            segments.Add($"[{minute:00}:{second:00}] {string.Join(" ", sentences)}");
        }

        return string.Join(" ", segments);
    }
}
