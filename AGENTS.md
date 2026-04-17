<claude-mem-context>
# Memory Context

# [decksyncworkbench] recent context, 2026-04-17 9:21am MDT

Legend: 🎯session 🔴bugfix 🟣feature 🔄refactor ✅change 🔵discovery ⚖️decision
Format: ID TIME TYPE TITLE
Fetch details: get_observations([IDs]) | Search: mem-search skill

Stats: 50 obs (20,453t read) | 579,104t work | 96% savings

### Apr 14, 2026
992 5:55p 🔵 Program.cs DI registration: ArchidektCacheJobService registered as both Singleton and HostedService via factory delegation
993 " 🔵 ArchidektCacheJobsControllerTests cover 4 cases but use DurationSeconds=600 which no longer matches the UI default
994 " 🔵 Archidekt integration layer: HTML scraping for deck IDs, Polly retry for API calls, per-deck error isolation in cache session
995 " 🔴 ArchidektDeckCacheSession: recent deck fetch failures now caught and retried instead of crashing the session
996 5:56p 🟣 Added RunAsync_WaitsForFullDurationWhenRecentDeckFetchFails test and FailingRecentDecksImporter fake
997 " 🔵 All 3 ArchidektDeckCacheSessionTests pass including new fetch-failure resilience test
1004 5:59p 🔵 ChatGptJsonTextFormatterService.ExtractJsonPayload strips markdown fencing from all three ChatGPT workflows
1007 " 🔵 EdhTop16Client uses GraphQL API with WinRate calculation; tested with injected executeAsync delegate
1037 6:15p 🔵 cEDH Meta Gap Results View Structure Inspected
1038 " 🔵 cEDH and Deck Comparison Views Share Same Top-Level Markup Pattern
1041 6:16p 🔴 cEDH Meta Gap View Refactored to Remove h5 Nesting
1045 " ✅ cEDH Meta Gap View Refactor Builds Successfully
1050 6:21p 🟣 cEDH Meta Gap Analysis Feature Staged for Commit
1051 6:22p ⚖️ Three cEDH Analysis Approaches Designed; Only Approach 1 Implemented
1052 " 🔵 CategorySuggestionService Now Depends on IScryfallTaggerService
1053 " 🔵 cEDH Workflow Step Routing Logic in DeckController POST Handler
1054 6:23p 🔴 Archidekt Background Harvest Fixed for Reliability
1055 " 🔴 ChatGptJsonTextFormatterService Hardened to Extract First Balanced JSON Object
1056 " ✅ Approach Design Docs Rewritten to Reflect Actual Implementation State
1057 6:27p ✅ README Updated to Document cEDH Meta Gap and Category Suggestion Changes
1058 " ⚖️ Approach Design Docs Removed; Documentation Consolidated into README
### Apr 15, 2026
1062 8:41a 🔵 cEDH Meta Gap prompt currently enforces JSON-only response with no prose
1063 " 🔵 Exact OUTPUT section in BuildPrompt that needs editing for prose-before-JSON change
1064 " ✅ cEDH Meta Gap prompt updated to request prose-before-JSON response and shortened chat title
1066 8:42a ✅ ChatGptCedhMetaGapServiceTests all 4 tests pass after prompt instruction changes
1067 8:47a ✅ CedhMetaTimePeriod dropdown labels improved from SCREAMING_SNAKE to human-readable text
1069 8:48a 🔵 Concurrent dotnet build fails with MSB3021 access denied on MtgDeckStudio.Core.dll
1070 8:50a 🔵 EDH Top 16 API returns tournamentDate as ISO datetime string, not plain date
1071 " 🔴 EdhTop16Client.ParseDate fixed to handle ISO datetime strings from live API
1073 8:51a ✅ EdhTop16ClientTests all 3 tests pass after ISO datetime fix and fixture update
1074 8:54a 🔵 CardNormalizer strips split card faces and normalizes to lowercase for matching
1075 " 🔴 cEDH Meta Gap prompt decklists now strip alternate face names from split and double-faced cards
1077 8:55a ✅ ChatGptCedhMetaGapServiceTests now 5/5 passing including new split-card normalization test
1085 9:04a 🔵 EDH Top 16 Reference Grid Header/Row Column Mismatch
1086 " 🔵 EDH Reference Grid Table Has No Dedicated CSS Styles
1087 9:05a 🔴 EDH Top 16 Reference Grid Table Row Alignment Fixed
1088 9:10a 🔵 CommanderSpellbookService Architecture and API Integration
1089 " 🔵 ChatGptCedhMetaGapService Does Not Use Commander Spellbook — DeckPacket and Comparison Services Do
1090 " 🔵 ICommanderSpellbookService Registered as Singleton — Safe to Inject into Scoped MetaGap Service
1091 9:11a 🟣 Commander Spellbook Combo Lookup Added to cEDH Meta Gap Service
1092 " 🟣 Test Coverage Added for Commander Spellbook Combo Injection in cEDH Meta Gap
1093 9:12a 🟣 All 5 ChatGptCedhMetaGapServiceTests Pass After Combo Integration
1096 9:15a ⚖️ cEDH Meta Gap Reference Deck Limit Reduced from 5 to 3
1102 9:38a 🔵 cEDH Deck List: Plagon, Lord of the Beach (Azorius Flicker/Combo)
### Apr 17, 2026
1182 9:14a 🔵 DeckFlow Security Audit — Surface Area Scan Findings
1187 9:16a 🔴 Same-Origin Enforcement Added to All API POST Endpoints
1188 " 🔴 Page Snapshot innerHTML Restore Removed — XSS Vector Eliminated
1189 " 🔴 Browser Extension Manifest Scope Narrowed to localhost Only
1190 9:18a 🔴 SameOriginRequestValidator Port Comparison Logic Fixed
1191 " 🟣 Unit Tests Added for SameOriginRequestValidator and Cross-Origin Controller Rejection

Access 579k tokens of past work via get_observations([IDs]) or mem-search skill.
</claude-mem-context>