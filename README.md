# DeckSyncWorkbench

DeckSyncWorkbench helps commanders translate decks between Moxfield and Archidekt without manual editing. The CLI and web projects both rely on a shared core that parses exports, compares quantities, flags printing conflicts, and generates delta imports, full exports, and category suggestions.

## Highlights
- `DeckSyncWorkbench.Core` contains parsers, diffing logic, exporters, and the Archidekt/Moxfield integrations.
- `DeckSyncWorkbench.Web` provides an ASP.NET Core MVC UI for running syncs, viewing comparisons, and suggesting categories/tags with caching support.
- `DeckSyncWorkbench.CLI` exposes the same functionality in a console tool so you can script imports or harvest Archidekt decks for the category cache.

## Getting Started
1. Restore/build: `dotnet build DeckSyncWorkbench.sln`
2. Run the web app: `dotnet run --project DeckSyncWorkbench.Web`
3. Use the CLI to compare or harvest decks: `dotnet run --project DeckSyncWorkbench.CLI -- --help`

## Architecture
- Core logic is isolated in `DeckSyncWorkbench.Core` (diff engine, export helpers, parsers, integration clients, knowledge store).
- Web and CLI layers orchestrate requests and rely on DI to resolve the shared services (deck sync, knowledge store, importers).
- Importers for Archidekt/Moxfield now implement typed interfaces so the service can swap implementations easily during testing.
