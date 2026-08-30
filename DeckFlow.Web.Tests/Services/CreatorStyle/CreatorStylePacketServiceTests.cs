using DeckFlow.Core.Content;
using DeckFlow.Core.Knowledge;
using DeckFlow.Core.Knowledge.CardGrounding;
using DeckFlow.Core.Knowledge.CreatorStyleRubric;
using DeckFlow.Core.Models;
using DeckFlow.Core.Normalization;
using DeckFlow.Web.Models;
using DeckFlow.Web.Services;
using DeckFlow.Web.Services.CreatorStyle;
using DeckFlow.Web.Services.FeatureFlags;
using System.Globalization;
using Xunit;

namespace DeckFlow.Web.Tests.Services.CreatorStyle;

public sealed class CreatorStylePacketServiceTests
{
    private const string CardsWithheldNotice = "Some cards couldn't be validated and were left out of this packet. The critique below still reflects your deck's core build — just with a smaller card pool than usual.";
    private const string UpstreamOutageNotice = "Card validation is temporarily unavailable, so this packet uses a reduced card pool. Try again in a few minutes for the full picture.";

    [Fact]
    public async Task BuildAsync_ProfileExists_ReturnsRubricScoresAndAcceptedCollections()
    {
        var request = new CreatorStyleRequest
        {
            CreatorSlug = "alpha",
            DeckText = "1 Arcane Signet",
        };

        SubmittedDeckAnalysis analysis = CreateAnalysis(
            deckSize: 99,
            entries:
            [
                DeckEntry("Commander One", 1, "commander"),
                DeckEntry("Arcane Signet", 1, "mainboard"),
            ],
            includedComboCardNames: ["Dockside Extortionist"]);
        var expectedRubric = new RubricScoreResult
        {
            CreatorSlug = "alpha",
            MetricScores =
            [
                new RubricMetricScore
                {
                    Metric = "category_ratio:ramp",
                    TargetValue = 12.5,
                    SubmittedValue = 10.5,
                    Delta = -2,
                    Weight = 0.8,
                    Verdict = "under",
                    Confidence = "high",
                },
            ],
        };

        CreatorStylePacketService sut = CreateSut(
            analysis: analysis,
            whitelistResult: new CreatorWhitelistPoolBuildResult
            {
                AcceptedNames = ["Sol Ring"],
                HasUpstreamFailure = false,
            },
            validateAdditionalCardsAsync: (_, _, _) => Task.FromResult(new CardGroundingBatchResult
            {
                Verdicts =
                [
                    Accepted("Arcane Signet"),
                    Accepted("Commander One"),
                    Accepted("Dockside Extortionist"),
                ],
                HasUpstreamFailure = false,
            }),
            creatorDecks:
            [
                CreatorDeck("deck-1", "trusted-folder", "high", "Commander One", "Arcane Signet"),
            ],
            scoreRubric: expectedRubric);

        CreatorStylePacketResult result = await sut.BuildAsync(request);

        Assert.Same(expectedRubric, result.RubricScores);
        Assert.Equal(["Sol Ring"], result.ValidatedWhitelist);
        Assert.Equal(["Dockside Extortionist"], result.ValidatedComboCards);
        Assert.Equal(
            ["Arcane Signet", "Commander One"],
            Assert.Single(result.Exemplars).CardNames.OrderBy(static cardName => cardName, StringComparer.Ordinal).ToArray());
        Assert.False(result.GroundingDegraded);
    }

    [Fact]
    public async Task BuildAsync_AdditionalGroundingRejectsCards_ExcludesThemAndSetsGroundingDegraded()
    {
        var request = new CreatorStyleRequest
        {
            CreatorSlug = "alpha",
            DeckText = "1 Arcane Signet",
        };

        SubmittedDeckAnalysis analysis = CreateAnalysis(
            deckSize: 99,
            entries:
            [
                DeckEntry("Commander One", 1, "commander"),
                DeckEntry("Arcane Signet", 1, "mainboard"),
            ],
            includedComboCardNames: ["Jeska's Will"]);

        CreatorStylePacketService sut = CreateSut(
            analysis: analysis,
            whitelistResult: new CreatorWhitelistPoolBuildResult
            {
                AcceptedNames = ["Sol Ring"],
                HasUpstreamFailure = false,
            },
            validateAdditionalCardsAsync: (_, _, _) => Task.FromResult(new CardGroundingBatchResult
            {
                Verdicts =
                [
                    Accepted("Arcane Signet"),
                    Accepted("Commander One"),
                    Rejected("Hullbreacher", CardGroundingRejectReason.NotLegal),
                    Rejected("Jeska's Will", CardGroundingRejectReason.UpstreamUnavailable),
                ],
                HasUpstreamFailure = true,
            }),
            creatorDecks:
            [
                CreatorDeck("deck-1", "trusted-folder", "high", "Commander One", "Arcane Signet", "Hullbreacher"),
            ]);

        CreatorStylePacketResult result = await sut.BuildAsync(request);

        Assert.True(result.GroundingDegraded);
        Assert.DoesNotContain("Hullbreacher", result.Exemplars.SelectMany(exemplar => exemplar.CardNames));
        Assert.DoesNotContain("Jeska's Will", result.ValidatedComboCards);
        Assert.DoesNotContain("Hullbreacher", result.ArtifactText);
        Assert.DoesNotContain("Jeska's Will", result.ArtifactText);
    }

    [Fact]
    public async Task BuildAsync_UsesOneDistinctValidationBatchMinusWhitelist()
    {
        var request = new CreatorStyleRequest
        {
            CreatorSlug = "alpha",
            DeckText = "1 Arcane Signet",
        };

        SubmittedDeckAnalysis analysis = CreateAnalysis(
            deckSize: 100,
            entries:
            [
                DeckEntry("Commander One", 1, "commander"),
                DeckEntry("Arcane Signet", 1, "mainboard"),
            ],
            includedComboCardNames: ["Smothering Tithe", "Dockside Extortionist"]);
        List<IReadOnlyList<string>> validationBatches = [];

        CreatorStylePacketService sut = CreateSut(
            analysis: analysis,
            whitelistResult: new CreatorWhitelistPoolBuildResult
            {
                AcceptedNames = ["Sol Ring", "Arcane Signet", "Smothering Tithe"],
                HasUpstreamFailure = false,
            },
            validateAdditionalCardsAsync: (candidateNames, _, _) =>
            {
                validationBatches.Add(candidateNames.ToArray());
                return Task.FromResult(new CardGroundingBatchResult
                {
                    Verdicts = candidateNames.Select(Accepted).ToArray(),
                    HasUpstreamFailure = false,
                });
            },
            creatorDecks:
            [
                CreatorDeck("deck-1", "trusted-folder", "high", "Commander One", "Arcane Signet", "Sol Ring", "Smothering Tithe"),
            ]);

        CreatorStylePacketResult result = await sut.BuildAsync(request);

        IReadOnlyList<string> batch = Assert.Single(validationBatches);
        Assert.Equal(["Commander One", "Dockside Extortionist"], batch);
        Assert.DoesNotContain("Arcane Signet", batch);
        Assert.DoesNotContain("Sol Ring", batch);
        Assert.DoesNotContain("Smothering Tithe", batch);
        Assert.Equal(["Sol Ring", "Arcane Signet", "Smothering Tithe"], result.ValidatedWhitelist);
        Assert.Equal(["Smothering Tithe", "Dockside Extortionist"], result.ValidatedComboCards);
    }

    [Fact]
    public async Task BuildAsync_WhitelistDiagnosticsHasUpstreamFailure_SetsGroundingDegraded()
    {
        var request = new CreatorStyleRequest
        {
            CreatorSlug = "alpha",
            DeckText = "1 Arcane Signet",
        };

        SubmittedDeckAnalysis analysis = CreateAnalysis(
            deckSize: 99,
            entries:
            [
                DeckEntry("Commander One", 1, "commander"),
                DeckEntry("Arcane Signet", 1, "mainboard"),
            ]);

        CreatorStylePacketService sut = CreateSut(
            analysis: analysis,
            whitelistResult: new CreatorWhitelistPoolBuildResult
            {
                AcceptedNames = ["Sol Ring"],
                HasUpstreamFailure = true,
            },
            validateAdditionalCardsAsync: (_, _, _) => Task.FromResult(new CardGroundingBatchResult
            {
                Verdicts =
                [
                    Accepted("Arcane Signet"),
                    Accepted("Commander One"),
                ],
                HasUpstreamFailure = false,
            }),
            creatorDecks:
            [
                CreatorDeck("deck-1", "trusted-folder", "high", "Commander One", "Arcane Signet"),
            ]);

        CreatorStylePacketResult result = await sut.BuildAsync(request);

        Assert.True(result.GroundingDegraded);
        Assert.Equal(UpstreamOutageNotice, result.Notice);
    }

    [Fact]
    public async Task TryComputeCacheKeyAsync_FlagOn_ReturnsNull()
    {
        var request = CreateCacheRequest();
        CreatorStylePacketService sut = CreateSut(flagCache: new FakeFeatureFlagCache(new Dictionary<string, bool>
        {
            [CreatorStylePacketService.CreatorStyleToolEnabledFlag] = true,
        }));

        string? key = await sut.TryComputeCacheKeyAsync(request, CancellationToken.None);

        Assert.Null(key);
    }

    [Fact]
    public async Task TryComputeCacheKeyAsync_FlagOff_Returns64CharacterKey()
    {
        var request = CreateCacheRequest();
        CreatorStylePacketService sut = CreateSut(flagCache: new FakeFeatureFlagCache(new Dictionary<string, bool>
        {
            [CreatorStylePacketService.CreatorStyleToolEnabledFlag] = false,
        }));

        string? key = await sut.TryComputeCacheKeyAsync(request, CancellationToken.None);

        Assert.NotNull(key);
        Assert.Equal(64, key!.Length);
    }

    [Fact]
    public async Task BuildAsync_FlagFlipsOffMidRequest_WriteSideSkipsCacheBasedOnLatchedBypass()
    {
        var packetCache = new PacketSessionCache();
        var request = CreateCacheRequest();
        CreatorStylePacketService sut = CreateSut(
            packetCache: packetCache,
            flagCache: new FlipAfterNSnapshotsFeatureFlagCache(CreatorStylePacketService.CreatorStyleToolEnabledFlag, trueCallCount: 1),
            analysis: CreateAnalysis(
                deckSize: 99,
                entries:
                [
                    DeckEntry("Commander One", 1, "commander"),
                    DeckEntry("Arcane Signet", 1, "mainboard"),
                ]),
            creatorDecks:
            [
                CreatorDeck("deck-1", "trusted-folder", "ok", "Commander One", "Arcane Signet"),
            ]);

        CreatorStylePacketResult result = await sut.BuildAsync(request, CancellationToken.None);

        Assert.NotEmpty(result.ArtifactText);

        CreatorStylePacketService probe = CreateSut(
            packetCache: packetCache,
            flagCache: new FakeFeatureFlagCache(new Dictionary<string, bool>
            {
                [CreatorStylePacketService.CreatorStyleToolEnabledFlag] = false,
            }));
        string? cacheKey = await probe.TryComputeCacheKeyAsync(request, CancellationToken.None);
        Assert.NotNull(cacheKey);
        Assert.False(packetCache.TryGet<CreatorStylePacketResult>(cacheKey!, out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BuildAsync_MissingOrInsufficientProfile_ReturnsDegradedEmptyResult(bool insufficientSample)
    {
        var request = new CreatorStyleRequest
        {
            CreatorSlug = "alpha",
            DeckText = "1 Arcane Signet",
        };

        CreatorStylePacketService sut = CreateSut(
            getProfileAsync: (_, _) => Task.FromResult<CreatorStyleProfile?>(insufficientSample ? CreateProfile("alpha", insufficientSample: true) : null),
            analysis: CreateAnalysis(
                deckSize: 99,
                entries:
                [
                    DeckEntry("Commander One", 1, "commander"),
                ]),
            buildWhitelistAsync: (_, _, _) => throw new Xunit.Sdk.XunitException("Whitelist should not run."),
            validateAdditionalCardsAsync: (_, _, _) => throw new Xunit.Sdk.XunitException("Guard should not run."),
            getCreatorDecksAsync: (_, _) => throw new Xunit.Sdk.XunitException("Deck cache should not run."),
            scoreRubricFunc: (_, _, _) => throw new Xunit.Sdk.XunitException("Rubric should not run."));

        CreatorStylePacketResult result = await sut.BuildAsync(request);

        Assert.False(result.GroundingDegraded);
        Assert.True(result.ProfileUnavailable);
        Assert.Empty(result.Exemplars);
        Assert.Empty(result.ValidatedWhitelist);
        Assert.Empty(result.ValidatedComboCards);
        Assert.Empty(result.ArtifactText);
        Assert.NotNull(result.Notice);
    }

    [Fact]
    public async Task BuildAsync_AssemblesArtifactTextWithFiveSectionsAndAcceptedCardsOnly()
    {
        var request = new CreatorStyleRequest
        {
            CreatorSlug = "alpha\ncreator",
            DeckText = "1 Arcane Signet",
        };

        SubmittedDeckAnalysis analysis = CreateAnalysis(
            deckSize: 99,
            entries:
            [
                DeckEntry("Commander One", 1, "commander"),
                DeckEntry("Arcane Signet", 1, "mainboard"),
            ],
            includedComboCardNames: ["Dockside Extortionist", "Hullbreacher"]);
        RubricScoreResult rubric = new()
        {
            CreatorSlug = "alpha",
            MetricScores =
            [
                new RubricMetricScore
                {
                    Metric = "category_ratio:ramp",
                    TargetValue = 12.5,
                    SubmittedValue = 10.5,
                    Delta = -2,
                    Weight = 0.75,
                    Verdict = "under",
                    Confidence = "high",
                },
            ],
        };

        CreatorStylePacketService sut = CreateSut(
            analysis: analysis,
            whitelistResult: new CreatorWhitelistPoolBuildResult
            {
                AcceptedNames = ["Sol Ring"],
                HasUpstreamFailure = false,
            },
            validateAdditionalCardsAsync: (candidateNames, _, _) => Task.FromResult(new CardGroundingBatchResult
            {
                Verdicts = candidateNames.Select(candidateName => candidateName switch
                {
                    "Hullbreacher" => Rejected("Hullbreacher", CardGroundingRejectReason.NotLegal),
                    _ => Accepted(candidateName),
                }).ToArray(),
                HasUpstreamFailure = false,
            }),
            creatorDecks:
            [
                CreatorDeck("deck-1", "trusted-folder", "high", "Commander One", "Arcane Signet", "Hullbreacher"),
            ],
            scoreRubric: rubric);

        CreatorStylePacketResult result = await sut.BuildAsync(request);

        Assert.Contains("Creator Targets", result.ArtifactText, StringComparison.Ordinal);
        Assert.Contains("Exemplar Decklists", result.ArtifactText, StringComparison.Ordinal);
        Assert.Contains("Validated Synergy Context", result.ArtifactText, StringComparison.Ordinal);
        Assert.Contains("Rubric Scores", result.ArtifactText, StringComparison.Ordinal);
        Assert.Contains("Critique this deck ONLY using the cards provided above.", result.ArtifactText, StringComparison.Ordinal);
        Assert.Contains("alpha creator", result.ArtifactText, StringComparison.Ordinal);
        Assert.Contains("Arcane Signet", result.ArtifactText, StringComparison.Ordinal);
        Assert.Contains("Dockside Extortionist", result.ArtifactText, StringComparison.Ordinal);
        Assert.Contains("Sol Ring", result.ArtifactText, StringComparison.Ordinal);
        Assert.DoesNotContain("Hullbreacher", result.ArtifactText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_GroundingDegraded_ArtifactTextIncludesVisibleCaveatAndCapsUserText()
    {
        string longSlug = "creator\nsecond-line-" + new string('x', 260);
        var request = new CreatorStyleRequest
        {
            CreatorSlug = longSlug,
            DeckText = "1 Arcane Signet",
        };

        CreatorStylePacketService sut = CreateSut(
            whitelistResult: new CreatorWhitelistPoolBuildResult
            {
                AcceptedNames = ["Sol Ring"],
                HasUpstreamFailure = true,
            },
            validateAdditionalCardsAsync: (_, _, _) => Task.FromResult(new CardGroundingBatchResult
            {
                Verdicts = [Accepted("Commander One")],
                HasUpstreamFailure = false,
            }),
            creatorDecks:
            [
                CreatorDeck("deck-1", "trusted-folder", "high", "Commander One"),
            ]);

        CreatorStylePacketResult result = await sut.BuildAsync(request);

        Assert.True(result.GroundingDegraded);
        Assert.Contains("Grounding caveat", result.ArtifactText, StringComparison.Ordinal);
        Assert.Contains(UpstreamOutageNotice, result.ArtifactText, StringComparison.Ordinal);
        Assert.DoesNotContain("creator\nsecond-line", result.ArtifactText, StringComparison.Ordinal);
        Assert.DoesNotContain(longSlug, result.ArtifactText, StringComparison.Ordinal);
        Assert.Contains("second-line", result.ArtifactText, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 260), result.ArtifactText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_DeDeCulture_ArtifactTextRemainsByteIdentical()
    {
        var request = new CreatorStyleRequest
        {
            CreatorSlug = "alpha",
            DeckText = "1 Arcane Signet",
        };

        CreatorStylePacketService sut = CreateSut(
            analysis: CreateAnalysis(
                deckSize: 99,
                entries:
                [
                    DeckEntry("Commander One", 1, "commander"),
                    DeckEntry("Arcane Signet", 1, "mainboard"),
                ]),
            whitelistResult: new CreatorWhitelistPoolBuildResult
            {
                AcceptedNames = ["Sol Ring"],
                HasUpstreamFailure = false,
            },
            validateAdditionalCardsAsync: (_, _, _) => Task.FromResult(new CardGroundingBatchResult
            {
                Verdicts =
                [
                    Accepted("Commander One"),
                    Accepted("Arcane Signet"),
                ],
                HasUpstreamFailure = false,
            }),
            creatorDecks:
            [
                CreatorDeck("deck-1", "trusted-folder", "high", "Commander One", "Arcane Signet"),
            ],
            scoreRubric: new RubricScoreResult
            {
                CreatorSlug = "alpha",
                MetricScores =
                [
                    new RubricMetricScore
                    {
                        Metric = "category_ratio:ramp",
                        TargetValue = 12.5,
                        SubmittedValue = 10.5,
                        Delta = -2,
                        Weight = 0.75,
                        Verdict = "under",
                        Confidence = "high",
                    },
                ],
            });

        string invariantArtifact = await WithCultureAsync(CultureInfo.InvariantCulture, async () => (await sut.BuildAsync(request).ConfigureAwait(false)).ArtifactText);
        string germanArtifact = await WithCultureAsync(new CultureInfo("de-DE"), async () => (await sut.BuildAsync(request).ConfigureAwait(false)).ArtifactText);

        Assert.Equal(invariantArtifact, germanArtifact);
        Assert.Contains("12.5", invariantArtifact, StringComparison.Ordinal);
        Assert.DoesNotContain("12,5", germanArtifact, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_SupersededTarget_DoesNotScoreOrRender()
    {
        var request = new CreatorStyleRequest
        {
            CreatorSlug = "alpha",
            DeckText = "1 Arcane Signet",
        };

        IReadOnlyList<FusedTarget>? scoredTargets = null;
        CreatorStyleProfile profile = CreateProfile(
            "alpha",
            fusedTargets:
            [
                new FusedTarget
                {
                    Metric = "category_ratio:ramp",
                    Value = 12.5,
                    Weight = 0.8,
                    Source = "fused",
                    Confidence = "high",
                },
                new FusedTarget
                {
                    Metric = "category_ratio:ramp",
                    Value = 45,
                    Weight = 1.0,
                    Source = "superseded",
                    Verdict = "superseded",
                    Confidence = "low",
                },
            ]);

        CreatorStylePacketService sut = CreateSut(
            profile: profile,
            whitelistResult: new CreatorWhitelistPoolBuildResult
            {
                AcceptedNames = [],
                HasUpstreamFailure = false,
            },
            validateAdditionalCardsAsync: (_, _, _) => Task.FromResult(new CardGroundingBatchResult
            {
                Verdicts = [Accepted("Commander One")],
                HasUpstreamFailure = false,
            }),
            creatorDecks:
            [
                CreatorDeck("deck-1", "trusted-folder", "ok", "Commander One"),
            ],
            onScoreTargets: targets => scoredTargets = targets);

        CreatorStylePacketResult result = await sut.BuildAsync(request);

        Assert.NotNull(scoredTargets);
        Assert.Single(scoredTargets!);
        Assert.DoesNotContain(scoredTargets!, target => string.Equals(target.Verdict, "superseded", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("Value: 45", result.ArtifactText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_ConditionalTarget_RendersConditionButDoesNotScore()
    {
        var request = new CreatorStyleRequest
        {
            CreatorSlug = "alpha",
            DeckText = "1 Arcane Signet",
        };

        RubricScoreResult rubric = new()
        {
            CreatorSlug = "alpha",
            MetricScores =
            [
                new RubricMetricScore
                {
                    Metric = "category_ratio:ramp",
                    TargetValue = 12.5,
                    SubmittedValue = 10.5,
                    Delta = -2,
                    Weight = 0.8,
                    Verdict = "under",
                    Confidence = "high",
                },
            ],
        };
        IReadOnlyList<FusedTarget>? scoredTargets = null;
        CreatorStyleProfile profile = CreateProfile(
            "alpha",
            fusedTargets:
            [
                new FusedTarget
                {
                    Metric = "category_ratio:ramp",
                    Value = 12.5,
                    Weight = 0.8,
                    Source = "fused",
                    Confidence = "high",
                },
                new FusedTarget
                {
                    Metric = "karsten:target_lands",
                    Value = 37,
                    Weight = 0.6,
                    Source = "fused",
                    Condition = "Only when landfall",
                    Confidence = "medium",
                },
            ]);

        CreatorStylePacketService sut = CreateSut(
            profile: profile,
            whitelistResult: new CreatorWhitelistPoolBuildResult
            {
                AcceptedNames = [],
                HasUpstreamFailure = false,
            },
            validateAdditionalCardsAsync: (_, _, _) => Task.FromResult(new CardGroundingBatchResult
            {
                Verdicts = [Accepted("Commander One")],
                HasUpstreamFailure = false,
            }),
            creatorDecks:
            [
                CreatorDeck("deck-1", "trusted-folder", "ok", "Commander One"),
            ],
            scoreRubric: rubric,
            onScoreTargets: targets => scoredTargets = targets);

        CreatorStylePacketResult result = await sut.BuildAsync(request);

        Assert.NotNull(scoredTargets);
        Assert.Single(scoredTargets!);
        Assert.DoesNotContain(scoredTargets!, target => !string.IsNullOrWhiteSpace(target.Condition));
        Assert.Contains("Condition: Only when landfall", result.ArtifactText, StringComparison.Ordinal);
        Assert.DoesNotContain("Metric: karsten:target_lands; Target:", result.ArtifactText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_DeckResolutionDegraded_SetsGroundingNotice()
    {
        var request = new CreatorStyleRequest
        {
            CreatorSlug = "alpha",
            DeckText = "1 Arcane Signet",
        };

        SubmittedDeckAnalysis analysis = CreateAnalysis(
            deckSize: 99,
            entries:
            [
                DeckEntry("Commander One", 1, "commander"),
            ],
            deckResolutionDegraded: true);

        CreatorStylePacketService sut = CreateSut(
            analysis: analysis,
            whitelistResult: new CreatorWhitelistPoolBuildResult
            {
                AcceptedNames = [],
                HasUpstreamFailure = false,
            },
            validateAdditionalCardsAsync: (_, _, _) => Task.FromResult(new CardGroundingBatchResult
            {
                Verdicts = [Accepted("Commander One")],
                HasUpstreamFailure = false,
            }),
            creatorDecks:
            [
                CreatorDeck("deck-1", "trusted-folder", "ok", "Commander One"),
            ]);

        CreatorStylePacketResult result = await sut.BuildAsync(request);

        Assert.True(result.GroundingDegraded);
        Assert.Equal("The submitted deck could not be fully resolved for grounding-sensitive analysis.", result.Notice);
        Assert.NotNull(result.Notice);
        Assert.Contains(result.Notice, result.ArtifactText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_ValidationCountMismatch_ThrowsInvalidOperationException()
    {
        var request = new CreatorStyleRequest
        {
            CreatorSlug = "alpha",
            DeckText = "1 Arcane Signet",
        };

        CreatorStylePacketService sut = CreateSut(
            whitelistResult: new CreatorWhitelistPoolBuildResult
            {
                AcceptedNames = [],
                HasUpstreamFailure = false,
            },
            validateAdditionalCardsAsync: (_, _, _) => Task.FromResult(new CardGroundingBatchResult
            {
                Verdicts = [],
                HasUpstreamFailure = false,
            }),
            creatorDecks:
            [
                CreatorDeck("deck-1", "trusted-folder", "ok", "Commander One"),
            ]);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.BuildAsync(request));

        Assert.Contains("verdict", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("candidate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildAsync_ArtifactTextRoundsDisplayedNumbersToThreeDecimals()
    {
        var request = new CreatorStyleRequest
        {
            CreatorSlug = "alpha",
            DeckText = "1 Arcane Signet",
        };

        CreatorStyleProfile profile = CreateProfile(
            "alpha",
            fusedTargets:
            [
                new FusedTarget
                {
                    Metric = "category_ratio:ramp",
                    Value = 12.333333333333334d,
                    Weight = 0.8,
                    Source = "fused",
                    Confidence = "high",
                },
            ]);

        RubricScoreResult rubric = new()
        {
            CreatorSlug = "alpha",
            MetricScores =
            [
                new RubricMetricScore
                {
                    Metric = "category_ratio:ramp",
                    TargetValue = 12.333333333333334d,
                    SubmittedValue = 10.033333333333333d,
                    Delta = -2.3000000000000007d,
                    Weight = 0.8,
                    Verdict = "under",
                    Confidence = "high",
                },
            ],
        };

        CreatorStylePacketService sut = CreateSut(
            profile: profile,
            whitelistResult: new CreatorWhitelistPoolBuildResult
            {
                AcceptedNames = [],
                HasUpstreamFailure = false,
            },
            validateAdditionalCardsAsync: (_, _, _) => Task.FromResult(new CardGroundingBatchResult
            {
                Verdicts = [Accepted("Commander One")],
                HasUpstreamFailure = false,
            }),
            creatorDecks:
            [
                CreatorDeck("deck-1", "trusted-folder", "ok", "Commander One"),
            ],
            scoreRubric: rubric);

        CreatorStylePacketResult result = await sut.BuildAsync(request);

        Assert.Contains("Value: 12.333", result.ArtifactText, StringComparison.Ordinal);
        Assert.Contains("Delta: -2.3", result.ArtifactText, StringComparison.Ordinal);
        Assert.DoesNotContain("12.333333333333334", result.ArtifactText, StringComparison.Ordinal);
        Assert.DoesNotContain("-2.3000000000000007", result.ArtifactText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_RejectedCandidateWithoutUpstreamFailure_StillSetsGroundingDegraded()
    {
        var request = new CreatorStyleRequest
        {
            CreatorSlug = "alpha",
            DeckText = "1 Arcane Signet",
        };

        CreatorStylePacketService sut = CreateSut(
            analysis: CreateAnalysis(
                deckSize: 99,
                entries:
                [
                    DeckEntry("Commander One", 1, "commander"),
                ],
                includedComboCardNames: ["Hullbreacher"]),
            whitelistResult: new CreatorWhitelistPoolBuildResult
            {
                AcceptedNames = [],
                HasUpstreamFailure = false,
            },
            validateAdditionalCardsAsync: (candidateNames, _, _) => Task.FromResult(new CardGroundingBatchResult
            {
                Verdicts = candidateNames.Select(candidateName => candidateName switch
                {
                    "Hullbreacher" => Rejected("Hullbreacher", CardGroundingRejectReason.NotLegal),
                    _ => Accepted(candidateName),
                }).ToArray(),
                HasUpstreamFailure = false,
            }),
            creatorDecks:
            [
                CreatorDeck("deck-1", "trusted-folder", "ok", "Commander One"),
            ]);

        CreatorStylePacketResult result = await sut.BuildAsync(request);

        Assert.True(result.GroundingDegraded);
        Assert.Equal(CardsWithheldNotice, result.Notice);
    }

    [Fact]
    public async Task BuildAsync_ExemplarCardNames_DeduplicatesOrdinalDuplicates()
    {
        var request = new CreatorStyleRequest
        {
            CreatorSlug = "alpha",
            DeckText = "1 Arcane Signet",
        };

        CreatorStylePacketService sut = CreateSut(
            creatorDecks:
            [
                CreatorDeck("deck-1", "trusted-folder", "ok", "Arcane Signet", "Arcane Signet", "Commander One"),
            ]);

        CreatorStylePacketResult result = await sut.BuildAsync(request);

        Assert.Equal(
            ["Arcane Signet", "Commander One"],
            Assert.Single(result.Exemplars).CardNames.OrderBy(static cardName => cardName, StringComparer.Ordinal).ToArray());
    }

    private static CreatorStyleProfile CreateProfile(string slug, bool insufficientSample = false, IReadOnlyList<FusedTarget>? fusedTargets = null)
        => new()
        {
            Slug = slug,
            Platform = "archidekt",
            MinDecks = 12,
            InsufficientSample = insufficientSample,
            FusedTargets = fusedTargets ??
                [
                    new FusedTarget
                    {
                        Metric = "category_ratio:ramp",
                        Value = 12.5,
                        Weight = 0.8,
                        Source = "fused",
                        StatedMin = 10,
                        StatedMax = 15,
                        Confidence = "high",
                    },
                ],
            UpdatedUtc = new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero),
        };

    private static SubmittedDeckAnalysis CreateAnalysis(
        int deckSize,
        IReadOnlyList<DeckEntry> entries,
        IReadOnlyList<string>? includedComboCardNames = null,
        bool deckResolutionDegraded = false)
        => new()
        {
            Stats = new SubmittedDeckStats
            {
                DeckSize = deckSize,
                CommanderCount = entries.Count(entry => string.Equals(entry.Board, "commander", StringComparison.OrdinalIgnoreCase)),
                Metrics = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    ["category_ratio:ramp"] = 10.5,
                },
            },
            DeckContext = new CardGroundingDeckContext
            {
                CommanderColorIdentity = new HashSet<string>(StringComparer.Ordinal) { "U", "R" },
                DeckProducedColors = new HashSet<char> { 'U', 'R' },
                DeckCardNames = entries.Select(entry => CardNormalizer.Normalize(entry.Name)).ToHashSet(StringComparer.Ordinal),
            },
            Entries = entries,
            IncludedComboCardNames = includedComboCardNames ?? [],
            DeckResolutionDegraded = deckResolutionDegraded,
            ResolvedCommanderName = "Commander One",
            ImportNotice = null,
        };

    private static CreatorDeckCacheEntry CreatorDeck(
        string deckId,
        string folderName,
        string confidenceMarker,
        params string[] cardNames)
        => new()
        {
            CreatorSlug = "alpha",
            DeckId = deckId,
            ContentHash = $"{deckId}-hash",
            FolderName = folderName,
            Size = cardNames.Length,
            ConfidenceMarker = confidenceMarker,
            Entries = cardNames.Select(cardName => DeckEntry(cardName, 1, "mainboard")).ToArray(),
            CachedUtc = new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero),
        };

    private static DeckEntry DeckEntry(string name, int quantity, string board)
        => new()
        {
            Name = name,
            NormalizedName = CardNormalizer.Normalize(name),
            Quantity = quantity,
            Board = board,
        };

    private static CardGroundingVerdict Accepted(string canonicalName)
        => new()
        {
            Accepted = true,
            CanonicalName = canonicalName,
            RejectReason = CardGroundingRejectReason.None,
        };

    private static CardGroundingVerdict Rejected(string canonicalName, CardGroundingRejectReason rejectReason)
        => new()
        {
            Accepted = false,
            CanonicalName = canonicalName,
            RejectReason = rejectReason,
        };

    private static RubricScoreResult EmptyRubric(string creatorSlug)
        => new()
        {
            CreatorSlug = creatorSlug,
            MetricScores = [],
        };

    private static CreatorStylePacketService CreateSut(
        CreatorStyleProfile? profile = null,
        SubmittedDeckAnalysis? analysis = null,
        CreatorWhitelistPoolBuildResult? whitelistResult = null,
        Func<IReadOnlyList<string>, CardGroundingDeckContext, CancellationToken, Task<CardGroundingBatchResult>>? validateAdditionalCardsAsync = null,
        IReadOnlyList<CreatorDeckCacheEntry>? creatorDecks = null,
        RubricScoreResult? scoreRubric = null,
        Func<string, CancellationToken, Task<CreatorStyleProfile?>>? getProfileAsync = null,
        Func<string, CardGroundingDeckContext, CancellationToken, Task<CreatorWhitelistPoolBuildResult>>? buildWhitelistAsync = null,
        Func<string, CancellationToken, Task<IReadOnlyList<CreatorDeckCacheEntry>>>? getCreatorDecksAsync = null,
        Func<string, IReadOnlyList<FusedTarget>, SubmittedDeckStats, RubricScoreResult>? scoreRubricFunc = null,
        Action<IReadOnlyList<FusedTarget>>? onScoreTargets = null,
        PacketSessionCache? packetCache = null,
        IFeatureFlagCache? flagCache = null)
    {
        CreatorStyleProfile defaultProfile = profile ?? CreateProfile("alpha");
        SubmittedDeckAnalysis defaultAnalysis = analysis ?? CreateAnalysis(
            deckSize: 99,
            entries:
            [
                DeckEntry("Commander One", 1, "commander"),
            ]);
        CreatorWhitelistPoolBuildResult defaultWhitelistResult = whitelistResult ?? new CreatorWhitelistPoolBuildResult
        {
            AcceptedNames = [],
            HasUpstreamFailure = false,
        };
        IReadOnlyList<CreatorDeckCacheEntry> defaultCreatorDecks = creatorDecks ??
        [
            CreatorDeck("deck-1", "trusted-folder", "ok", "Commander One"),
        ];
        RubricScoreResult defaultRubric = scoreRubric ?? EmptyRubric(defaultProfile.Slug);

        return new CreatorStylePacketService(
            getProfileAsync: getProfileAsync ?? ((_, _) => Task.FromResult<CreatorStyleProfile?>(defaultProfile)),
            buildSubmittedDeckAsync: (_, _) => Task.FromResult(defaultAnalysis),
            buildWhitelistAsync: buildWhitelistAsync ?? ((_, _, _) => Task.FromResult(defaultWhitelistResult)),
            validateAdditionalCardsAsync: validateAdditionalCardsAsync ?? ((candidateNames, _, _) => Task.FromResult(new CardGroundingBatchResult
            {
                Verdicts = candidateNames.Select(Accepted).ToArray(),
                HasUpstreamFailure = false,
            })),
            getCreatorDecksAsync: getCreatorDecksAsync ?? ((_, _) => Task.FromResult(defaultCreatorDecks)),
            scoreRubric: scoreRubricFunc ?? ((creatorSlug, targets, stats) =>
            {
                onScoreTargets?.Invoke(targets);
                return scoreRubric ?? defaultRubric;
            }),
            packetCache: packetCache,
            flagCache: flagCache);
    }

    private static CreatorStyleRequest CreateCacheRequest()
        => new()
        {
            CreatorSlug = "alpha",
            DeckText = "1 Arcane Signet",
            Format = "Commander",
        };

    private sealed class FakeFeatureFlagCache : IFeatureFlagCache
    {
        public FakeFeatureFlagCache(IReadOnlyDictionary<string, bool> flags)
        {
            Flags = new Dictionary<string, bool>(flags, StringComparer.Ordinal);
        }

        public Dictionary<string, bool> Flags { get; }

        public bool IsEnabled(string key) => Snapshot().TryGetValue(key, out bool enabled) && enabled;

        public IReadOnlyDictionary<string, bool> Snapshot() => Flags;

        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FlipAfterNSnapshotsFeatureFlagCache : IFeatureFlagCache
    {
        private readonly string _flagKey;
        private readonly int _trueCallCount;
        private int _callCount;

        public FlipAfterNSnapshotsFeatureFlagCache(string flagKey, int trueCallCount)
        {
            _flagKey = flagKey;
            _trueCallCount = trueCallCount;
        }

        public bool IsEnabled(string key) => Snapshot().TryGetValue(key, out bool enabled) && enabled;

        public IReadOnlyDictionary<string, bool> Snapshot()
        {
            _callCount++;
            return new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                [_flagKey] = _callCount <= _trueCallCount,
            };
        }

        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static async Task<string> WithCultureAsync(CultureInfo culture, Func<Task<string>> action)
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            return await action().ConfigureAwait(false);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
