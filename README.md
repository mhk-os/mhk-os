# mhk-os

Moorhuhn Kart: Open Source Edition is a fan remake of the Moorhuhn Kart games in Unity 6. It brings the tracks, drivers and karts of Moorhuhn Kart 2, Moorhuhn Kart 3, Moorhuhn Kart Thunder and Moorhuhn Kart Extra together in one game with championships, single races, time trials with ghosts, battles and mod support. Every push to main builds the game for Windows, macOS, Linux and Android and publishes it as a GitHub release.

## Online

Accounts, global time-trial leaderboards with ghost exchange and real-time races and battles for up to 10 players run on the [MHK:OS server](https://github.com/mhk-os/server) (its `docs/protocol.md` describes the protocol). Editor and unstamped builds connect to `tcp://localhost:7350`, release builds to `tls://mhk-os.svit.ac:7350`; `-server <uri>` on the command line or the Server field of the Account page picks another one. Room links have the form `mhkos://join/<code>`.

## Credits

Thanks to @pyramidensurfer for providing the scripts that extract the data files of the original games, and to the Moorhuhnverse community on Discord.

## License

MIT. Game assets belong to their respective owners. If there is a problem with any of them, message me and I will remove it.
