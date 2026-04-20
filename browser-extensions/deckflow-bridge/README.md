# DeckFlow Bridge

Chrome/Edge Manifest V3 extension that lets DeckFlow fetch Moxfield decks through your logged-in browser session when the server cannot reach them directly.

The bridge only responds on DeckFlow origins you explicitly allow in the extension's Options page.

## Load Unpacked

1. Open `chrome://extensions` or `edge://extensions`.
2. Enable `Developer mode`.
3. Click `Load unpacked`.
4. Select this folder:

`browser-extensions/deckflow-bridge`

## Options

1. Open the extension details page.
2. Open `Extension options`.
3. Add each DeckFlow origin you trust to the allowed-origins list.

After that, return to DeckFlow and retry the Moxfield URL import.
