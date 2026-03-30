# MTG Deck Studio

MTG Deck Studio helps deck builders translate decks between Moxfield and Archidekt without manual editing. The CLI and web projects both rely on a shared core that parses exports, compares quantities, flags printing conflicts, and generates delta imports, full exports, and cache-backed category suggestions.

**Repository description (≤350 characters):** MTG Deck Studio unifies Moxfield and Archidekt decks, harvests Archidekt category data, and exposes CLI/web tools for diffs, printing conflict reports, card/mechanic lookup, and cache-backed category suggestions.

## Highlights
- `MtgDeckStudio.Core` contains parsers, diffing logic, exporters, and the Archidekt/Moxfield integrations.
- `MtgDeckStudio.Web` provides an ASP.NET Core MVC UI for running syncs, viewing comparisons, and suggesting categories/tags with caching support.
- `MtgDeckStudio.CLI` exposes the same functionality in a console tool so you can script imports or harvest Archidekt decks for the category cache.
- The Commander Categories page surfaces the Archidekt categories that show up most frequently in decks where the queried card is listed as the commander; it does not try to re-label cards, it just reports the tags observers assigned when that card led the deck.

## Getting Started
1. Restore/build: `dotnet build MtgDeckStudio.sln` (Web may still fail locally if the SDK resolver cannot resolve `Microsoft.NET.SDK.WorkloadAutoImportPropsLocator`; the CLI binary builds independently.)
2. Run the web app: `dotnet run --project MtgDeckStudio.Web`
3. Use the CLI to compare or harvest decks: `dotnet run --project MtgDeckStudio.CLI -- --help`

## Archidekt category cache
- Run `dotnet run --project MtgDeckStudio.CLI -- archidekt-cache --minutes 5` to keep the local cache fed with the latest public decks. The CLI now runs a dedicated cache session that respects rate limits (via Polly), records skips for noisy decks, and persists card/category observations to `artifacts/category-knowledge.db`.
- The web cache service now reuses the same session logic, so the MVC tools can refresh a few decks on demand or run longer sweeps with `CategoryKnowledgeStore.RunCacheSweepAsync`.

## Web API
- Swagger UI is available at `/swagger` when the web app is running.
- `POST /api/suggestions/card` looks up a single card, runs the bounded Archidekt cache sweep used by the UI, and returns exact reference-deck categories, cached matches, and EDHREC fallback themes.
- `POST /api/suggestions/commander` looks up a commander and returns the most common Archidekt categories seen on decks where that card is recorded as commander.

## Scryfall usage
- Scryfall is used in three places: card-name autocomplete, commander autocomplete, and the `Card Lookup` page.
- All Scryfall clients send a real `User-Agent`, an explicit `Accept` header, and use `https`.
- `Card Lookup` uses `POST /cards/collection` in batches of `75` identifiers instead of one live request per card.
- The Card Lookup page is capped at `100` non-empty input lines per submission, so it results in at most two live Scryfall collection requests.

### Card suggestion example
```json
POST /api/suggestions/card
Content-Type: application/json

{
  "mode": "CachedData",
  "archidektInputSource": "PublicUrl",
  "archidektUrl": "",
  "archidektText": "",
  "cardName": "Guardian Project"
}
```

### Commander category example
```json
POST /api/suggestions/commander
Content-Type: application/json

{
  "commanderName": "Bello, Bard of the Brambles"
}
```

### cURL examples
```bash
curl -X POST http://localhost:5000/api/suggestions/card \
  -H "Content-Type: application/json" \
  -d '{"mode":"CachedData","archidektInputSource":"PublicUrl","cardName":"Guardian Project"}'

curl -X POST http://localhost:5000/api/suggestions/commander \
  -H "Content-Type: application/json" \
  -d '{"commanderName":"Bello, Bard of the Brambles"}'
```

## CLI usage examples
- `dotnet run --project MtgDeckStudio.CLI -- compare --moxfield my.deck --archidekt other.deck --out diff.txt`
- `dotnet run --project MtgDeckStudio.CLI -- archidekt-cache --minutes 10` (harvest for ten minutes)
- `dotnet run --project MtgDeckStudio.CLI -- category-find --card "Guardian Project" --cache-seconds 20`


## Architecture
- Core logic is isolated in `MtgDeckStudio.Core` (diff engine, export helpers, parsers, integration clients, knowledge store).
- Web and CLI layers orchestrate requests and rely on DI to resolve the shared services (deck sync, knowledge store, importers).
- Importers for Archidekt/Moxfield now implement typed interfaces so the service can swap implementations easily during testing.
