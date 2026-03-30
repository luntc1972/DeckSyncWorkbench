# Moxfield Tag Exporter

Chrome/Edge Manifest V3 extension that adds export buttons on `moxfield.com/decks/*` pages.

It exports:
- commander
- mainboard
- sideboard
- visible Moxfield card tags from the deck JSON `authorTags` map

It produces both:
- Archidekt-style text: `1 Card Name (set) cn [Tag,Tag]`
- Moxfield-style text: `1 Card Name (set) cn #Tag #Tag`

Notes:
- The export is flattened. It does not preserve Moxfield group headers.
- Commander and sideboard are preserved as tags: `Commander` and `Sideboard`.
- Set code and collector number are included when Moxfield provides them.
- This extension prefers the public deck JSON endpoint and falls back to parsing inline page data if needed.

## Load Unpacked

1. Open `chrome://extensions` or `edge://extensions`.
2. Enable `Developer mode`.
3. Click `Load unpacked`.
4. Select this folder:

`browser-extensions/moxfield-tag-exporter`

## Use

1. Open a public Moxfield deck page.
2. Click one of:
   - `Copy Archidekt Text`
   - `Copy Moxfield Text`
   - `Download Both Files`

If the page was already open before the extension was loaded, reload the deck page once.
