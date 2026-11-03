using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Achievements
{
    /// <summary>
    /// Counts the time the game runs in the foreground into <see cref="AchievementStats.PlaytimeSeconds"/>: real time
    /// (menus, races and loading alike) while the app has focus and is not paused. An unfocused window does not count:
    /// the game stops there unless an online lobby or race keeps it running, and a window left open in the background
    /// is not time played. Whole seconds are recorded every <see cref="RecordSeconds"/> (each recording writes
    /// achievements.json and syncs the account once no race runs), when the app loses focus or pauses and when it
    /// quits; the fraction carries over. A component of the <see cref="Core.GameRoot"/>.
    /// </summary>
    [AddComponentMenu("")]
    public sealed partial class PlaytimeTracker : MonoBehaviour
    {
        const double RecordSeconds = 300;

        // Counted but not recorded yet, with the fraction of a second.
        [AutoStaticsCleanup] static double unrecorded;

        double last, nextRecord;
        bool counting;

        /// <summary>
        /// Seconds played as achievements count them (<see cref="Achievements.Counter"/>: the logged-in account's total
        /// once <see cref="Achievements.Synced"/>, otherwise this device's), with the seconds not recorded yet.
        /// </summary>
        public static long Seconds => Achievements.Counter(AchievementStats.PlaytimeSeconds) + (long)unrecorded;

        static double Now => Time.realtimeSinceStartupAsDouble;

        void OnEnable()
        {
            last = Now;
            nextRecord = last + RecordSeconds;
            counting = Application.isFocused;
        }

        void Update()
        {
            Count();
            if (last >= nextRecord) Record();
        }

        void OnApplicationFocus(bool focused)
        {
            SetCounting(focused);
            // Without running in the background the game stops until it is focused again: write now, it may be closed meanwhile.
            if (!focused && !Application.runInBackground) Achievements.SaveIfDirty();
        }

        // Mobile: the app may be ended in the background without quitting, so the seconds are written at once.
        void OnApplicationPause(bool paused)
        {
            SetCounting(!paused && Application.isFocused);
            if (paused) Achievements.SaveIfDirty();
        }

        void OnApplicationQuit()
        {
            SetCounting(false);
            Achievements.SaveIfDirty();
        }

        void OnDisable() => SetCounting(false);

        void SetCounting(bool on)
        {
            Count();
            if (counting && !on) Record();
            counting = on;
        }

        void Count()
        {
            var now = Now;
            if (counting) unrecorded += now - last;
            last = now;
        }

        void Record()
        {
            nextRecord = last + RecordSeconds;
            var whole = (long)unrecorded;
            if (whole <= 0) return;
            unrecorded -= whole;
            AchievementStats.Add(AchievementStats.PlaytimeSeconds, whole);
        }
    }
}
