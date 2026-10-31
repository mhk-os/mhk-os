using System;
using System.Collections.Generic;
using MHK.Karts;
using MHK.Modding;
using UnityEngine;

namespace MHK.Core
{
    /// <summary>Global, non-content gameplay configuration. Lives at Resources/GameConfig.</summary>
    [CreateAssetMenu(menuName = "MHK/Game Config", fileName = "GameConfig")]
    public class GameConfig : ScriptableObject
    {
        [Header("Prefabs")]
        [Tooltip("Gameplay kart (physics, effects, inventory, audio) without visuals. Visuals are attached from content.")]
        public GameObject kartRootPrefab;
        [Tooltip("Physics of karts without their own profile, also the reference kart for the engine classes' top speeds.")]
        public KartPhysicsProfile defaultPhysics;
        [Tooltip("Online races: sign (Prefabs/UnknownContent/UnknownContentMarker) above a remote kart whose character or " +
                 "kart is not installed here, so the stand-in driver is not mistaken for someone else's.")]
        public GameObject unknownContentMarker;

        [Header("Loading")]
        [Tooltip("Loading screen (Prefabs/UI/LoadingScreen): spawned once under the GameRoot, SceneFlow shows it while scenes load.")]
        public UI.LoadingScreen loadingScreen;

        [Header("Branding")]
        [Tooltip("The game's own logo on the title and loading screens (Branding/GameLogo, made by " +
                 "mhk-work/tools/gen_logo.py). Pack logos only show in the mods list. Empty: the title screen shows " +
                 "the game's name as text and the loading screen shows no logo.")]
        public Texture2D gameLogo;

        [Header("User interface")]
        [Tooltip("Hidden/MHK/UiGlassBlur: blurs the camera picture once per frame for the frosted glass plates of the " +
                 "menus (UiGlassBlur). Empty: the plates are plain gray.")]
        public Shader uiGlassShader;

        [Header("Rendering")]
        [Tooltip("MHK/Lit as built with the game. World sky domes (WorldSkyDome) switch to it, so their fog can be turned " +
                 "off also when a mod bundle carries an older copy of the shader. Empty: they keep their own shader.")]
        public Shader litShader;

        [Header("Audio mix")]
        [Tooltip("Gain of every sound effect (engines, skids, surfaces, collisions, voices, items, hazards, ambience and " +
                 "prop loops, UI clicks, also AudioSources placed in scenes or prefabs without code) on top of the " +
                 "player's master × effects volume settings. Applied through AudioListener.volume, so every source that " +
                 "is not music follows it.")]
        [Range(0f, 1f)] public float sfxMixGain = 0.4f;
        [Tooltip("Gain of the music (menu and track music, × the track's own musicVolume) on top of the player's master × " +
                 "music volume settings.")]
        [Range(0f, 1f)] public float musicMixGain = 0.6f;

        [Header("Simulation")]
        [Tooltip("Physics ticks per second (kart simulation is tuned for this rate).")]
        public int physicsHz = 60;
        [Tooltip("World gravity during races (MHK2: 19.62 = 2 g).")]
        public float gravity = 19.62f;
        [Tooltip("Quadratic drag (MHK2 'material resistance') of every offroad ground (TrackSurface.offroad: grass, sand, " +
                 "mud, snow beside the road) in every game. Road surfaces have none, so every road has the same top " +
                 "speed and every offroad the same slowdown. Scale it with driveTorqueScale to keep the slowdown: 0.3 at " +
                 "torque × 2 leaves offroad top speeds at 60 to 70 % of road speed, as MHK2's 0.15 at torque × 1.")]
        public float offroadResistance = 0.3f;
        [Tooltip("Remake: how quickly (1/s) a kart with no wheel on the ground turns its nose towards its direction of " +
                 "travel when the steering is let go, so after a jump or crest it lands straight instead of keeping the " +
                 "spin it took off with and sliding on landing (worst at high speed, where flights are long). While the " +
                 "steering is held the kart turns in the air like in MHK2 (the extra steering torque), at full lock " +
                 "without this pull. 0 = off (MHK2).")]
        [Min(0f)] public float airHeadingAlign = 2.5f;
        [Tooltip("Remake: how fast (degrees per second at full lock) the steering turns the direction of travel of a kart " +
                 "with no wheel on the ground, so it can correct its course a little during a jump. The speed stays the " +
                 "same. 0 = off (MHK2: the steering only turns the kart's nose in the air, not its course).")]
        [Min(0f)] public float airSteerRate = 25f;
        [Tooltip("Remake: multiplies every kart's engine drive torque in every engine class, so karts pick up speed and " +
                 "approach their top speed sooner (1 = MHK2, 2 halves the time to 90 % of top speed). Top speeds stay at " +
                 "the classes' topSpeedKmh: the speed scale is solved with it. Engine braking and traction limits are " +
                 "unchanged. Offroad slows less unless offroadResistance grows with it.")]
        [Min(0.1f)] public float driveTorqueScale = 2f;
        [Tooltip("Masses of loose track props (non-kinematic rigidbodies), set when a track loads (MHK.Race.PropMass): the " +
                 "bounding volume of their colliders × their material's density, between shares of defaultPhysics' mass. " +
                 "A kart hitting or pushing one loses speed by the mass ratio, so these change lap times.")]
        public Race.PropMassSettings propMass = new();

        [Header("Kart visuals")]
        [Tooltip("Turn pose: how the driver and kart lean into a bend (the original turn animations). Smoothed and " +
                 "partly driven by the kart's actual turning, so it follows what the kart does instead of snapping with " +
                 "the steering key.")]
        public KartDriverAnimation.TurnPoseSettings kartTurnPose = new();
        [Tooltip("Spin-out of effects that spin (StatusEffectDefinition.Spins): the kart model turns this many full times, " +
                 "fast at first and settling facing forward. Visual only, the kart's heading and motion are unchanged.")]
        [Min(1)] public int spinTurns = 5;
        [Tooltip("Length of the spin-out (s), however long the effect lasts. A spinning hit during a spin starts a new " +
                 "one from the current angle.")]
        [Min(0.1f)] public float spinSeconds = 3f;

        [Header("Race (defaults = Moorhuhn Kart 2 rules)")]
        [Tooltip("Countdown length. MHK2: 3.999 s (1 s wait, then the 3-2-1 beeps, GO at 0).")]
        public float countdownSeconds = 3.999f;
        [Tooltip("Countdown time window (seconds left) in which the first throttle press gives a power start.")]
        public Vector2 powerStartWindow = new(0.8f, 1.2f);
        [Tooltip("Throttle presses while more than this much countdown is left are ignored.")]
        public float powerStartIgnoreAbove = 2.7f;
        public StatusEffectDefinition powerStartEffect;
        public StatusEffectDefinition powerFailEffect;
        [Tooltip("Karts in a local race at most: the player plus up to maxRacers - 1 computer opponents.")]
        public int maxRacers = 51;
        [Tooltip("Championship points by finishing position (index 0 = winner). Places beyond the table get 0.")]
        public int[] championshipPoints = { 10, 8, 6, 4, 2, 1, 0 };
        [Tooltip("Seconds after the human players finish before the race ends for everybody.")]
        public float finishGraceSeconds = 4f;
        [Tooltip("Seconds facing backwards before 'wrong way' shows (MHK2: 2).")]
        public float wrongWaySeconds = 2f;

        [Header("Items")]
        public float itemBoxRespawnSeconds = 3f;
        [Tooltip("Picking up a box while holding an item replaces it (MHK2). Otherwise full karts skip boxes.")]
        public bool itemBoxReplacesHeld = true;
        [Tooltip("Using any item ends the active exclusive buff (MHK2).")]
        public bool usingItemCancelsBuffs = true;
        [Tooltip("Item roulette duration after pickup (MHK2: none).")]
        public float itemRouletteSeconds;
        [Tooltip("Seconds after placing / throwing a hazard (trap, mine, x-field, black hole, glibber ball, charge...) during " +
                 "which it spares its owner, afterwards it hits the owner too (MHK2 traps do). Other karts are hit at once.")]
        [Min(0f)] public float ownerHazardImmunity = 1f;

        [Header("Respawn")]
        [Tooltip("Applied when touching 'drop' geometry (fall animation, then respawn).")]
        public StatusEffectDefinition dropEffect;
        [Tooltip("Respawn this far behind the last safe point (MHK2: 15).")]
        public float respawnBackDistance = 15f;
        [Tooltip("Road (m) a kart needs before a gap (a jump over a fall zone) to reach take-off speed: respawns are moved " +
                 "back to leave at least this much, and computer drivers go full throttle from here on. At the scaled " +
                 "speeds and the original 2 g the MHK2 river bridge is a full-throttle jump (50cc: 16-17 m/s at the lip " +
                 "for a 3-3.5 m flight). The original's 15 m respawn run-up and AI throttle had ~3× the margin.")]
        public float gapRunUp = 60f;
        [Tooltip("Extra drop height above the ground at respawn.")]
        public float respawnDropHeight = 1.4f;
        [Tooltip("Respawn spot occupied by another kart: move this much further back (MHK3: 3 AI points).")]
        public float respawnOccupiedStep = 4f;
        [Tooltip("Another kart closer than this to a respawn spot makes it occupied (MHK3: 1.2 units).")]
        public float respawnClearRadius = 2.5f;
        public float respawnProtectionSeconds = 0.7f;

        [Header("Health / Battle (Thunder)")]
        public int maxHealth = 100;
        [Tooltip("Damage of hostile effects inflicted by another kart unless the effect sets its own (Thunder: 10).")]
        public int defaultHitDamage = 10;
        [Tooltip("Ramming damage in battles (Thunder hit 18: 5).")]
        public int ramDamage = 5;
        [Tooltip("A kart can be rammed at most once per this many seconds (Thunder: 3).")]
        public float ramCooldown = 3f;
        [Tooltip("Rammer must be faster than this (original m/s, × the kart's SpeedScale, Thunder 1 unit/s).")]
        public float ramMinSpeed = 0.7f;
        [Tooltip("Victim must lie within this angle of the rammer's forward vector (Thunder: 45).")]
        public float ramConeDegrees = 45f;
        [Tooltip("Seconds between the last elimination and the results screen.")]
        public float eliminationGraceSeconds = 3f;

        [Header("Time trial ghosts")]
        [Tooltip("Opacity of the ghosts raced against (the see-through look of a ghost-ride kart, 1 = opaque).")]
        [Range(0.05f, 1f)] public float ghostOpacity = 0.45f;
        [Tooltip("Most ghosts in one time trial (the own best plus picked ones, local or downloaded).")]
        [Min(1)] public int maxGhosts = 50;
        [Tooltip("Raced ghosts nearest the camera that animate, show effect visuals and name tags; the others only " +
                 "drive (the split reference and a watched ghost always count as near).")]
        [Min(1)] public int ghostDetailCount = 8;
        [Tooltip("Seconds a ghost keeps recording after the finish line (it coasts on, then fades out).")]
        [Min(0f)] public float ghostTailSeconds = 3f;
        [Tooltip("Ghost name tags show up to this distance from the camera (m), 0 = no name tags.")]
        [Min(0f)] public float ghostTagDistance = 80f;
        [Tooltip("Minimap ring of a ghost.")]
        public Color ghostMinimapColor = new(0.75f, 0.92f, 1f, 0.95f);
        [Tooltip("Minimap ring size of a raced ghost (1 = a kart's dot).")]
        [Min(0f)] public float ghostMinimapSize = 0.85f;
        [Tooltip("Minimap dot of the ghost watched in a replay.")]
        public Color ghostSpectatedMinimapColor = new(0.2f, 0.92f, 0.25f, 1f);
        [Tooltip("Seconds after its recording ends before a raced ghost fades out.")]
        [Min(0f)] public float ghostFadeDelaySeconds = 0.5f;
        [Tooltip("Seconds a replay keeps running after the watched ghost's last tick.")]
        [Min(0f)] public float replayEndSeconds = 1f;

        [Header("Difficulty")]
        public DifficultySettings[] difficulties =
        {
            new() { label = "50cc", engineClassFactor = 1.1f, topSpeedKmh = 85f, aiPrecision = 0.55f },
            new() { label = "100cc", engineClassFactor = 1.3f, topSpeedKmh = 95f, aiPrecision = 0.75f },
            new() { label = "150cc", engineClassFactor = 1.5f, topSpeedKmh = 105f, aiPrecision = 0.95f },
        };

        [Header("AI recovery")]
        [Tooltip("Respawn when a kart is this many seconds stuck/off track.")]
        public float stuckRespawnSeconds = 4f;
        [Tooltip("An AI kart whose race distance grows by less than this (m) within stuckRespawnSeconds respawns " +
                 "(blocked against a wall, wedged, facing the wrong way). Not during countdown, finish or effects.")]
        public float aiStuckMinProgress = 6f;
        [Tooltip("An AI kart farther than this (m) outside the road of the racing line or any alternative route for " +
                 "stuckRespawnSeconds respawns.")]
        public float aiOffRouteDistance = 10f;

        [Header("AI driving")]
        [Tooltip("Furthest distance (m) computer drivers look ahead for walls and solid props, item boxes, planted traps " +
                 "and speed pads.")]
        public float aiLookAheadRange = 35f;
        [Tooltip("The wall / solid prop scan covers this many seconds of driving (at least 10 m, at most aiLookAheadRange).")]
        public float aiObstacleLookSeconds = 1.3f;
        [Tooltip("Deceleration (m/s²) computer drivers plan with for walls or solid bodies blocking every lane, karts they " +
                 "follow and stops in battles (bends use aiBrakeGrip). Full brakes manage 15-25 m/s². The margin covers " +
                 "late reactions.")]
        public float aiPlanBraking = 7f;
        [Tooltip("Computer drivers only brake for bends tighter than the kart's full-lock turn radius at its speed (steering " +
                 "angle washed out with speed, or sliding when the grip runs out) times this: the rest is taken at full " +
                 "throttle, like players take every bend up to about 90°.")]
        public float aiTurnRadiusMargin = 1f;
        [Tooltip("aiTurnRadiusMargin is multiplied by up to this for imprecise drivers (DifficultySettings.aiPrecision 0, " +
                 "1 at precision 1): lower engine classes brake earlier.")]
        public float aiImpreciseTurnMargin = 1.5f;
        [Tooltip("Share of the kart's lateral grip (rear tyre friction × grip effects × (gravity + downforce)) computer " +
                 "drivers plan to use in bends. The bend limit is the larger of the sliding radius and the full-lock radius.")]
        public float aiCornerGrip = 1.05f;
        [Tooltip("Share of the kart's full braking (brake friction of its wheels × (gravity + downforce), so stronger at " +
                 "speed) computer drivers plan with before bends: they brake as late as this allows.")]
        [Range(0.1f, 1f)] public float aiBrakeGrip = 0.8f;
        [Tooltip("Computer drivers follow a racing line: the line with the evenest curvature within the road (the track " +
                 "line's width, cut short at walls, offroad ground and missing ground), out, in to the apex and out again, " +
                 "keeping this far (m) from the road's edges.")]
        public float aiRacingLineMargin = 1.2f;
        [Tooltip("Chance a computer driver takes an alternative route / shortcut, rolled per route and lap when it nears " +
                 "the fork, the same in every engine class. In battles routes with a gap or a drop are only taken along " +
                 "the track's direction.")]
        [Range(0, 1)] public float aiRouteChance = 0.5f;
        [Tooltip("Loose props (dynamic rigidbodies) up to this mass (kg, see propMass) are pushed through, not braked for: " +
                 "computer drivers steer around them when there is room, else plough through at speed (straw bales, " +
                 "cones, cartons, crates, barrels, mailboxes, hydrants, small rocks). Heavier ones (rocks and boulders) and " +
                 "kinematic bodies count as solid.")]
        public float aiPushableMass = 15f;
        [Tooltip("Computer drivers press the player's keys (steering, throttle, brake: MHK.AI.AIKeys) and change a key at " +
                 "most this often (s): part steering or part throttle is tapped like a quick player does (0.05 s ≈ 10 " +
                 "taps a second).")]
        public float aiKeyMinHold = 0.05f;
        [Tooltip("Computer drivers steer towards the racing line averaged over this share of their look-ahead distance " +
                 "(around the target point): node kinks and road-width steps don't make them twitch.")]
        public float aiSteerSmoothing = 0.3f;
        [Tooltip("Heading error (degrees) computer drivers accept without steering when they steer towards a point off the " +
                 "racing line (battles): below it the wheel stays straight. On the racing line they set the wheel for the " +
                 "line's bend plus the arc back onto their lane, from the kart's steering geometry.")]
        public float aiSteerTolerance = 3f;
        [Tooltip("Once correcting, they steer until the heading error is back within this (degrees).")]
        public float aiSteerRelease = 0.75f;
        [Tooltip("Heading error (degrees, beyond the release band) at which they steer at full lock, less is tapped.")]
        public float aiSteerFullLockError = 6f;
        [Tooltip("Computer drivers judge their heading this far ahead in time (s) from how much faster or slower the kart " +
                 "turns than the arc to their target needs, so they let go of the key before overshooting (the wheel " +
                 "needs time to come back) but hold it through a bend.")]
        public float aiSteerDamping = 0.25f;
        [Tooltip("Slide control: while the kart's nose points more than this (degrees) into the turn from its direction of " +
                 "travel (the rear breaking away on snow or ice), computer drivers don't steer further into the turn. " +
                 "Karts drift 7-9° at the grip limit on MHK2 Winter's snow.")]
        public float aiSlideAngle = 11f;
        [Tooltip("Lane wishes closer than this (m) to the current lane are ignored (except to dodge walls, solid props, " +
                 "traps or other karts), so computer drivers hold their line instead of hopping between near-equal lanes.")]
        public float aiLaneTolerance = 0.8f;
        [Tooltip("Sideways speed (m/s) at which computer drivers move to a new lane (±20 % with precision).")]
        public float aiLaneChangeSpeed = 3f;
        [Tooltip("...times this when dodging walls, solid props or traps, or a kart they are about to hit.")]
        public float aiLaneUrgentFactor = 2.5f;
        [Tooltip("Computer drivers pick a passing side for a slower or blocking kart ahead this many seconds before they " +
                 "would reach it (at their closing speed, within aiLookAheadRange), on the side with free road and no " +
                 "wall, trap or other kart in the way, and drive past it at full speed. Only with no side free do they " +
                 "lift or brake to its speed. Karts they can drive through (ghosts, flattened or shrunk karts) are not passed.")]
        public float aiPassLookSeconds = 2.5f;
        [Tooltip("Side gap (m) computer drivers keep between their kart and a kart they pass or drive beside. They cut back " +
                 "in only once they are clear ahead of the kart they passed.")]
        public float aiPassSideGap = 0.5f;

        [Header("AI items")]
        [Tooltip("Computer drivers hold a picked-up item at least this long (s) before using it, unless the next item box " +
                 "cluster comes sooner or a protecting item is needed against a homing missile.")]
        public float aiItemMinHoldSeconds = 3f;
        [Tooltip("Computer drivers use their item at the latest this far (m along the track) before the next item box " +
                 "cluster, so they always arrive with empty hands: aimed if a target is there, else blind (traps behind, " +
                 "shots ahead, self effects at once). Items with several charges start earlier by their cooldowns.")]
        public float aiItemUseBeforeBoxes = 15f;
        [Tooltip("Item boxes closer than this (m along the main track line) belong to one cluster for the rule above.")]
        public float aiItemBoxClusterGap = 25f;

        [Header("AI battle")]
        [Tooltip("Battle skill of computer drivers per engine class (50cc, 100cc, 150cc).")]
        public BattleAiSettings[] battleAi =
        {
            new() { decisionInterval = 0.8f, huntRange = 35f, ramRange = 8f, aggression = 0.35f, aimLead = 0.3f },
            new() { decisionInterval = 0.5f, huntRange = 45f, ramRange = 12f, aggression = 0.6f, aimLead = 0.65f },
            new() { decisionInterval = 0.3f, huntRange = 55f, ramRange = 15f, aggression = 0.85f, aimLead = 1f },
        };
        [Tooltip("On circuits computer drivers hunting an opponent in a battle roam up to this far (m) beyond the road " +
                 "edges of the racing line and its alternative routes, offroad included, but never onto walls, fall zones, " +
                 "deadly ground, water or below the kill height.")]
        public float aiBattleOffroadLeash = 8f;
        [Tooltip("The same reach (m) beyond the road edges of an arena's line, whose floor is wider than its line.")]
        public float aiBattleArenaLeash = 40f;
        [Tooltip("Distance (m) computer drivers in a battle check ahead for walls, solid props and unsafe ground in each " +
                 "direction they consider while they hunt, roam an arena or turn around.")]
        public float aiBattleLookRange = 22f;
        [Tooltip("Opponents closer than this (m) are hunted across the ground, farther ones along the racing line " +
                 "(forwards or backwards, whichever is shorter), ones on an alternative route along that route. Arenas " +
                 "are always crossed directly.")]
        public float aiBattleDirectRange = 45f;
        [Tooltip("In arenas computer drivers without an item box to drive to roam to open spots between this distance (m) " +
                 "and twice it ahead of them.")]
        public float aiBattleWanderRadius = 30f;
        [Tooltip("Share of the top speed computer drivers in a battle slow to for turns sharper than about 60 degrees.")]
        [Range(0.1f, 1f)] public float aiBattleTurnSpeedShare = 0.5f;
        [Tooltip("Steepest offroad ground (degrees) computer drivers in a battle drive onto. Steeper hillsides beside the " +
                 "road are left alone (karts cannot climb them), the road of the racing line is always allowed.")]
        [Range(5f, 50f)] public float aiBattleMaxSlope = 20f;
        [Tooltip("Computer drivers in a battle use an item at the latest after holding it this long (s), so their hands " +
                 "are free for the next item box.")]
        [Min(1f)] public float aiBattleItemMaxHoldSeconds = 15f;

        [NonSerialized] float[] speedScaleCache;

        /// <summary>The race-start and drop effects, in this order: power start, power fail, drop.</summary>
        public IEnumerable<StatusEffectDefinition> RaceEffects
        {
            get
            {
                yield return powerStartEffect;
                yield return powerFailEffect;
                yield return dropEffect;
            }
        }

        public DifficultySettings GetDifficulty(Difficulty d) => difficulties[DifficultyIndex(d)];

        /// <summary>Battle skill of computer drivers in engine class <paramref name="d"/>.</summary>
        public BattleAiSettings GetBattleAi(Difficulty d) =>
            battleAi != null && battleAi.Length > 0 ? battleAi[Mathf.Clamp((int)d, 0, battleAi.Length - 1)] : new BattleAiSettings();

        /// <summary>
        /// Kart speed scale (<see cref="KartPhysics.SpeedScale"/>) of engine class <paramref name="d"/>: the reference kart
        /// (<see cref="defaultPhysics"/>) reaches the class's <see cref="DifficultySettings.topSpeedKmh"/> at full throttle
        /// on flat road. Top speeds and accelerations of the decoded MHK2 karts scale with it.
        /// </summary>
        public float SpeedScale(Difficulty d)
        {
            var index = DifficultyIndex(d);
            if (speedScaleCache == null || speedScaleCache.Length != difficulties.Length) speedScaleCache = new float[difficulties.Length];
            if (speedScaleCache[index] > 0f) return speedScaleCache[index];
            if (defaultPhysics == null)
            {
                Debug.LogError("[GameConfig] defaultPhysics (reference kart) is missing: engine class speeds use the original MHK2 scale 1.");
                return 1f;
            }
            var diff = difficulties[index];
            return speedScaleCache[index] = KartPhysics.SpeedScaleFor(defaultPhysics, diff.engineClassFactor, diff.topSpeedKmh / Units.KmhPerMs, driveTorqueScale);
        }

        /// <summary>Drops derived values (the asset outlives play sessions in the editor, its profiles may have changed).</summary>
        public void ClearCaches() => speedScaleCache = null;

        int DifficultyIndex(Difficulty d) => Mathf.Clamp((int)d, 0, difficulties.Length - 1);

        void OnValidate() => ClearCaches();
    }
}
