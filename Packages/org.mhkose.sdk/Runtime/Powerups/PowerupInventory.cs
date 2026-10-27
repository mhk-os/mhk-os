using MHK.Core;
using MHK.Karts;
using MHK.Modding;
using MHK.Race;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Powerups
{
    /// <summary>
    /// Holds the kart's current powerup and fires it on input. Items with several charges wait
    /// <see cref="PowerupDefinition.useCooldown"/> between uses. Fire presses during the wait do nothing. Each use gets
    /// its own <see cref="PowerupContext.usage"/> id. The roulette and the cooldown run in physics steps. Online the
    /// server decides what a box gives: the roulette spins until its <see cref="Grant"/> (or <see cref="Deny"/>) arrives.
    /// </summary>
    [RequireComponent(typeof(Kart))]
    public partial class PowerupInventory : MonoBehaviour
    {
        const int NoBox = -1;

        public PowerupDefinition Held { get; private set; }
        public int Charges { get; private set; }
        /// <summary>The roulette runs: an item was given and is not revealed yet, or online the server's answer is awaited.</summary>
        public bool Rolling => RaceClock.Ticks < rollUntilTick || pendingBox != NoBox;
        /// <summary>An item is held (the roulette has stopped). It may still be cooling down (<see cref="CoolingDown"/>).</summary>
        public bool HasItem => Held != null && !Rolling;
        /// <summary>The held item was just used and its next charge is not ready yet.</summary>
        public bool CoolingDown => Held != null && RaceClock.Ticks < cooldownUntilTick;
        /// <summary>MHK2: a box always replaces the held item. Otherwise only empty karts collect. Never while a pickup waits for the server.</summary>
        public bool CanReceive => pendingBox == NoBox && (Held == null || Config.itemBoxReplacesHeld);
        /// <summary>The held item's visual (<see cref="PowerupDefinition.heldPrefab"/>) while it is shown, else null.</summary>
        public Transform HeldVisual => heldVisual != null && heldVisual.activeInHierarchy ? heldVisual.transform : null;
        /// <summary>Items received so far: changes with every pickup, also when a box gives the item already held.</summary>
        public int Received { get; private set; }

        Kart kart;
        bool lastFire;
        // Race ticks (RaceClock.Ticks, counted before every kart and item steps) at which the roulette and the cooldown end.
        int rollUntilTick;
        int cooldownUntilTick;
        bool revealPending;
        GameObject heldVisual;
        float heldHiddenUntil;
        // Online: the box whose item the server has not answered yet, and how long the roulette has run for it (s).
        int pendingBox = NoBox;
        float pendingTime;

        [AutoStaticsCleanup] static int lastUsage;

        static GameConfig Config => GameRoot.Instance.Config;

        void Awake() => kart = GetComponent<Kart>();

        /// <summary>Starts the roulette for <paramref name="def"/> if the kart can receive an item.</summary>
        public void Give(PowerupDefinition def)
        {
            if (def == null || !CanReceive) return;
            Take(def, Mathf.Max(1, ChargesOf(def)), Config.itemRouletteSeconds);
        }

        void Take(PowerupDefinition def, int charges, float roulette)
        {
            DestroyHeldVisual();
            Held = def;
            Received++;
            Charges = charges;
            cooldownUntilTick = 0;
            heldHiddenUntil = 0f;
            rollUntilTick = RaceClock.Ticks + StepsOf(roulette);
            revealPending = true;
            if (!Rolling) Reveal();
        }

        // Whole physics steps of a duration: a cooldown or roulette ends on the same step at any frame rate.
        static int StepsOf(float seconds) => seconds > 0f ? Mathf.CeilToInt(seconds / Time.fixedDeltaTime - StepRounding) : 0;

        const float StepRounding = 1e-3f;

        void Reveal()
        {
            revealPending = false;
            ShowHeld();
        }

        /// <summary>
        /// Online: the kart touched item box <paramref name="boxId"/> and asked the server for its item. The held item
        /// goes (a box replaces it, as offline) and the roulette spins until <see cref="Grant"/> or <see cref="Deny"/>.
        /// </summary>
        public void BeginPending(int boxId)
        {
            Clear();
            pendingBox = boxId;
            pendingTime = 0f;
        }

        /// <summary>
        /// Online: the server gave <paramref name="def"/> with <paramref name="charges"/> for box <paramref name="boxId"/>.
        /// The roulette stops no earlier than <see cref="GameConfig.itemRouletteSeconds"/> after the pickup.
        /// </summary>
        public void Grant(int boxId, PowerupDefinition def, int charges)
        {
            if (boxId != pendingBox) return;
            pendingBox = NoBox;
            if (def != null) Take(def, Mathf.Max(1, charges), Mathf.Max(0f, Config.itemRouletteSeconds - pendingTime));
        }

        /// <summary>Online: the server refused box <paramref name="boxId"/>: the roulette stops empty.</summary>
        public void Deny(int boxId)
        {
            if (boxId == pendingBox) pendingBox = NoBox;
        }

        // Battles may give fewer charges (PowerupDefinition.battleCharges, MHK Extra hammer: 1 instead of 3).
        static int ChargesOf(PowerupDefinition def)
        {
            var race = Race.RaceManager.Current;
            return def.battleCharges > 0 && race != null && race.IsBattle ? def.battleCharges : def.charges;
        }

        // Never 0 (= no use): wraps to 1 after int.MaxValue uses.
        static int NextUsage() => lastUsage = lastUsage == int.MaxValue ? 1 : lastUsage + 1;

        /// <summary>Drops the held item without using it (a pickup waiting for the server stays pending).</summary>
        public void Clear()
        {
            Held = null;
            Charges = 0;
            rollUntilTick = 0;
            cooldownUntilTick = 0;
            revealPending = false;
            DestroyHeldVisual();
        }

        // Nulled at once: Destroy only takes effect at the end of the frame, and ShowHeld must not see the old one.
        void DestroyHeldVisual()
        {
            if (heldVisual != null) Destroy(heldVisual);
            heldVisual = null;
        }

        void FixedUpdate()
        {
            if (pendingBox != NoBox) pendingTime += Time.fixedDeltaTime;
            if (revealPending && !Rolling) Reveal();
        }

        // The held visual is presentation: it hides on frame time.
        void Update()
        {
            if (heldVisual != null && !heldVisual.activeSelf && Time.time >= heldHiddenUntil) heldVisual.SetActive(true);
        }

        void ShowHeld()
        {
            if (Held == null || Held.heldPrefab == null || heldVisual != null) return;
            heldVisual = Instantiate(Held.heldPrefab, KartMounts.Parent(kart));
            heldVisual.transform.localPosition += KartMounts.Point(kart, Held.heldMount);
            if (Time.time < heldHiddenUntil) heldVisual.SetActive(false);
        }

        /// <summary>
        /// Hides the held-item visual for <paramref name="seconds"/>, e.g. while a use prefab animates its own copy of
        /// it (hammer swing). It reappears afterwards if charges are left.
        /// </summary>
        public void HideHeldVisual(float seconds)
        {
            heldHiddenUntil = Time.time + seconds;
            if (heldVisual != null) heldVisual.SetActive(false);
        }

        /// <summary>Edge-triggered use, aimed backwards while "look back" is held. A press while the item cools down is ignored.</summary>
        public void HandleInput(in KartInput input)
        {
            if (input.fire && !lastFire) TryUse(input.lookBack);
            lastFire = input.fire;
        }

        // Online the use gets its network id and is announced with the pose it spawned from and the target it chose.
        void TryUse(bool backwards)
        {
            if (!HasItem || CoolingDown || kart.IsRemote) return;
            var def = Held;
            if (Config.usingItemCancelsBuffs) kart.Effects.EndExclusiveBuffs();
            var online = OnlineItems.IsLocal(kart);
            var usage = online ? OnlineItems.NextUseId() : NextUsage();
            transform.GetPositionAndRotation(out var position, out var rotation);
            GameObject spawned = null;
            if (def.usePrefab != null)
                spawned = PowerupFx.SpawnAndLaunch(def.usePrefab, position, rotation, new PowerupContext(kart, def, backwards, usage));
            else Debug.LogWarning($"[Powerups] '{def.Id}' has no use prefab.");
            if (online) OnlineItems.SendUse(usage, position, rotation, backwards, spawned);
            Audio.AudioManager.PlaySfx(def.useSound, transform);
            Charges--;
            if (Charges <= 0) Clear();
            else cooldownUntilTick = RaceClock.Ticks + StepsOf(def.useCooldown);
        }
    }
}
