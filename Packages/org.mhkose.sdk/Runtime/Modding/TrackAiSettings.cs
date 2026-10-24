using System;
using UnityEngine;

namespace MHK.Modding
{
    /// <summary>
    /// Per-engine-class AI tuning of a track (MHK2 objects.txt LevelSettings "AI 50cc" / "Target ranking 50cc").
    /// The AI throttle is <c>speedFactor × rankFactor</c>, where speedFactor blends from normal towards the leading or
    /// trailing factor with the distance to the (leading) human player.
    /// </summary>
    [Serializable]
    public class TrackAiSettings
    {
        public float leadingDistance = 200f;
        public float leadingSpeedFactor = 0.7f;
        public float normalSpeedFactor = 0.85f;
        public float trailingDistance = 200f;
        public float trailingSpeedFactor = 0.95f;
        [Tooltip("Throttle factor by target rank (index 0 = AI meant to win).")]
        public float[] rankFactors = { 1f, 1f, 0.95f, 0.9f, 0.85f, 0.8f };
    }
}
