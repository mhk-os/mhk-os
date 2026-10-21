using System.Collections.Generic;
using MHK.Karts;
using UnityEngine;
using UnityEngine.Pool;

namespace MHK.Powerups
{
    public enum TargetSelection
    {
        [Tooltip("Kart directly ahead in race position.")] NextAhead,
        [Tooltip("Race leader (or second place if the user leads).")] Leader,
        [Tooltip("Every kart ahead of the user.")] AllAhead,
        [Tooltip("Every other kart.")] AllOthers,
        [Tooltip("Nearest kart in front within a 60° cone.")] NearestInFront,
        [Tooltip("Nearest kart regardless of direction.")] Nearest,
    }

    /// <summary>Picks the karts a targeted item goes for. Ghosts (<see cref="KartEffects.Ghost"/>) are never targeted.</summary>
    public static class PowerupTargeting
    {
        /// <summary>Half angle (degrees) of the cone used by <see cref="TargetSelection.NearestInFront"/>.</summary>
        public const float FrontConeHalfAngle = 30f;
        /// <summary>Default range (m) of <see cref="TargetSelection.NearestInFront"/>.</summary>
        public const float DefaultMaxDistance = 200f;

        /// <summary>Fills <paramref name="results"/> with the targets of <paramref name="owner"/> in selection order.</summary>
        public static void Select(Kart owner, TargetSelection selection, float maxDistance, List<Kart> results)
        {
            results.Clear();
            using var pooled = ListPool<Kart>.Get(out var others);
            foreach (var k in Kart.All)
                if (k != owner && k.isActiveAndEnabled && !k.Effects.Ghost) others.Add(k);
            if (others.Count == 0) return;
            var myPosition = owner.Progress.Position;
            switch (selection)
            {
                case TargetSelection.NextAhead: AddIfAny(results, NextAhead(others, myPosition)); break;
                case TargetSelection.Leader: AddIfAny(results, Leader(others)); break;
                case TargetSelection.AllAhead:
                    foreach (var k in others)
                        if (k.Progress.Position < myPosition) results.Add(k);
                    break;
                case TargetSelection.AllOthers: results.AddRange(others); break;
                case TargetSelection.NearestInFront: AddIfAny(results, Nearest(owner, others, true, maxDistance)); break;
                case TargetSelection.Nearest: AddIfAny(results, Nearest(owner, others, false, 0f)); break;
            }
        }

        /// <summary>The first target of <paramref name="owner"/>, or null.</summary>
        public static Kart First(Kart owner, TargetSelection selection, float maxDistance = DefaultMaxDistance)
        {
            using var pooled = ListPool<Kart>.Get(out var results);
            Select(owner, selection, maxDistance, results);
            return results.Count > 0 ? results[0] : null;
        }

        static void AddIfAny(List<Kart> results, Kart k)
        {
            if (k != null) results.Add(k);
        }

        static Kart NextAhead(List<Kart> others, int myPosition)
        {
            Kart best = null;
            foreach (var k in others)
                if (k.Progress.Position < myPosition && (best == null || k.Progress.Position > best.Progress.Position)) best = k;
            return best;
        }

        static Kart Leader(List<Kart> others)
        {
            Kart best = null;
            foreach (var k in others)
                if (best == null || k.Progress.Position < best.Progress.Position) best = k;
            return best;
        }

        static Kart Nearest(Kart owner, List<Kart> others, bool inFront, float maxDistance)
        {
            var origin = owner.transform.position;
            var forward = owner.transform.forward;
            Kart best = null;
            var bestDistance = 0f;
            foreach (var k in others)
            {
                var p = k.transform.position;
                var distance = Vector3.Distance(p, origin);
                if (inFront && (Vector3.Angle(forward, p - origin) >= FrontConeHalfAngle || distance >= maxDistance)) continue;
                if (best != null && distance >= bestDistance) continue;
                best = k;
                bestDistance = distance;
            }
            return best;
        }
    }
}
