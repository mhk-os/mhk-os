---
name: changelog
description: Use when writing a player-facing changelog or Discord announcement for one or more Moorhuhn Kart Open Source (MHK OS) builds, including changes to the official mods (MHK 2, MHK 3, MHK Extra, MHK Thunder, TINcan), the asset packs and the online server.
---

# MHK OS changelogs and announcements

Changelogs are posted in Discord, one message per release, written for players. They read like the developer quickly telling the community what's new: a flat list of short headline bullets, casual and a bit playful, without details.

## Find what changed

1. Find the previous announced build. Releases are tagged `build-<sha7>` on GitHub (`https://github.com/mhk-os/mhk-os/releases`) and titled "Build N".
2. Commits on `main` are snapshots of `dev`, not merges, so `git log main..dev` is useless. Find the `dev` commit with the same tree as the previous release: compare `git rev-parse <release-commit>^{tree}` with the trees of recent `dev` commits, then read `git log --format='%h %s%n%b' <that-commit>..dev`.
3. Do the same for every repository that shipped something since: the official mods (`mhk2-mod`, `mhk3-mod`, `mhke-mod`, `mhkt-mod`, `tincan-mod`), the asset packs (`music-asset-pack`) and the server (`server`) in the `mhk-os` GitHub organisation. A mod change reaches players only after that mod is released, so leave out mod changes whose release is not out or planned with this build.
4. Read the bodies of the commits, not only the subjects: a subject lists many changes in compressed form. When a commit is unclear, read the diff.

## What goes in

- The changes a player notices and cares about: new items, tracks, modes, menus and options, gameplay, physics and AI changes, sound and music, online features, the mods page, installers.
- Fixed bugs, phrased as the new behaviour: "respawning no longer dodges the TINcan self guiding missile".
- Only the headline of each change. A feature gets one bullet; its parts get their own bullet only when players see them on their own ("championships got a standings page", "online sets also get a standings page with the set points of everyone in that race").
- Related small changes share one bullet ("softer shadows and better lighting on TINcan tracks"). Minor ones (shortcuts in menus, small AI tweaks, modder tooling) go into "other tweaks and various improvements".

Leave out refactors, tests, CI and build pipeline work, internal tooling, SDK and modder internals, code and protocol details, version and compatibility notes (protocol or content versions, separate lobbies, older builds), commit hashes, class names, file paths, and anything still unreleased. Never claim a fix or feature that is not in the build.

## Style

- No title and no headings: the post starts with the first bullet. No section titles, no Markdown headings, no bold, no numbering.
- One change per bullet: `- ` and the line. Discord renders the dashes as a bullet list.
- Every bullet starts with a lower-case letter and has no trailing period. A line that would begin with a name (MHK 3, Hank, Thunder) is reworded so it doesn't: "the pumpkin grenade now leaves a ring of fire", "brought oil powerup from MHK Thunder". Names inside the line keep their capitals.
- Keep bullets short, mostly 4 to 15 words. Say what changed, not how it works: no numbers, durations, distances, item stats, track lists, menu paths, reasons or parenthesised details. A short second clause is fine when it is part of the change: "the championship results table now shows only the points of that race, the totals moved to the standings page".
- Plain verbs: "you can now ...", "brought back ...", "improved ...", "adjusted ...", "... no longer ...", "... handles failure better". Hedge fixes that can't be fully proven: "should no longer".
- Order roughly by topic (modes and menus, account, items, battles, physics, tracks, sound) so related changes sit together, without titles between them.
- End the list with "- other tweaks and various improvements" (nearly always, since details and minor changes are left out).
- Name things the way players know them: the games as MHK 2, MHK 3, MHK Extra, MHK Thunder or Thunder, TINcan; characters by name (Moorhuhn, Lesshuhn, Kröt, Hank, Moorfrosch, Tod); items by their in-game name (Leapfrog, signal jammer, homing missile, self guiding missile, Shrinker).
- A casual remark or joke is welcome where it fits, at most one or two per post: "brought back the black spinning spike ball monster thingy (srsly what even is that lol) from MHK Extra".
- Links as bare URLs, never Markdown links.
- No emojis.

## Ending

Close with the download line, after an empty line below the list (a plain line, not a bullet), with nothing after the URL:

> You can download the latest MHK: OS build here: https://github.com/mhk-os/mhk-os/releases/latest

No mods-update line, server link or disclaimer paragraph. Only when players lose something or must act beyond updating (accounts or leaderboards wiped, a manual reinstall), add one short plain sentence between the list and the download line. A later fix to an announced build goes into an edit at the end: "Edit: released build 11 which should fix ...".

## Length

A Discord message holds at most 2000 characters, 4000 with Nitro. Count the characters. When the text is longer, shorten or merge bullets first, then split it between bullets into several messages and give each its own code block.

## Output

Give the announcement in a fenced code block (```` ```text ````) so it can be copied as is, with nothing else inside the block; when asked for a file, write the same text to it. Below the block, list in a few short lines what is uncertain: changes you could not verify, mod releases the text depends on.

## Example

```text
- you can now start a whole championship in private online rooms where the owner picks the tracks
- championships got a standings page
- the championship results table now shows only the points of that race, the totals moved to the standings page
- online sets also get a standings page with the set points of everyone in that race
- the account page now shows your playtime
- the pumpkin grenade now leaves a ring of fire
- brought back the black spinning spike ball monster thingy (srsly what even is that lol) from MHK Extra
- brought oil powerup from MHK Thunder
- respawning no longer dodges the TINcan self guiding missile
- 16-ton powerup handles failure better
- battles: bumping into other karts no longer depletes health
- battles: stage hazards like the stompers and crushers on MHK 3 and TINcan tracks now take 10 health per hit
- a kart standing still (or rolling backwards) should no longer rocket forward at insane velocity when another kart rams it from behind
- adjusted MHK Extra track proportions
- improved TINcan AI paths
- softer shadows and better lighting on TINcan tracks
- adjusted music volume for different track music to be more uniform
- other tweaks and various improvements

You can download the latest MHK: OS build here: https://github.com/mhk-os/mhk-os/releases/latest
```
