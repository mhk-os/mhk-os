using System.Runtime.CompilerServices;

namespace MHK.AI
{
    /// <summary>Random decisions of a computer driver: repeatable hash rolls per object and the seeded draws of a race.</summary>
    internal static class AIRolls
    {
        // Salts of the per-driver rolls: one decision per trap, fake box, speed pad, melee holder and lap.
        public const int TrapSalt = 0x2545F491, FakeSalt = 0x6C8E9CF5, PadSalt = 0x1B873593, MeleeSalt = 0x3C6EF372;
        public const int KeyPrime = 7919;

        /// <summary>Uniform 0..1 from a seed and a key: the same decision every time it is asked.</summary>
        public static float Roll01(int seed, int key)
        {
            unchecked
            {
                var h = (uint)seed * 0x9E3779B1u ^ (uint)key * 0x85EBCA77u;
                h ^= h >> 15;
                h *= 0x2C1B3C6Du;
                h ^= h >> 12;
                h *= 0x297A2D39u;
                h ^= h >> 15;
                return (h & 0xFFFFFF) / 16777216f;
            }
        }

        /// <summary>Roll for one object, the same for the whole race (keyed by identity, not by value).</summary>
        public static float Once(int seed, object o) => Roll01(seed, RuntimeHelpers.GetHashCode(o));

        /// <summary>Roll for one object, drawn again every lap.</summary>
        public static float PerLap(int seed, object o, int lap) => Roll01(seed, RuntimeHelpers.GetHashCode(o) + lap * KeyPrime);

        public static float NextFloat(this System.Random rng) => (float)rng.NextDouble();

        public static float Range(this System.Random rng, float min, float max) => min + (max - min) * rng.NextFloat();

        /// <summary>True with <paramref name="chance"/>, drawing nothing when the chance is 0 or less.</summary>
        public static bool Roll(this System.Random rng, float chance) => chance > 0f && rng.NextFloat() < chance;
    }
}
