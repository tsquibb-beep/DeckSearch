# DeckSearch

A Slay the Spire 2 mod that adds a search box to the **Deck** screen, to the screens where you
pick cards from your deck, and to your draw, discard and exhaust piles in combat. Some daily modifiers leave you with a huge deck. Type part of a card's
name and only the matching cards stay on screen.

The box sits on the sort bar next to the sort buttons and matches their style. It takes on your
character's colour.

- **Fuzzy:** typos are forgiven (`defelct` finds Deflect), and initials work (`pstr` finds
  Perfected Strike).
- **Card text too:** `exhaust` or `block` find cards by what they do. Name matches still count
  for more.
- **Type and rarity:** `attack`, `skill`, `power`, `curse`, `status`, and `basic`, `common`,
  `uncommon`, `rare` show every card of that kind. Combine them: `rare power`, `skill block`.
  They match the whole word only, so `com` will not pull in every Common card while you type.
- **Type or rarity only:** start a word with `#` to match just the card's type or rarity, not its
  text. `#attack` shows your Attacks but not a Skill that mentions attacks. `#skill #rare`,
  `#attack strike` and a part-typed `#att` all work. `#upgraded` (or just `#up`) shows your
  upgraded cards.
- **Keeps your sort:** the Obtained / Type / Cost / A-Z buttons work as normal on the results.
  Clicking a card and paging through the inspect view stays within the results.
- **Card pickers too:** the screens that ask you to upgrade, remove, transform or enchant cards
  from your deck (campfire, events, relics) get the same bar: sort buttons and the search box.
  Cards you have already picked stay picked while you search or sort.
- **Combat piles too:** the draw, discard and exhaust piles get the bar as well. Search finds
  what a card has gained in combat, such as Replay from Hidden Gem. The draw pile still never
  shows the order you will draw in.
- **Just type:** start typing on any of these screens and it goes straight into the box, no
  click needed. The view scrolls back to the top. `Esc` clears the search, a second `Esc` closes
  the screen. `Enter` leaves the box and keeps the results. While a screen is open, the game's
  letter keys (D, A, S, X...) type into the box instead of closing it. Turn this off with
  `typeToSearch`, and add `Ctrl+F` back with `focusShortcuts` if you prefer a key.
- **Co-op safe:** it only changes what your screen shows. Nothing is sent to other players.

The box is hidden while you play with a controller, just as the game hides its "View Upgrades"
tickbox.

## Install

Unzip into `Slay the Spire 2/mods/`, so that you end up with `mods/DeckSearch/DeckSearch.dll`.
It also installs through Vortex.

## Settings

Edit `DeckSearch.config.jsonc` in the mod folder. To keep your settings through updates, copy it
to `%APPDATA%\SlayTheSpire2\` instead. That copy is read first. You can set how strict matching
is, whether card text is searched, type-to-search, a focus key, where the box sits, and whether the card
pickers (`pickScreens`) and combat piles (`pileScreens`) get the bar.

With the dev console open (backtick):

- `dsearch reload` applies config changes without restarting.
- `dsearch test <query>` scores your deck against a query.
- `dsearch diag` describes the open Deck screen, card picker or pile.

## Licence

MIT, by Tomasapan.
