using System.Text;
using DeckFlow.Web.Models.CutLab;
using DeckFlow.Web.Services.CutLab;
using Xunit;

namespace DeckFlow.Web.Tests;

/// <summary>Tests for <see cref="CutLabStateSerializer"/> covering round-trip, tamper defense, and size/error handling.</summary>
public sealed class CutLabStateSerializerTests
{
    [Theory]
    [InlineData("pool")]
    [InlineData("packages")]
    [InlineData("decisions")]
    [InlineData("quantityAdjustments")]
    [InlineData("originalEntries")]
    [InlineData("roleFloors")]
    public void Deserialize_ExplicitNullCollection_ReturnsEmptyCollection(string propertyName)
    {
        var state = CutLabStateSerializer.Deserialize($"{{\"{propertyName}\":null}}");

        Assert.NotNull(GetCollection(state, propertyName));
        Assert.Empty(GetCollection(state, propertyName));
    }

    [Fact]
    public void Deserialize_ExplicitNullGenericStrategies_ReturnsEmptyCollection()
    {
        var state = CutLabStateSerializer.Deserialize("{\"intent\":{\"planProfile\":{\"genericStrategies\":null}}}");

        Assert.NotNull(state.Intent.PlanProfile);
        Assert.NotNull(state.Intent.PlanProfile.GenericStrategies);
        Assert.Empty(state.Intent.PlanProfile.GenericStrategies);
    }

    private static System.Collections.IEnumerable GetCollection(CutLabState state, string propertyName) =>
        propertyName switch
        {
            "pool" => state.Pool,
            "packages" => state.Packages,
            "decisions" => state.Decisions,
            "quantityAdjustments" => state.QuantityAdjustments,
            "originalEntries" => state.OriginalEntries,
            "roleFloors" => state.RoleFloors,
            _ => throw new ArgumentOutOfRangeException(nameof(propertyName)),
        };

    [Fact]
    public void SerializeDeserialize_RoundTripsState_AndReLocksCommander()
    {
        var state = new CutLabState
        {
            Commander = "Atraxa, Praetors' Voice",
            Pool =
            [
                new CutLabPoolCard
                {
                    Name = "Atraxa, Praetors' Voice",
                    Quantity = 1,
                    TypeLine = "Legendary Creature — Phyrexian Angel Horror",
                    IsCommander = true,
                    IsLocked = false,
                },
                new CutLabPoolCard
                {
                    Name = "Arcane Signet",
                    Quantity = 1,
                    TypeLine = "Artifact",
                    IsLocked = true,
                    PackageId = "ramp",
                },
            ],
            Packages =
            [
                new CutLabPackage
                {
                    Id = "ramp",
                    Name = "Ramp Core",
                    Locked = true,
                },
            ],
            RoleFloors =
            [
                new CutLabRoleFloor
                {
                    Role = "interaction-targeted",
                    Floor = 5,
                    IsUserSet = true,
                },
                new CutLabRoleFloor
                {
                    Role = "interaction-mass",
                    Floor = 2,
                    IsUserSet = true,
                },
                new CutLabRoleFloor
                {
                    Role = "draw",
                    Floor = 12,
                    IsUserSet = false,
                },
            ],
            Intent = new CutLabIntent
            {
                PrimaryPlan = "Counters",
                SecondaryPlan = "Blink",
                Bracket = 3,
                PlayExperience = "Resilient midrange",
            },
        };

        var json = CutLabStateSerializer.Serialize(state);
        var roundTripped = CutLabStateSerializer.Deserialize(json);

        Assert.Contains("\"roleFloors\"", json);
        Assert.Equal("Atraxa, Praetors' Voice", roundTripped.Commander);
        Assert.Equal(2, roundTripped.Pool.Count);
        Assert.Equal("ramp", Assert.Single(roundTripped.Packages).Id);
        Assert.Equal(state.RoleFloors, roundTripped.RoleFloors);
        Assert.Equal("Counters", roundTripped.Intent.PrimaryPlan);
        Assert.Equal("Blink", roundTripped.Intent.SecondaryPlan);
        Assert.Equal(3, roundTripped.Intent.Bracket);
        Assert.Equal("Resilient midrange", roundTripped.Intent.PlayExperience);
        Assert.True(Assert.Single(roundTripped.Pool, card => card.IsCommander).IsLocked);
        Assert.True(Assert.Single(roundTripped.Pool, card => card.Name == "Arcane Signet").IsLocked);
    }

    [Fact]
    public void Deserialize_Pre102JsonWithoutRoleFloors_ReturnsEmptyRoleFloors_AndReLocksCommander()
    {
        const string json =
            """
            {
              "commander": "Atraxa, Praetors' Voice",
              "pool": [
                {
                  "name": "Atraxa, Praetors' Voice",
                  "quantity": 1,
                  "typeLine": "Legendary Creature — Phyrexian Angel Horror",
                  "isCommander": true,
                  "isLocked": false
                },
                {
                  "name": "Arcane Signet",
                  "quantity": 1,
                  "typeLine": "Artifact",
                  "isCommander": false,
                  "isLocked": true
                }
              ],
              "packages": [],
              "intent": {
                "primaryPlan": "Counters",
                "secondaryPlan": null,
                "bracket": 3,
                "playExperience": "Focused"
              }
            }
            """;

        var state = CutLabStateSerializer.Deserialize(json);

        Assert.Empty(state.RoleFloors);
        Assert.True(Assert.Single(state.Pool, card => card.IsCommander).IsLocked);
    }

    [Fact]
    public void Deserialize_TamperedRoleFloors_ClampsAndDropsInvalidEntries()
    {
        const string json =
            """
            {
              "commander": "Atraxa, Praetors' Voice",
              "pool": [],
              "packages": [],
              "roleFloors": [
                {
                  "role": "wincons",
                  "floor": -3,
                  "isUserSet": true
                },
                {
                  "role": "battlecruiser",
                  "floor": 5,
                  "isUserSet": true
                }
              ],
              "intent": {
                "primaryPlan": "Counters",
                "secondaryPlan": null,
                "bracket": 3,
                "playExperience": "Focused"
              }
            }
            """;

        var state = CutLabStateSerializer.Deserialize(json);

        var floor = Assert.Single(state.RoleFloors);
        Assert.Equal("wincons", floor.Role);
        Assert.Equal(0, floor.Floor);
        Assert.True(floor.IsUserSet);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Deserialize_BlankJson_ReturnsEmptyState(string? json)
    {
        var state = CutLabStateSerializer.Deserialize(json);

        Assert.Equal(string.Empty, state.Commander);
        Assert.Empty(state.Pool);
        Assert.Empty(state.Packages);
        Assert.Equal(string.Empty, state.Intent.PrimaryPlan);
    }

    [Fact]
    public void Deserialize_MalformedJson_ReturnsEmptyState()
    {
        var state = CutLabStateSerializer.Deserialize("{ not-json");

        Assert.Equal(string.Empty, state.Commander);
        Assert.Empty(state.Pool);
        Assert.Empty(state.Packages);
        Assert.Equal(string.Empty, state.Intent.PlayExperience);
    }

    [Fact]
    public void Deserialize_OversizedJson_ReturnsEmptyState()
    {
        var oversizedName = new string('A', CutLabStateSerializer.MaxUploadBytes);
        var state = CutLabStateSerializer.Deserialize($"{{\"commander\":\"{oversizedName}\"}}");

        Assert.Equal(string.Empty, state.Commander);
        Assert.Empty(state.Pool);
        Assert.Empty(state.Packages);
    }

    [Fact]
    public void Deserialize_TamperedPackages_DropsEmptyNamesAndCapsAtFifty()
    {
        string packagesJson = string.Join(
            ",",
            Enumerable.Range(1, 52).Select(index =>
                $$"""{"id":"pkg-{{index}}","name":"Package {{index}}","locked":{{(index % 2 == 0).ToString().ToLowerInvariant()}}}"""));
        string json =
            $$"""
            {
              "commander": "Atraxa, Praetors' Voice",
              "pool": [],
              "packages": [
                {"id":"blank-1","name":"","locked":false},
                {"id":"blank-2","name":"   ","locked":true},
                {{packagesJson}}
              ],
              "roleFloors": [],
              "intent": {
                "primaryPlan": "Counters",
                "secondaryPlan": null,
                "bracket": 3,
                "playExperience": "Focused"
              }
            }
            """;

        var state = CutLabStateSerializer.Deserialize(json);

        Assert.Equal(50, state.Packages.Count);
        Assert.DoesNotContain(state.Packages, package => string.IsNullOrWhiteSpace(package.Name));
        Assert.Equal("pkg-1", state.Packages[0].Id);
        Assert.Equal("pkg-50", state.Packages[^1].Id);
    }

    [Fact]
    public void Serialize_StateExceedsMaxUploadBytes_ThrowsInvalidOperationException()
    {
        var oversizedName = new string('A', CutLabStateSerializer.MaxUploadBytes);
        var state = new CutLabState
        {
            Commander = "Atraxa, Praetors' Voice",
            Pool =
            [
                new CutLabPoolCard
                {
                    Name = oversizedName,
                    Quantity = 1,
                    TypeLine = "Artifact",
                },
            ],
        };

        var exception = Assert.Throws<InvalidOperationException>(() => CutLabStateSerializer.Serialize(state));

        Assert.Equal("The Cut Lab working session is too large to save.", exception.Message);
    }

    [Fact]
    public void Serialize_FreshState_DoesNotWriteLegacyCombinedBoardFlag()
    {
        var state = new CutLabState
        {
            Intent = new CutLabIntent
            {
                PrimaryPlan = "Counters",
                IncludeSideboard = true,
                IncludeMaybeboard = true,
            },
        };

        var json = CutLabStateSerializer.Serialize(state);

        Assert.DoesNotContain("includeSideboardAndMaybeboard", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeDeserialize_RoundTripsDecisionsAndBaselineSnapshot_WithoutMutatingPool()
    {
        var state = new CutLabState
        {
            Commander = "Atraxa, Praetors' Voice",
            Pool =
            [
                new CutLabPoolCard
                {
                    Name = "Atraxa, Praetors' Voice",
                    Quantity = 1,
                    TypeLine = "Legendary Creature — Phyrexian Angel Horror",
                    IsCommander = true,
                    IsLocked = true,
                },
                new CutLabPoolCard
                {
                    Name = "Arcane Signet",
                    Quantity = 1,
                    TypeLine = "Artifact",
                    IsLocked = true,
                    PackageId = "ramp",
                },
                new CutLabPoolCard
                {
                    Name = "Brainstorm",
                    Quantity = 1,
                    TypeLine = "Instant",
                },
            ],
            Decisions =
            [
                new CutLabDecision
                {
                    CardName = "Arcane Signet",
                    Kind = CutLabDecisionKind.Accepted,
                    Round = "obvious-cuts",
                    Ordinal = 3,
                },
                new CutLabDecision
                {
                    CardName = "Brainstorm",
                    Kind = CutLabDecisionKind.Rejected,
                    Round = "structural-choices",
                    Ordinal = 4,
                },
                new CutLabDecision
                {
                    CardName = "Ponder",
                    Kind = CutLabDecisionKind.Deferred,
                    Round = "preference-calls",
                    Ordinal = 5,
                },
            ],
            BaselineSnapshot = CreateBaselineSnapshot(),
        };

        var json = CutLabStateSerializer.Serialize(state);
        var roundTripped = CutLabStateSerializer.Deserialize(json);

        Assert.Equal(state.Pool, roundTripped.Pool);
        Assert.Equal(state.Decisions, roundTripped.Decisions);
        Assert.NotNull(roundTripped.BaselineSnapshot);
        Assert.Equal(state.BaselineSnapshot!.Metrics, roundTripped.BaselineSnapshot.Metrics);
    }

    [Fact]
    public void Deserialize_DecisionsOverMax_TruncatesToFiveHundredNonBlankEntries()
    {
        string decisionsJson = string.Join(
            ",",
            Enumerable.Range(1, 503).Select(index =>
                $$"""{"cardName":"Card {{index}}","kind":0,"round":"round-1","ordinal":{{index}}}"""));
        string json =
            $$"""
            {
              "commander": "Atraxa, Praetors' Voice",
              "pool": [],
              "packages": [],
              "decisions": [
                {"cardName":"","kind":0,"round":"round-0","ordinal":0},
                {"cardName":"   ","kind":1,"round":"round-0","ordinal":-1},
                {{decisionsJson}}
              ],
              "roleFloors": [],
              "intent": {
                "primaryPlan": "Counters",
                "secondaryPlan": null,
                "bracket": 3,
                "playExperience": "Focused"
              }
            }
            """;

        var state = CutLabStateSerializer.Deserialize(json);

        Assert.Equal(500, state.Decisions.Count);
        Assert.DoesNotContain(state.Decisions, decision => string.IsNullOrWhiteSpace(decision.CardName));
        Assert.Equal("Card 1", state.Decisions[0].CardName);
        Assert.Equal("Card 500", state.Decisions[^1].CardName);
    }

    [Fact]
    public void Deserialize_Pre103JsonWithoutDecisionsOrBaselineSnapshot_ReturnsEmptyDefaults()
    {
        const string json =
            """
            {
              "commander": "Atraxa, Praetors' Voice",
              "pool": [
                {
                  "name": "Atraxa, Praetors' Voice",
                  "quantity": 1,
                  "typeLine": "Legendary Creature — Phyrexian Angel Horror",
                  "isCommander": true,
                  "isLocked": false
                }
              ],
              "packages": [],
              "roleFloors": [],
              "intent": {
                "primaryPlan": "Counters",
                "secondaryPlan": null,
                "bracket": 3,
                "playExperience": "Focused"
              }
            }
            """;

        var state = CutLabStateSerializer.Deserialize(json);

        Assert.Empty(state.Decisions);
        Assert.Null(state.BaselineSnapshot);
    }

    [Fact]
    public void SerializeDeserialize_QuantityAdjustments_RoundTripUnchanged()
    {
        var state = new CutLabState
        {
            Commander = "Atraxa, Praetors' Voice",
            Pool =
            [
                new CutLabPoolCard
                {
                    Name = "Atraxa, Praetors' Voice",
                    Quantity = 1,
                    TypeLine = "Legendary Creature — Phyrexian Angel Horror",
                    IsCommander = true,
                    IsLocked = true,
                },
                new CutLabPoolCard
                {
                    Name = "Island",
                    Quantity = 35,
                    TypeLine = "Basic Land — Island",
                },
            ],
            QuantityAdjustments =
            [
                new CutLabQuantityAdjustment
                {
                    Name = "Island",
                    Delta = -3,
                    IsAddedBasic = false,
                },
                new CutLabQuantityAdjustment
                {
                    Name = "Wastes",
                    Delta = 2,
                    IsAddedBasic = true,
                },
            ],
        };

        string json = CutLabStateSerializer.Serialize(state);
        CutLabState roundTripped = CutLabStateSerializer.Deserialize(json);

        Assert.Equal(state.QuantityAdjustments, roundTripped.QuantityAdjustments);
    }

    [Fact]
    public void Deserialize_Pre106JsonWithoutQuantityAdjustments_ReturnsEmptyList()
    {
        const string json =
            """
            {
              "commander": "Atraxa, Praetors' Voice",
              "pool": [],
              "packages": [],
              "decisions": [],
              "roleFloors": [],
              "goals": {
                "commanderByTurn": 3,
                "engineByTurn": 2,
                "representativeLineByTurn": 4
              },
              "intent": {
                "primaryPlan": "Counters",
                "secondaryPlan": null,
                "bracket": 3,
                "playExperience": "Focused"
              }
            }
            """;

        CutLabState state = CutLabStateSerializer.Deserialize(json);

        Assert.Empty(state.QuantityAdjustments);
    }

    [Fact]
    public void Deserialize_QuantityAdjustmentsOverMax_TruncatesToThreeHundredNonBlankEntries()
    {
        string adjustmentsJson = string.Join(
            ",",
            Enumerable.Range(1, 303).Select(index =>
                $$"""{"name":"Card {{index}}","delta":{{index}},"isAddedBasic":false}"""));
        string json =
            $$"""
            {
              "commander": "Atraxa, Praetors' Voice",
              "pool": [],
              "packages": [],
              "decisions": [],
              "quantityAdjustments": [
                {"name":"","delta":1,"isAddedBasic":false},
                {"name":"   ","delta":-1,"isAddedBasic":true},
                {{adjustmentsJson}}
              ],
              "roleFloors": [],
              "intent": {
                "primaryPlan": "Counters",
                "secondaryPlan": null,
                "bracket": 3,
                "playExperience": "Focused"
              }
            }
            """;

        CutLabState state = CutLabStateSerializer.Deserialize(json);

        Assert.Equal(300, state.QuantityAdjustments.Count);
        Assert.DoesNotContain(state.QuantityAdjustments, adjustment => string.IsNullOrWhiteSpace(adjustment.Name));
        Assert.Equal("Card 1", state.QuantityAdjustments[0].Name);
        Assert.Equal("Card 300", state.QuantityAdjustments[^1].Name);
    }

    [Fact]
    public void Deserialize_QuantityAdjustments_ClampsDeltaAndDropsBlankNames()
    {
        const string json =
            """
            {
              "commander": "Atraxa, Praetors' Voice",
              "pool": [],
              "packages": [],
              "decisions": [],
              "quantityAdjustments": [
                {
                  "name": "Island",
                  "delta": -999,
                  "isAddedBasic": false
                },
                {
                  "name": "Wastes",
                  "delta": 999,
                  "isAddedBasic": true
                },
                {
                  "name": "",
                  "delta": 4,
                  "isAddedBasic": true
                }
              ],
              "roleFloors": [],
              "intent": {
                "primaryPlan": "Counters",
                "secondaryPlan": null,
                "bracket": 3,
                "playExperience": "Focused"
              }
            }
            """;

        CutLabState state = CutLabStateSerializer.Deserialize(json);

        Assert.Collection(
            state.QuantityAdjustments,
            island =>
            {
                Assert.Equal("Island", island.Name);
                Assert.Equal(-150, island.Delta);
                Assert.False(island.IsAddedBasic);
            },
            wastes =>
            {
                Assert.Equal("Wastes", wastes.Name);
                Assert.Equal(150, wastes.Delta);
                Assert.True(wastes.IsAddedBasic);
            });
    }

    [Fact]
    public void NewState_Goals_DefaultToSeededTurns()
    {
        var state = new CutLabState();

        Assert.Equal(3, state.Goals.CommanderByTurn);
        Assert.Equal(2, state.Goals.EngineByTurn);
        Assert.Equal(4, state.Goals.RepresentativeLineByTurn);
    }

    [Fact]
    public void Deserialize_Pre104JsonWithoutGoals_ReturnsSeededGoalDefaults()
    {
        const string json =
            """
            {
              "commander": "Atraxa, Praetors' Voice",
              "pool": [],
              "packages": [],
              "decisions": [],
              "roleFloors": [],
              "intent": {
                "primaryPlan": "Counters",
                "secondaryPlan": null,
                "bracket": 3,
                "playExperience": "Focused"
              }
            }
            """;

        var state = CutLabStateSerializer.Deserialize(json);

        Assert.Equal(3, state.Goals.CommanderByTurn);
        Assert.Equal(2, state.Goals.EngineByTurn);
        Assert.Equal(4, state.Goals.RepresentativeLineByTurn);
    }

    [Fact]
    public void Deserialize_TamperedGoals_ClampsToSupportedRange()
    {
        const string json =
            """
            {
              "commander": "Atraxa, Praetors' Voice",
              "pool": [],
              "packages": [],
              "decisions": [],
              "goals": {
                "commanderByTurn": 0,
                "engineByTurn": 99,
                "representativeLineByTurn": -4
              },
              "roleFloors": [],
              "intent": {
                "primaryPlan": "Counters",
                "secondaryPlan": null,
                "bracket": 3,
                "playExperience": "Focused"
              }
            }
            """;

        var state = CutLabStateSerializer.Deserialize(json);

        Assert.Equal(1, state.Goals.CommanderByTurn);
        Assert.Equal(15, state.Goals.EngineByTurn);
        Assert.Equal(1, state.Goals.RepresentativeLineByTurn);
    }

    [Fact]
    public void SerializeDeserialize_ValidGoals_RoundTripUnchanged()
    {
        var state = new CutLabState
        {
            Goals = new CutLabGoalSettings
            {
                CommanderByTurn = 5,
                EngineByTurn = 3,
                RepresentativeLineByTurn = 7,
            },
        };

        string json = CutLabStateSerializer.Serialize(state);
        CutLabState roundTripped = CutLabStateSerializer.Deserialize(json);

        Assert.Equal(state.Goals, roundTripped.Goals);
    }

    [Fact]
    public void ClampGoals_WhenAlreadyValid_ReturnsSameStateInstance()
    {
        var state = new CutLabState
        {
            Goals = new CutLabGoalSettings
            {
                CommanderByTurn = 5,
                EngineByTurn = 3,
                RepresentativeLineByTurn = 7,
            },
        };

        CutLabState clamped = CutLabGoalRules.ClampGoals(state);

        Assert.Same(state, clamped);
    }

    [Fact]
    public void Serialize_WorstCaseDecisionHistoryAndBaselineSnapshot_StaysUnderMaxUploadBytes()
    {
        var pool = Enumerable.Range(1, 150)
            .Select(index => new CutLabPoolCard
            {
                Name = $"Card {index}",
                Quantity = 1,
                TypeLine = index == 1 ? "Legendary Creature — Human Wizard" : "Artifact",
                IsCommander = index == 1,
                IsLocked = index == 1,
                PackageId = index <= 10 ? "pkg-core" : null,
            })
            .ToArray();
        var decisions = new List<CutLabDecision>();
        int ordinal = 1;

        foreach (int index in Enumerable.Range(1, 50))
        {
            decisions.Add(new CutLabDecision
            {
                CardName = $"Card {index}",
                Kind = CutLabDecisionKind.Deferred,
                Round = "round-1",
                Ordinal = ordinal++,
            });
            decisions.Add(new CutLabDecision
            {
                CardName = $"Card {index}",
                Kind = CutLabDecisionKind.Rejected,
                Round = "round-2",
                Ordinal = ordinal++,
            });
            decisions.Add(new CutLabDecision
            {
                CardName = $"Card {index}",
                Kind = CutLabDecisionKind.Accepted,
                Round = "round-3",
                Ordinal = ordinal++,
            });
        }

        foreach (int index in Enumerable.Range(51, 100))
        {
            decisions.Add(new CutLabDecision
            {
                CardName = $"Card {index}",
                Kind = CutLabDecisionKind.Accepted,
                Round = "round-3",
                Ordinal = ordinal++,
            });
        }

        var state = new CutLabState
        {
            Commander = "Card 1",
            Pool = pool,
            Decisions = decisions,
            BaselineSnapshot = CreateBaselineSnapshot(),
        };

        var json = CutLabStateSerializer.Serialize(state);

        Assert.True(Encoding.UTF8.GetByteCount(json) < CutLabStateSerializer.MaxUploadBytes);
    }

    [Fact]
    public void Deserialize_NullCommanderTheme_DropsNullElement()
    {
        const string json = """
            {"intent":{"planProfile":{"commanderThemes":[null]}}}
            """;

        CutLabState state = CutLabStateSerializer.Deserialize(json);

        Assert.Empty(state.Intent.PlanProfile!.CommanderThemes);
    }

    [Fact]
    public void Deserialize_NullPoolCard_DropsNullElement()
    {
        CutLabState state = CutLabStateSerializer.Deserialize("{\"pool\":[null]}");

        Assert.Empty(state.Pool);
    }

    [Fact]
    public void Deserialize_NullRoleFloor_DropsNullElement()
    {
        CutLabState state = CutLabStateSerializer.Deserialize("{\"roleFloors\":[null]}");

        Assert.Empty(state.RoleFloors);
    }

    [Fact]
    public void Deserialize_NullGoals_RestoresDefaults()
    {
        CutLabState state = CutLabStateSerializer.Deserialize("{\"goals\":null}");

        Assert.Equal(CutLabGoalDefaults.CommanderByTurn, state.Goals.CommanderByTurn);
        Assert.Equal(CutLabGoalDefaults.EngineByTurn, state.Goals.EngineByTurn);
        Assert.Equal(CutLabGoalDefaults.RepresentativeLineByTurn, state.Goals.RepresentativeLineByTurn);
    }

    [Fact]
    public void Deserialize_NullIntent_RestoresDefaults()
    {
        CutLabState state = CutLabStateSerializer.Deserialize("{\"intent\":null}");

        Assert.NotNull(state.Intent);
    }

    [Fact]
    public void Deserialize_NullPlanProfile_PreservesValidIntent()
    {
        CutLabState state = CutLabStateSerializer.Deserialize("{\"intent\":{\"planProfile\":null}}");

        Assert.NotNull(state.Intent);
        Assert.Null(state.Intent.PlanProfile);
    }

    [Fact]
    public void Deserialize_NullCommanderThemes_RestoresEmptyThemes()
    {
        CutLabState state = CutLabStateSerializer.Deserialize("{\"intent\":{\"planProfile\":{\"commanderThemes\":null}}}");

        Assert.NotNull(state.Intent);
        Assert.Empty(state.Intent.PlanProfile!.CommanderThemes);
    }

    [Theory]
    [InlineData("{\"pool\":null}", false)]
    [InlineData("{\"packages\":null}", false)]
    [InlineData("{\"decisions\":null}", false)]
    [InlineData("{\"quantityAdjustments\":null}", false)]
    [InlineData("{\"originalEntries\":null}", false)]
    [InlineData("{\"roleFloors\":null}", false)]
    [InlineData("{\"intent\":{\"planProfile\":{\"genericStrategies\":null}}}", true)]
    [InlineData("{\"intent\":{\"planProfile\":{\"commanderThemes\":null}}}", true)]
    public void Deserialize_NullCollection_RestoresEmptyState(string json, bool hasPlanProfile)
    {
        CutLabState state = CutLabStateSerializer.Deserialize(json);

        Assert.Empty(state.Pool);
        Assert.Empty(state.Packages);
        Assert.Empty(state.Decisions);
        Assert.Empty(state.QuantityAdjustments);
        Assert.Empty(state.OriginalEntries);
        Assert.Empty(state.RoleFloors);
        if (hasPlanProfile)
        {
            Assert.NotNull(state.Intent);
            Assert.NotNull(state.Intent.PlanProfile);
            Assert.Empty(state.Intent.PlanProfile!.GenericStrategies);
            Assert.Empty(state.Intent.PlanProfile.CommanderThemes);
        }
    }

    private static CutLabMetricSnapshot CreateBaselineSnapshot()
        => new()
        {
            Metrics =
            [
                CreateMetric(CutLabMetricKind.CommanderOnTime, CutLabMetricFamily.CommanderOnTime, "Commander on time", 71.2, CutLabMetricUnit.Percent),
                CreateMetric(CutLabMetricKind.KeepableHand, CutLabMetricFamily.KeepableHand, "Keepable hand", 82.5, CutLabMetricUnit.Percent),
                CreateMetric(CutLabMetricKind.ManaColorReliability, CutLabMetricFamily.ManaColorReliability, "Mana and color reliability", 76.3, CutLabMetricUnit.Percent),
                CreateMetric(CutLabMetricKind.EarlyInteraction, CutLabMetricFamily.EarlyInteraction, "Early interaction", 48.9, CutLabMetricUnit.Percent),
                CreateMetric(CutLabMetricKind.PlanPresence, CutLabMetricFamily.PlanPresence, "Plan presence", 64.1, CutLabMetricUnit.Percent),
                CreateMetric(CutLabMetricKind.CommanderByTurn, CutLabMetricFamily.CategoryByTurn, "Commander by turn 3", 57.7, CutLabMetricUnit.Percent),
                CreateMetric(CutLabMetricKind.EngineByTurn, CutLabMetricFamily.CategoryByTurn, "Engine by turn 2", 43.8, CutLabMetricUnit.Percent),
                CreateMetric(CutLabMetricKind.RepresentativeLineByTurn, CutLabMetricFamily.CategoryByTurn, "Representative line by turn 4", 39.2, CutLabMetricUnit.Percent),
                CreateMetric(CutLabMetricKind.Flood, CutLabMetricFamily.FloodScrewCurveRisk, "Flood risk", 2.0, CutLabMetricUnit.Cards),
                CreateMetric(CutLabMetricKind.Screw, CutLabMetricFamily.FloodScrewCurveRisk, "Screw risk", 11.4, CutLabMetricUnit.Percent),
                CreateMetric(CutLabMetricKind.Curve, CutLabMetricFamily.FloodScrewCurveRisk, "Curve risk", 6.0, CutLabMetricUnit.Cards),
            ],
        };

    private static CutLabMetricValue CreateMetric(
        CutLabMetricKind kind,
        CutLabMetricFamily family,
        string label,
        double value,
        CutLabMetricUnit unit)
        => new()
        {
            Kind = kind,
            Family = family,
            Label = label,
            Value = value,
            Unit = unit,
        };
}
