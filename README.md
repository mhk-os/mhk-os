# mhk-os

Moorhuhn Kart: Open Source Edition is a fan remake of the Moorhuhn Kart games in Unity 6. It brings the tracks, drivers and karts of Moorhuhn Kart 2, Moorhuhn Kart 3, Moorhuhn Kart Thunder and Moorhuhn Kart Extra together in one game with championships, single races, time trials with ghosts, battles and mod support. Every push to main builds the game for Windows, macOS, Linux and Android and publishes it as a GitHub release.

## Battles

Battles are fought on health (100 per kart). Item boxes give random powerups there, among them the battle-only health pack (+25 health), and come back only after 30 seconds (`GameConfig.battleItemBoxRespawnSeconds`) so nobody camps one. Fixed pickups placed in arenas (the Moorhuhn Kart Thunder health, quad damage and hammer) are removed from battles; races keep their item boxes as they are.

## Online

Accounts, global time-trial leaderboards with ghost exchange and real-time races and battles for up to 50 karts (players plus computer drivers that fill free places, simulated on one player's game and streamed to the others like a player's kart) run on the [MHK:OS server](https://github.com/mhk-os/server) (its `docs/protocol.md` describes the protocol). Editor and unstamped builds connect to `tcp://localhost:7350`, release builds to `tls://mhk-os.svit.ac:7350`; `-server <uri>` on the command line or the Server field of the Account page picks another one. Room links have the form `mhkos://join/<code>`.

## Credits

The info icon at the bottom right of the main menu opens Credits: the Moorhuhnverse Discord community for inspiration and valuable ideas that keep the project going, pyramidensurfer for reverse-engineering the original MHK game data and asset formats, MoorBOOST for the Affe, Alien and Tod portraits, and SKevo as author and lead developer of MHK: OS. Join the [Moorhuhnverse Discord community](https://discord.gg/buJ64SrHxY) from that page or this link.

## License

MIT. Game assets belong to their respective owners. If there is a problem with any of them, message me and I will remove it.
