using MHK.Karts;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Input
{
    /// <summary>Input injected by on-screen touch controls per local player.</summary>
    public static partial class VirtualKartInput
    {
        /// <summary>Local players with an input slot.</summary>
        public const int MaxLocalPlayers = 4;

        [AutoStaticsCleanup] static KartInput[] players = new KartInput[MaxLocalPlayers];
        [AutoStaticsCleanup] static float[] steerWishes = new float[MaxLocalPlayers];
        [AutoStaticsCleanup] static bool[] steerWishHeld = new bool[MaxLocalPlayers];
        [AutoStaticsCleanup] static bool[] cameraPresses = new bool[MaxLocalPlayers];

        public static ref KartInput ForPlayer(int index) => ref players[Slot(index)];

        /// <summary>
        /// Analog steering of a held touch joystick or wheel (-1 left .. 1 right): the kart's wheel is steered towards
        /// this share of full lock with the steering keys (<see cref="PlayerKartDriver"/>). Null releases it.
        /// </summary>
        public static void SetSteerWish(int index, float? wish)
        {
            var i = Slot(index);
            steerWishHeld[i] = wish.HasValue;
            steerWishes[i] = wish.HasValue ? Mathf.Clamp(wish.Value, -1f, 1f) : 0f;
        }

        /// <summary>True while a touch joystick or wheel steers player <paramref name="index"/>, with its wish.</summary>
        public static bool TryGetSteerWish(int index, out float wish)
        {
            var i = Slot(index);
            wish = steerWishes[i];
            return steerWishHeld[i];
        }

        /// <summary>The touch camera button was pressed: the next <see cref="TakeCameraPress"/> of the player reports it.</summary>
        public static void PressCamera(int index) => cameraPresses[Slot(index)] = true;

        /// <summary>Whether the touch camera button was pressed since the last call, clearing the press.</summary>
        public static bool TakeCameraPress(int index)
        {
            var i = Slot(index);
            var pressed = cameraPresses[i];
            cameraPresses[i] = false;
            return pressed;
        }

        /// <summary>Releases every touch input of player <paramref name="index"/>.</summary>
        public static void Clear(int index)
        {
            var i = Slot(index);
            players[i] = default;
            steerWishHeld[i] = false;
            steerWishes[i] = 0f;
            cameraPresses[i] = false;
        }

        static int Slot(int index) => Mathf.Clamp(index, 0, MaxLocalPlayers - 1);
    }
}
