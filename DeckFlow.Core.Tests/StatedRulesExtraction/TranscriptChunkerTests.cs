using DeckFlow.Core.Knowledge.StatedRulesExtraction;

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
        // Why (CR-03): only the first chunk is guaranteed to start with a marker — the transcript
        // itself starts with one. Later chunks correctly lead with the previous chunk's
        // chronologically-earlier overlap tail (no marker of its own), so only "contains a marker"
        // is a universal guarantee; "starts with a marker" is not.
        Assert.Matches("^\\[\\d{2}:\\d{2}\\]", chunks[0]);
        foreach (string chunk in chunks)
        {
            Assert.DoesNotMatch(@"\[\d{2}:\d{2}\]\s*$", chunk);
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
            Assert.Contains(overlap, chunks[index], StringComparison.Ordinal);

            // Why (CR-03): the overlap is the PREVIOUS chunk's chronologically-earlier tail, so it
            // must LEAD the new chunk — not just appear somewhere inside it — or the [mm:ss]
            // markers handed to the stated-rules extractor are out of chronological order.
            Assert.StartsWith(overlap, chunks[index], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Chunk_TranscriptExceedingChunkBudget_Throws()
    {
        var transcript = BuildTranscript(segmentCount: TranscriptChunker.MaxChunks + 25, sentencesPerSegment: 80);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => TranscriptChunker.Chunk(transcript));

        Assert.Contains($"{TranscriptChunker.MaxChunks}-chunk distillation budget", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Chunk_WordShortCharacterLongTranscript_SplitsWithinCharacterBudget()
    {
        IReadOnlyList<string> chunks = TranscriptChunker.Chunk(BuildLongSentences(prefix: string.Empty));

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.True(chunk.Length <= TranscriptChunker.MaxCharsPerChunk));
    }

    [Fact]
    public void Chunk_TranscriptWithoutTimestampMarkers_SplitsWithinCharacterBudget()
    {
        IReadOnlyList<string> chunks = TranscriptChunker.Chunk(BuildLongSentences(prefix: "Narrator "));

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.True(chunk.Length <= TranscriptChunker.MaxCharsPerChunk));
    }

    [Fact]
    public void Chunk_TranscriptWithOneTimestampMarker_SplitsWithinCharacterBudget()
    {
        IReadOnlyList<string> chunks = TranscriptChunker.Chunk(BuildLongSentences(prefix: "[00:00] Narrator "));

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.True(chunk.Length <= TranscriptChunker.MaxCharsPerChunk));
    }

    [Fact]
    public void Chunk_OversizedTimestampSegment_SplitsWithinCharacterBudget()
    {
        IReadOnlyList<string> chunks = TranscriptChunker.Chunk(BuildLongSentences(prefix: "[00:00] "));

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.True(chunk.Length <= TranscriptChunker.MaxCharsPerChunk));
        Assert.StartsWith("[00:00]", chunks[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Chunk_OverlapAndSegmentAtWordLimit_StaysWithinWordBudget()
    {
        var firstSegment = $"[00:00] {BuildWords(1_499)}. {BuildWords(1_499)}.";
        var secondSegment = $"[00:01] {BuildWords(1_499)}. {BuildWords(1_499)}.";

        IReadOnlyList<string> chunks = TranscriptChunker.Chunk($"{firstSegment} {secondSegment}");

        Assert.All(chunks, chunk => Assert.True(CountWords(chunk) <= TranscriptChunker.TargetWordsPerChunk));
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

    private static string BuildLongSentences(string prefix)
        => prefix + string.Join(" ", Enumerable.Repeat($"{new string('x', 900)}.", 20));

    private static string BuildWords(int count)
        => string.Join(" ", Enumerable.Repeat("x", count));

    private static int CountWords(string text)
        => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
