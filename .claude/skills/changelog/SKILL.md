---
name: changelog
description: Use when writing a player-facing changelog or Discord announcement for one or more Moorhuhn Kart Open Source (MHK OS) builds, including changes to the official mods (MHK 2, MHK 3, MHK Extra, MHK Thunder, TINcan), the asset packs and the online server.
---

# MHK OS changelogs and announcements

Changelogs are posted in Discord, one message per release, written for players. They read like a developer telling the community what changed: a list of short plain bullets, concrete names, casual where it fits.

## Find what changed

1. Find the previous announced build. Releases are tagged `build-<sha7>` on GitHub (`https://github.com/mhk-os/mhk-os/releases`) and titled "Build N".
2. Commits on `main` are snapshots of `dev`, not merges, so `git log main..dev` is useless. Find the `dev` commit with the same tree as the previous release: compare `git rev-parse <release-commit>^{tree}` with the trees of recent `dev` commits, then read `git log --format='%h %s%n%b' <that-commit>..dev`.
3. Do the same for every repository that shipped something since: the official mods (`mhk2-mod`, `mhk3-mod`, `mhke-mod`, `mhkt-mod`, `tincan-mod`), the asset packs (`music-asset-pack`) and the server (`server`) in the `mhk-os` GitHub organisation. A mod change reaches players only after that mod is released, so leave out mod changes whose release is not out or planned with this build.
4. Read the bodies of the commits, not only the subjects: a subject lists many changes in compressed form. When a commit is unclear, read the diff.

## What goes in

- Everything a player can see, hear or feel: gameplay, physics, AI, items, tracks, karts, characters, UI, sound, music, controls, online features, mods page, installers and updates.
- Fixed bugs, phrased as the new behaviour: "respawning can no longer dodge the MHK 2 homing missile".
- Things players must act on or know about: leaderboard resets, separate lobbies for different builds, mods that need updating, data that may be wiped, new data the server collects.
- Changes that matter to modders and asset pack makers (new SDK features, templates), in plain words.

Leave out refactors, tests, CI and build pipeline work, internal tooling, code and protocol details, commit hashes, class names, file paths, and anything still unreleased. Never claim a fix or feature that is not in the build.

## Style

- No title and no headings: the post starts with the first bullet. No section titles, no Markdown headings, no bold, no numbering.
- One change per bullet: `- ` and the line. Discord renders the dashes as a bullet list.
- Every bullet starts with a lower-case letter and has no trailing period. A line that would begin with a name (MHK 3, Hank, Thunder) is reworded so it doesn't: "speed pads in MHK 3 no longer ...". Names inside the line keep their capitals.
- Order the bullets by topic (online, racing, items, AI, controls, mods page, fixes) so related changes sit together, without titles between them. Short bullets for small things ("reload button in mod menu"); a bullet may add the reason or effect after a comma and details in parentheses: "added an "auto accelerate" option in settings (default "auto", which means on for touchscreen and off for desktop)".
- When minor changes were left out, end the list with "- other tweaks and various improvements".
- Name things the way players know them: the games as MHK 2, MHK 3, MHK Extra, Thunder, TINcan; characters by name (Moorhuhn, Lesshuhn, Kröt, Hank, Moorfrosch, Tod); items by their in-game name (Leapfrog, signal jammer, homing missile, Shrinker); engine classes as 50, 100 and 150 ccm; screens by their in-game path ("main menu > mods").
- Say what changed and, where it isn't obvious, why it matters to the player: "the falling blocks in MHK Extra tracks now actually fall down, bringing back the one-time use shortcuts like the original game had".
- Short casual remarks are fine where they fit: "(oops!)", "they don't play dumb anymore". Keep them rare.
- Be honest about limits: "it still loses some time on narrow parts", "very early stage".
- Put disclaimers in their own paragraph after the list when online features or data handling change: what may be wiped, what the server stores (IP address, and why), password advice.
- Links as bare URLs, never Markdown links.
- No emojis.

## Ending

Close with the download line, after an empty line below the list (a plain line, not a bullet):

> You can download the latest MHK: OS build here: https://github.com/mhk-os/mhk-os/releases/latest.

When mods or asset packs changed, add: "Mods/content can be updated to latest version directly in-game in main menu > mods." When the server's source matters (new online features), link `https://github.com/mhk-os/server`. A later fix to an announced build goes into an edit at the end: "Edit: released build 11 which should fix ...".

## Length

A Discord message holds at most 2000 characters, 4000 with Nitro. Count the characters. When the text is longer, shorten bullets first (drop explanations that add nothing), then split it between bullets into several messages and give each its own code block.

## Output

Give the announcement in a fenced code block (```` ```text ````) so it can be copied as is, with nothing else inside the block. Below the block, list in a few short lines what is uncertain: an assumed build number, changes you could not verify, mod releases the text depends on.

## Example

```text
- the default kart of Lesshuhn and Kröt now comes from MHK 2 (because there was a bug where they would be floating in mid air when the mod the kart is from was not installed)
- you can now open the options menu when in race, not just from main menu
- on higher ccms, the AI will now actively try to target shrunk down karts and run them over (50 % chance on 100 ccm, 100 % chance on 150 ccm)
- swapped Kröt's and Hank's honk sound to match MHK 2 (oops!)
- stage hazards like the MHK 3 stompers are now deterministic, so in time trial mode they trigger in the same order every run

You can download the latest MHK: OS build here: https://github.com/mhk-os/mhk-os/releases/latest. Mods/content can be updated to latest version directly in-game in main menu > mods.
```
