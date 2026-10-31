using System.Collections.Generic;
using MHK.Modding;
using MHK.Powerups;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.AI
{
    /// <summary>What an item does, as far as the AI's item use is concerned.</summary>
    internal enum AIItemKind
    {
        /// <summary>No known behaviour component: used by its <see cref="PowerupDefinition.aiUsage"/> hint.</summary>
        Unknown,
        /// <summary>MHK2 homing missile: always hunts the race leader.</summary>
        HomingMissile,
        /// <summary>Straight 3D shot (beams, fireworks and TINcan rockets, soap bubbles).</summary>
        StraightShot,
        /// <summary>Ground-following shot fired along the kart's facing (snowball).</summary>
        GroundShot,
        /// <summary>Ground shot that steers itself onto a selected kart.</summary>
        HomingShot,
        /// <summary>Rolling physics ball (glibber ball).</summary>
        Ball,
        /// <summary>Lobbed onto the road a speed-dependent distance ahead (water bomb).</summary>
        Lob,
        /// <summary>Hits the nearest kart within a short radius (wooden hammer).</summary>
        Melee,
        /// <summary>Strikes selected karts directly, no flight (16-ton weight, bang-flash).</summary>
        Targeted,
        /// <summary>Field around the user that affects karts within a radius.</summary>
        Area,
        /// <summary>Trap left on the track (x-field, mine, gum, spike strip, black hole, banana rain).</summary>
        Trap,
        /// <summary>Effect on the user (turbo, blue light, shield, panzer).</summary>
        Self,
        /// <summary>The user digs ahead under the track (Hank's molehill), <see cref="AIItemProfile.Reach"/> is the dig distance.</summary>
        Dig,
    }

    /// <summary>
    /// Description of an item for the AI, derived once per <see cref="PowerupDefinition"/> from the behaviour components
    /// on its use prefab (a burst is described by its shot), so new items made from the same components need no code.
    /// </summary>
    internal sealed partial class AIItemProfile
    {
        const float KartHalfWidth = 0.6f;
        const float MaxAimRange = 90f, MaxBallAimRange = 60f;
        const float BallMargin = 0.2f;
        const float BoostThreshold = 1.01f;
        const int MaxBurstDepth = 2;
        const float LobBaseFlightTime = 0.6f, LobFlightTimePerMetre = 0.01f;

        [AutoStaticsCleanup] static readonly Dictionary<PowerupDefinition, AIItemProfile> cache = new();

        public AIItemKind Kind { get; private set; } = AIItemKind.Unknown;
        /// <summary>Effective forward reach (m) of shots and forward throws.</summary>
        public float Range { get; private set; }
        /// <summary>Half width of the corridor a shot hits (m, kart centre to the shot's line).</summary>
        public float LateralTolerance { get; private set; }
        /// <summary>Launch speed (m/s), 0 = instant.</summary>
        public float Speed { get; private set; }
        /// <summary>Shots fly at least this much faster than the user (m/s, negative = fixed speed).</summary>
        public float MinSpeedOverOwner { get; private set; } = -1f;
        /// <summary>Speed loss of decelerating shots (m/s²).</summary>
        public float Deceleration { get; private set; }
        /// <summary>Shots are stopped by level geometry: only fired with a clear line of sight.</summary>
        public bool NeedsLineOfSight { get; private set; }
        /// <summary>Straight shots fly along the racing line (the way the kart faces) instead of the kart's facing.</summary>
        public bool AlongTrack { get; private set; }
        /// <summary>Can be used to the rear (shots fired backwards, traps dropped or thrown behind).</summary>
        public bool CanGoBehind { get; private set; }
        /// <summary>Traps that can also be thrown forward: landing distance ahead (m), 0 = none.</summary>
        public float ForwardThrowRange { get; private set; }
        /// <summary>Placed items thrown backwards with look back instead of dropped: landing distance behind (m), 0 = dropped.</summary>
        public float BackwardThrowRange { get; private set; }
        /// <summary>Thrown placed items: flight time (s) of the arc onto flat ground.</summary>
        public float ThrowFlightTime { get; private set; }
        /// <summary>Goes to the rear in a throw rather than left at the bumper (<see cref="BackwardThrowRange"/>).</summary>
        public bool ThrownBehind => BackwardThrowRange > 0f;
        /// <summary>Thrown charges that explode on contact or landing (<see cref="ExplosiveCharge"/>): blast radius (m), 0 = none.</summary>
        public float BlastRadius { get; private set; }
        /// <summary>Homing and targeted items: whom they go for.</summary>
        public TargetSelection Selection { get; private set; } = TargetSelection.NextAhead;
        /// <summary>Targeted, melee and area items: reach around the user (m, 0 = unlimited).</summary>
        public float Reach { get; private set; }
        /// <summary>Lob: landing distance standing still (m).</summary>
        public float LobDistance { get; private set; }
        /// <summary>Lob: extra landing distance per m/s of the user's speed.</summary>
        public float LobPerSpeed { get; private set; }
        /// <summary>Self effects: blocks hostile effects (shield, blue light, panzer).</summary>
        public bool Protects { get; private set; }
        /// <summary>Self effects: speeds the kart up (turbo, blue light).</summary>
        public bool Boosts { get; private set; }
        /// <summary>Self effects: rams karts it touches (panzer aura).</summary>
        public bool Rams { get; private set; }

        /// <summary>The cached profile of <paramref name="def"/>.</summary>
        public static AIItemProfile Of(PowerupDefinition def)
        {
            if (cache.TryGetValue(def, out var p)) return p;
            p = new AIItemProfile();
            if (def.usePrefab != null) p.Describe(def.usePrefab, 0);
            cache[def] = p;
            return p;
        }

        /// <summary>Forgets every profile (content reload: the definitions they were made from are gone).</summary>
        public static void ClearCache() => cache.Clear();

        /// <summary>Lob: landing distance ahead (m) at the user's forward <paramref name="speed"/>.</summary>
        public float LobLanding(float speed) => LobDistance + LobPerSpeed * speed;

        /// <summary>Lob: approximate flight time (s) to a landing <paramref name="distance"/> metres ahead.</summary>
        public static float LobFlightTime(float distance) => LobBaseFlightTime + LobFlightTimePerMetre * distance;

        // First match wins: the order is the priority of the behaviour components.
        void Describe(GameObject prefab, int depth)
        {
            _ = TryDig(prefab) || TryHomingMissile(prefab) || TryLob(prefab) || TryStraightShot(prefab) || TryProjectile(prefab)
                || TryBall(prefab) || TryMelee(prefab) || TryTargeted(prefab) || TryBurst(prefab, depth) || TryPlacement(prefab)
                || TryDroppedTrap(prefab) || TryScatter(prefab) || TryArea(prefab) || TrySelf(prefab);
        }

        bool TryDig(GameObject prefab)
        {
            if (!prefab.TryGetComponent<MolehillPowerup>(out var dig)) return false;
            Kind = AIItemKind.Dig;
            Reach = dig.digDistance;
            return true;
        }

        bool TryHomingMissile(GameObject prefab)
        {
            if (!prefab.TryGetComponent<TrackHomingMissile>(out _)) return false;
            Kind = AIItemKind.HomingMissile;
            return true;
        }

        bool TryLob(GameObject prefab)
        {
            if (!prefab.TryGetComponent<TrackLobPowerup>(out var lob)) return false;
            Kind = AIItemKind.Lob;
            LobDistance = lob.throwDistance;
            LobPerSpeed = lob.distancePerSpeed;
            return true;
        }

        bool TryStraightShot(GameObject prefab)
        {
            if (!prefab.TryGetComponent<StraightProjectile>(out var straight)) return false;
            Kind = AIItemKind.StraightShot;
            Speed = straight.speed;
            Deceleration = straight.deceleration;
            MinSpeedOverOwner = straight.minSpeedOverOwner;
            var reachable = straight.deceleration > 0f ? straight.speed * straight.speed / (2f * straight.deceleration) : straight.speed * straight.lifetime;
            Range = Mathf.Min(MaxAimRange, reachable, straight.speed * straight.lifetime);
            LateralTolerance = straight.hitRadius + KartHalfWidth;
            NeedsLineOfSight = !straight.passThroughWorld;
            AlongTrack = straight.aim == ProjectileAim.TrackDirection;
            CanGoBehind = true;
            return true;
        }

        bool TryProjectile(GameObject prefab)
        {
            if (!prefab.TryGetComponent<ProjectilePowerup>(out var shot)) return false;
            Speed = shot.speed;
            Deceleration = shot.deceleration;
            MinSpeedOverOwner = shot.ignoreOwnerSpeed ? -1f : ProjectilePowerup.OwnerSpeedMargin;
            var reachable = shot.deceleration > 0f ? shot.speed * shot.speed / (2f * shot.deceleration) : shot.speed * shot.lifetime;
            Range = Mathf.Min(MaxAimRange, reachable, shot.speed * shot.lifetime);
            LateralTolerance = shot.hitRadius + KartHalfWidth;
            if (shot.homing)
            {
                Kind = AIItemKind.HomingShot;
                Selection = shot.targetSelection;
                Reach = shot.activationDistance;
                return true;
            }
            Kind = AIItemKind.GroundShot;
            NeedsLineOfSight = !shot.followTrackPath;
            CanGoBehind = true;
            return true;
        }

        bool TryBall(GameObject prefab)
        {
            if (!prefab.TryGetComponent<PhysicsBallPowerup>(out var ball)) return false;
            Kind = AIItemKind.Ball;
            Speed = ball.throwSpeed;
            Range = Mathf.Min(MaxBallAimRange, ball.throwSpeed * ball.moveTime);
            // The ball's hit radius is measured from the kart's centre already.
            LateralTolerance = ball.hitRadius + BallMargin;
            NeedsLineOfSight = true;
            CanGoBehind = true;
            return true;
        }

        bool TryMelee(GameObject prefab)
        {
            if (!prefab.TryGetComponent<HammerSwingPowerup>(out var hammer)) return false;
            Kind = AIItemKind.Melee;
            Reach = hammer.radius;
            return true;
        }

        bool TryTargeted(GameObject prefab)
        {
            if (!prefab.TryGetComponent<TargetEffectPowerup>(out var targeted)) return false;
            Kind = AIItemKind.Targeted;
            Selection = targeted.targets;
            Reach = targeted.maxDistance;
            return true;
        }

        bool TryBurst(GameObject prefab, int depth)
        {
            if (depth >= MaxBurstDepth || !prefab.TryGetComponent<BurstPowerup>(out var burst) || burst.shotPrefab == null) return false;
            Describe(burst.shotPrefab, depth + 1);
            return true;
        }

        bool TryPlacement(GameObject prefab)
        {
            if (!prefab.TryGetComponent<PlacementPowerup>(out var place)) return false;
            Kind = AIItemKind.Trap;
            CanGoBehind = true;
            ThrowFlightTime = FlightTime(place);
            var range = PlacementPowerup.ThrowOriginForward + place.throwSpeed * ThrowFlightTime;
            if (place.throwForward) ForwardThrowRange = range;
            if (place.throwBackwards) BackwardThrowRange = range;
            if (prefab.TryGetComponent<ExplosiveCharge>(out var charge)) BlastRadius = charge.hitRadius;
            return true;
        }

        bool TryDroppedTrap(GameObject prefab)
        {
            if (!prefab.TryGetComponent<HeldItemDropPowerup>(out _) && !prefab.TryGetComponent<HazardTrap>(out _)) return false;
            Kind = AIItemKind.Trap;
            CanGoBehind = true;
            return true;
        }

        bool TryScatter(GameObject prefab)
        {
            if (!prefab.TryGetComponent<ScatterPowerup>(out var scatter)) return false;
            Kind = AIItemKind.Trap;
            CanGoBehind = true;
            Reach = scatter.radius;
            return true;
        }

        bool TryArea(GameObject prefab)
        {
            if (!prefab.TryGetComponent<AreaEffectPowerup>(out var area)) return false;
            Kind = AIItemKind.Area;
            Reach = area.radius;
            return true;
        }

        bool TrySelf(GameObject prefab)
        {
            if (!prefab.TryGetComponent<SelfEffectPowerup>(out var self) || self.effects == null) return false;
            Kind = AIItemKind.Self;
            foreach (var e in self.effects)
            {
                if (e == null) continue;
                Protects |= e.blocksHostileEffects;
                Boosts |= e.maxSpeedMultiplier > BoostThreshold || e.accelerationMultiplier > BoostThreshold || e.forwardImpulse > 0f;
                Rams |= e.auraEffect != null;
            }
            return true;
        }

        // Ballistic flight of a thrown placed item from its throw origin down to the kart's ground level.
        static float FlightTime(PlacementPowerup p)
        {
            var g = Mathf.Max(0.1f, p.gravity);
            var vy = p.throwUpSpeed;
            return Mathf.Min(p.maxFlightTime, (vy + Mathf.Sqrt(vy * vy + 2f * g * PlacementPowerup.ThrowOriginUp)) / g);
        }
    }
}
