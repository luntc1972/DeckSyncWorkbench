using System.Text.RegularExpressions;

namespace DeckFlow.Core.Knowledge.StatedRulesExtraction;

/// <summary>
/// Transcript chunking helper for timestamp-aligned map-reduce distillation.
/// </summary>
public static partial class TranscriptChunker
{
    // Why: sentence overlap preserves complete nearby statements without cutting a sentence by character count.
    internal const int TargetWordsPerChunk = 3000;
    internal const int MaxCharsPerChunk = TargetWordsPerChunk * 4;
    internal const int OverlapSentences = 2;
    internal const int MaxChunks = (DistillationValidation.MaxTranscriptInputTokens * 4 + MaxCharsPerChunk - 1) / MaxCharsPerChunk;

    /// <summary>
    /// Splits a transcript into timestamp-aligned chunks with small sentence overlap between neighbors.
    /// </summary>
    public static IReadOnlyList<string> Chunk(string transcript)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transcript);
        DistillationValidation.ValidateTranscriptLength(transcript);

        if (CountWords(transcript) <= TargetWordsPerChunk && transcript.Length <= MaxCharsPerChunk)
        {
            return [transcript];
        }

        IReadOnlyList<string> segments = SplitIntoTimestampSegments(transcript);
        if (segments.Count <= 1)
        {
            return SplitIntoSentenceSegments(transcript);
        }

        segments = segments.SelectMany(SplitOversizedSegment).ToArray();

        var chunks = new List<string>(Math.Min(segments.Count, MaxChunks));
        var currentSegments = new List<string>();
        var currentLength = 0;
        var currentWords = 0;
        string? pendingOverlap = null;

        foreach (string segment in segments)
        {
            var segmentWords = CountWords(segment);
            var overlapWords = string.IsNullOrWhiteSpace(pendingOverlap) || currentSegments.Count > 0
                ? 0
                : CountWords(pendingOverlap);
            var projectedWords = currentWords + segmentWords + overlapWords;
            var projectedLength = currentLength + GetProjectedSegmentLength(currentSegments.Count, segment, pendingOverlap);
            var wouldExceedWords = projectedWords > TargetWordsPerChunk;
            var wouldExceedChars = projectedLength > MaxCharsPerChunk;

            if (currentSegments.Count > 0 && (wouldExceedWords || wouldExceedChars))
            {
                if (chunks.Count == MaxChunks - 1)
                {
                    throw new InvalidOperationException($"Transcript exceeds the {MaxChunks}-chunk distillation budget.");
                }

                string finalized = JoinSegments(currentSegments);
                chunks.Add(finalized);

                pendingOverlap = GetTrailingSentences(finalized, OverlapSentences);
                currentSegments.Clear();
                currentLength = 0;
                currentWords = 0;
            }

            if (!string.IsNullOrWhiteSpace(pendingOverlap) && currentSegments.Count == 0 &&
                (CountWords(pendingOverlap) + segmentWords > TargetWordsPerChunk ||
                 pendingOverlap.Length + segment.Length + 1 > MaxCharsPerChunk))
            {
                pendingOverlap = null;
            }

            AddSegment(currentSegments, segment, ref currentLength, ref currentWords, ref pendingOverlap);
        }

        if (currentSegments.Count > 0)
        {
            chunks.Add(JoinSegments(currentSegments));
        }

        return chunks;
    }

    internal static int CountTimestampMarkers(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TimestampMarkerRegex().Matches(text).Count;
    }

    internal static string GetTrailingSentencesForTests(string text, int sentenceCount)
    {
        ArgumentNullException.ThrowIfNull(text);
        return GetTrailingSentences(text, sentenceCount);
    }

    private static IReadOnlyList<string> SplitIntoTimestampSegments(string transcript)
    {
        MatchCollection matches = TimestampMarkerRegex().Matches(transcript);
        if (matches.Count == 0)
        {
            return [transcript];
        }

        var segments = new List<string>(matches.Count);
        for (var index = 0; index < matches.Count; index++)
        {
            var start = matches[index].Index;
            var end = index + 1 < matches.Count ? matches[index + 1].Index : transcript.Length;
            var segment = transcript[start..end].Trim();
            if (segment.Length > 0)
            {
                segments.Add(segment);
            }
        }

        return segments;
    }

    private static void AddSegment(
        List<string> currentSegments,
        string segment,
        ref int currentLength,
        ref int currentWords,
        ref string? pendingOverlap)
    {
        // Why (CR-03): the overlap is the PREVIOUS chunk's tail, so it must lead the new chunk to
        // keep the chunk's [mm:ss] markers in chronological order for the stated-rules extractor.
        if (!string.IsNullOrWhiteSpace(pendingOverlap))
        {
            currentSegments.Add(pendingOverlap);
            currentLength += pendingOverlap.Length + (currentSegments.Count == 1 ? 0 : 1);
            currentWords += CountWords(pendingOverlap);
            pendingOverlap = null;
        }

        currentSegments.Add(segment);
        currentLength += segment.Length + (currentSegments.Count == 1 ? 0 : 1);
        currentWords += CountWords(segment);
    }

    private static int GetProjectedSegmentLength(int currentSegmentCount, string segment, string? pendingOverlap)
    {
        var projected = segment.Length + (currentSegmentCount == 0 ? 0 : 1);
        if (currentSegmentCount == 0 && !string.IsNullOrWhiteSpace(pendingOverlap))
        {
            projected += pendingOverlap.Length + 1;
        }

        return projected;
    }

    // Why: chunk sizing is a reasoned starting point from research guidance rather than measured
    // production telemetry, and timestamp-aligned splits keep chunk boundaries on natural speech.
    private static string GetTrailingSentences(string text, int sentenceCount)
    {
        if (sentenceCount <= 0 || string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        MatchCollection matches = SentenceRegex().Matches(text);
        var take = Math.Min(sentenceCount, matches.Count);
        var start = matches[matches.Count - take].Index;
        return text[start..].Trim();
    }

    private static int CountWords(string text)
        => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static string JoinSegments(IReadOnlyList<string> segments)
        => string.Join(" ", segments.Where(segment => !string.IsNullOrWhiteSpace(segment)));

    private static IReadOnlyList<string> SplitOversizedSegment(string segment)
        => segment.Length <= MaxCharsPerChunk ? [segment] : SplitIntoSentenceSegments(segment);

    private static IReadOnlyList<string> SplitIntoSentenceSegments(string transcript)
    {
        var chunks = new List<string>();
        var current = new List<string>();
        var currentLength = 0;

        foreach (Match match in SentenceRegex().Matches(transcript))
        {
            string sentence = match.Value.Trim();
            if (current.Count > 0 && currentLength + sentence.Length + 1 > MaxCharsPerChunk)
            {
                chunks.Add(JoinSegments(current));
                current.Clear();
                currentLength = 0;
            }

            current.Add(sentence);
            currentLength += sentence.Length + (current.Count == 1 ? 0 : 1);
        }

        if (current.Count > 0)
        {
            chunks.Add(JoinSegments(current));
        }

        return chunks;
    }

    [GeneratedRegex(@"\[\d{2}:\d{2}\]", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampMarkerRegex();

    [GeneratedRegex(@"[^.!?]+[.!?]+|[^.!?]+$", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex SentenceRegex();
}
