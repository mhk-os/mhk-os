using System.Collections.Generic;
using MHK.Race;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.AI
{
    /// <summary>What a collider met by an AI feeler is to a kart driving into it.</summary>
    internal enum ObstacleKind : byte
    {
        /// <summary>Not in the way (floors, ceilings, the tops of platforms).</summary>
        Plain,
        /// <summary>Wall surface or steep static scenery.</summary>
        Wall,
        /// <summary>Kinematic or heavy body (crusher boxes, heavy props).</summary>
        Body,
        /// <summary>Light dynamic prop a kart ploughs through (crates, straw bales).</summary>
        Loose,
    }

    /// <summary>Classifies colliders for the AI's wall and prop perception, cached per collider for the current track.</summary>
    internal static partial class AIObstacleClassifier
    {
        /// <summary>Normals flatter than this (|y| below) are steep scenery.</summary>
        public const float SteepNormalY = 0.6f;
        /// <summary>Normals at least this upright are drivable ground and platform tops.</summary>
        public const float DrivableNormalY = 0.8f;

        [AutoStaticsCleanup] static readonly Dictionary<Collider, ObstacleKind> Kinds = new();
        [AutoStaticsCleanup] static TrackRuntime cacheTrack;
        [AutoStaticsCleanup] static float cacheLooseMass;

        /// <summary>Starts a new cache when the track or the loose-prop mass limit changed.</summary>
        public static void Prepare(float looseMass)
        {
            if (cacheTrack == TrackRuntime.Current && cacheLooseMass == looseMass) return;
            Kinds.Clear();
            cacheTrack = TrackRuntime.Current;
            cacheLooseMass = looseMass;
        }

        /// <summary>Kind of obstacle <paramref name="hit"/> is, <see cref="ObstacleKind.Plain"/> when it is not in the way.</summary>
        public static ObstacleKind Classify(in RaycastHit hit)
        {
            var collider = hit.collider;
            if (!Kinds.TryGetValue(collider, out var kind))
            {
                kind = KindOf(collider);
                Kinds[collider] = kind;
            }
            var ny = hit.normal.y;
            return kind switch
            {
                ObstacleKind.Wall => ObstacleKind.Wall,
                ObstacleKind.Body or ObstacleKind.Loose => ny < DrivableNormalY ? kind : ObstacleKind.Plain,
                _ => ny < SteepNormalY && ny > -SteepNormalY ? ObstacleKind.Wall : ObstacleKind.Plain,
            };
        }

        /// <summary>The collider belongs to a wall surface.</summary>
        public static bool IsWall(Collider collider)
        {
            var surface = TrackSurface.Of(collider);
            return surface != null && surface.kind.IsWall();
        }

        static ObstacleKind KindOf(Collider collider)
        {
            if (IsWall(collider)) return ObstacleKind.Wall;
            var body = collider.attachedRigidbody;
            if (body == null) return ObstacleKind.Plain;
            return !body.isKinematic && body.mass <= cacheLooseMass ? ObstacleKind.Loose : ObstacleKind.Body;
        }
    }
}
