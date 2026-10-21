using MHK.Core;
using MHK.Karts;
using MHK.Modding;
using UnityEngine;

namespace MHK.Powerups
{
    /// <summary>
    /// Holds the kart's current powerup and fires it on input. Items with several charges wait
    /// <see cref="PowerupDefinition.useCooldown"/> between uses. Fire presses during the wait do nothing.
    /// </summary>
    [RequireComponent(typeof(Kart))]
    public class PowerupInventory : MonoBehaviour
    {
        public PowerupDefinition Held { get; private set; }
        public int Charges { get; private set; }
        public bool Rolling => rollTimer > 0f;
        /// <summary>An item is held (the roulette has stopped). It may still be cooling down (<see cref="CoolingDown"/>).</summary>
        public bool HasItem => Held != null && !Rolling;
        /// <summary>The held item was just used and its next charge is not ready yet.</summary>
        public bool CoolingDown => Held != null && Time.time < cooldownUntil;
        /// <summary>MHK2: a box always replaces the held item. Otherwise only empty karts collect.</summary>
        public bool CanReceive => Held == null || Config.itemBoxReplacesHeld;
        /// <summary>The held item's visual (<see cref="PowerupDefinition.heldPrefab"/>) while it is shown, else null.</summary>
        public Transform HeldVisual => heldVisual != null && heldVisual.activeInHierarchy ? heldVisual.transform : null;
        /// <summary>Items received so far: changes with every pickup, also when a box gives the item already held.</summary>
        public int Received { get; private set; }

        Kart kart;
        bool lastFire;
        float rollTimer;
        float cooldownUntil;
        GameObject heldVisual;
        float heldHiddenUntil;

        static GameConfig Config => GameRoot.Instance.Config;

        void Awake() => kart = GetComponent<Kart>();

        /// <summary>Starts the roulette for <paramref name="def"/> if the kart can receive an item.</summary>
        public void Give(PowerupDefinition def)
        {
            if (def == null || !CanReceive) return;
            DestroyHeldVisual();
            Held = def;
            Received++;
            Charges = Mathf.Max(1, def.charges);
            cooldownUntil = 0f;
            heldHiddenUntil = 0f;
            rollTimer = Config.itemRouletteSeconds;
            if (rollTimer <= 0f) ShowHeld();
        }

        /// <summary>Drops the held item without using it.</summary>
        public void Clear()
        {
            Held = null;
            Charges = 0;
            rollTimer = 0f;
            cooldownUntil = 0f;
            DestroyHeldVisual();
        }

        // Nulled at once: Destroy only takes effect at the end of the frame, and ShowHeld must not see the old one.
        void DestroyHeldVisual()
        {
            if (heldVisual != null) Destroy(heldVisual);
            heldVisual = null;
        }

        void Update()
        {
            if (rollTimer > 0f)
            {
                rollTimer -= Time.deltaTime;
                if (rollTimer <= 0f)
                {
                    rollTimer = 0f;
                    ShowHeld();
                }
            }
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

        void TryUse(bool backwards)
        {
            if (!HasItem || CoolingDown) return;
            var def = Held;
            if (Config.usingItemCancelsBuffs) kart.Effects.EndExclusiveBuffs();
            if (def.usePrefab != null)
                PowerupFx.SpawnAndLaunch(def.usePrefab, transform.position, transform.rotation, new PowerupContext(kart, def, backwards));
            else Debug.LogWarning($"[Powerups] '{def.Id}' has no use prefab.");
            Audio.AudioManager.PlaySfx(def.useSound, transform.position);
            Charges--;
            if (Charges <= 0) Clear();
            else cooldownUntil = Time.time + def.useCooldown;
        }
    }
}
