# mhk-os

Moorhuhn Kart: Open Source Edition is a fan remake of the Moorhuhn Kart games in Unity 6. It brings the tracks, drivers and karts of Moorhuhn Kart 2, Moorhuhn Kart 3, Moorhuhn Kart Thunder and Moorhuhn Kart Extra together in one game with championships, single races, time trials with ghosts, battles and mod support. Every push to main builds the game for Windows, macOS, Linux and Android and publishes it as a GitHub release.

## Championships

Championship results have a separate Standings page with total points, points earned in the last race and places gained or lost. During later races, Pause → Standings shows the totals before the current race. Online set results also offer standings for the racers in that race.

## Items and track proportions

Pumpkin's grenade leaves a ground-following fire ring that spreads to 11 m and burns karts it reaches. Self-guiding missiles wait for a respawning target's protection to end rather than being wasted on it. MHK Extra adds the Fireball (Feuerkugel), a rolling spiked ball that hunts the leader; MHK Thunder adds Oil, a dropped slick that lowers grip for 5 seconds. MHK Extra's 16-ton weight stops above a shielded or protected target, hovers there and goes back up with its fail sound, without the earthquake.

MHK Extra's tracks use one original-unit-to-kart-size conversion instead of the two conflicting import scales. Outdoor, Scribble Blocks, Kart Track, Moon and Docklands are enlarged by 1.495; Desert, Canyon, Ice, Volcano and Graveyard are reduced by 0.748. MHK2 tracks are unchanged. Content version 7 separates the changed lap times from previous records and ghosts.

## Battles

Battles are fought on health (100 per kart). Only powerups and the MHK3 and TINcan stage hazards cost health: being squashed by a stomper, crusher or rolling boulder takes 10 (`GameConfig.defaultHitDamage`); running into other karts takes nothing. Item boxes give random powerups there, among them the battle-only health pack (+25 health), and come back only after 30 seconds (`GameConfig.battleItemBoxRespawnSeconds`) so nobody camps one. Fixed pickups placed in arenas (the Moorhuhn Kart Thunder health, quad damage and hammer) are removed from battles; races keep their item boxes as they are.

## Online

Accounts, global time-trial leaderboards with ghost exchange and real-time races and battles for up to 50 karts (players plus computer drivers that fill free places, simulated on one player's game and streamed to the others like a player's kart) run on the [MHK:OS server](https://github.com/mhk-os/server) (its `docs/protocol.md` describes the protocol). Editor and unstamped builds connect to `tcp://localhost:7350`, release builds to `tls://mhk-os.svit.ac:7350`; `-server <uri>` on the command line or the Server field of the Account page picks another one. Room links have the form `mhkos://join/<code>`.

In private Races rooms where the owner picks tracks, the host can choose a championship cup as the next set. Its tracks run in order, then normal host picking resumes. This requires client and server protocol 4; update both together.

The Account page shows total foreground playtime, including menus, synced across devices through account statistics. Background time is excluded; progress is saved periodically and when the app pauses, loses focus or quits. Playtime starts accumulating with this feature, not retroactively.

## Audio

Music files, including menu and results tracks, are loudness-matched to −16 LUFS. Sound-effect sets are level-matched by source game while retaining their intentional relative levels; per-cue volume and distance attenuation remain in effect.

## Credits

The info icon at the bottom right of the main menu opens Credits: the Moorhuhnverse Discord community for inspiration and valuable ideas that keep the project going, pyramidensurfer for reverse-engineering the original MHK game data and asset formats, MoorBOOST for the Affe, Alien and Tod portraits, and SKevo as author and lead developer of MHK: OS. Join the [Moorhuhnverse Discord community](https://discord.gg/buJ64SrHxY) from that page or this link.

## License

MIT. Game assets belong to their respective owners. If there is a problem with any of them, message me and I will remove it.
