# DeckSearch

A Slay the Spire 2 mod that adds a search box to the **Deck** screen. Some daily modifiers leave
you with a huge deck. Type part of a card's name and only the matching cards stay on screen.

- **Fuzzy:** typos are forgiven (`defelct` finds Deflect), and initials work (`pstr` finds
  Perfected Strike).
- **Card text too:** `exhaust` or `block` find cards by what they do. Name matches still count
  for more.
- **Keeps your sort:** the Obtained / Type / Cost / A-Z buttons work as normal on the results.
  Clicking a card and paging through the inspect view stays within the results.
- **Keys:** `Ctrl+F` jumps to the box. `Esc` clears it, and a second `Esc` leaves it. `Enter`
  leaves the box and keeps the results.
- **Co-op safe:** it only changes what your screen shows. Nothing is sent to other players.

The box is hidden while you play with a controller, just as the game hides its "View Upgrades"
tickbox.

## Install

Unzip into `Slay the Spire 2/mods/`, so that you end up with `mods/DeckSearch/DeckSearch.dll`.
It also installs through Vortex.

## Settings

Edit `DeckSearch.config.jsonc` in the mod folder. To keep your settings through updates, copy it
to `%APPDATA%\SlayTheSpire2\` instead. That copy is read first. You can set how strict matching
is, whether card text is searched, the focus key, and where the box sits.

With the dev console open (backtick):

- `dsearch reload` applies config changes without restarting.
- `dsearch test <query>` scores your deck against a query.
- `dsearch diag` describes the open Deck screen.

## Licence

MIT, by Tomasapan.
