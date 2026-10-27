using System.Collections.Generic;
using MHK.Core;
using MHK.Karts;
using MHK.Modding;
using MHK.Online;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Powerups
{
    /// <summary>
    /// Pickup placed on the track that gives a random powerup (<see cref="PowerupRoller"/>) and respawns after a delay
    /// (counted in physics steps). A fixed id gives that powerup, applies that status effect on contact, or gives the
    /// character special (<see cref="CharacterSpecialId"/>). Boxes that give powerups are removed from races without items.
    /// Online the server decides what a box gives and who got it (<see cref="OnlineItems"/>): only the local kart picks
    /// up here, status effect boxes stay local.
    /// </summary>
    public partial class ItemBox : MonoBehaviour
    {
        public const string CharacterSpecialId = "@special";

        [Tooltip("Optional fixed powerup / effect id, or '@special'. Empty = random roll.")]
        public string fixedPowerupId;
        [SerializeField] float radius = 1.4f;
        [Tooltip("Respawn delay (s). <= 0 uses GameConfig.itemBoxRespawnSeconds.")]
        [SerializeField] float respawnSeconds = -1f;
        [Tooltip("Visual root hidden while collected (its ItemBoxLook shows and animates the box).")]
        [SerializeField] GameObject visual;
        [SerializeField] GameObject pickupVfx;
        [SerializeField] AudioClip pickupSound;

        float hiddenTimer;
        AudioClip resolvedPickupSound;

        [AutoStaticsCleanup] static readonly List<ItemBox> active = new();
        /// <summary>All item boxes currently enabled in the scene (computer drivers plan their pickups from it).</summary>
        public static IReadOnlyList<ItemBox> Active => active;

        /// <summary>Can be collected now (not waiting to reappear).</summary>
        public bool Available => hiddenTimer <= 0f;
        /// <summary>Online box id: index in the stable order of the track's boxes (<see cref="StableOrder"/>), -1 offline.</summary>
        public int NetId { get; internal set; } = -1;
        /// <summary>Respawn delay (s).</summary>
        public float RespawnSeconds => respawnSeconds > 0 ? respawnSeconds : GameRoot.Instance.Config.itemBoxRespawnSeconds;

        void OnEnable() => active.Add(this);
        void OnDisable() => active.Remove(this);

        // Status effect pickups (Thunder arena health, quad damage) stay when items are off, as they did before.
        void Start()
        {
            if (!PowerupRoller.ItemsEnabled && GivesPowerups()) gameObject.SetActive(false);
        }

        /// <summary>The box gives a powerup (random, fixed or the character special), not a status effect.</summary>
        public bool GivesPowerups() =>
            string.IsNullOrEmpty(fixedPowerupId) || fixedPowerupId == CharacterSpecialId ||
            GameRoot.Instance.Content.Get<StatusEffectDefinition>(fixedPowerupId) == null;

        void FixedUpdate()
        {
            if (hiddenTimer > 0f)
            {
                hiddenTimer -= Time.fixedDeltaTime;
                if (hiddenTimer <= 0f) Show(true);
                return;
            }
            var p = transform.position;
            var karts = Kart.All;
            for (var i = 0; i < karts.Count; i++)
            {
                var k = karts[i];
                var d = k.transform.position - p;
                if (d.sqrMagnitude > radius * radius || k.Eliminated || k.Effects.Hidden || k.IsRemote) continue;
                if (!TryGive(k)) continue;
                Collect();
                break;
            }
        }

        /// <summary>Online: another racer got this box <paramref name="ticksAgo"/> physics steps ago (shown as its pickup).</summary>
        internal void HideTaken(int ticksAgo)
        {
            Collect();
            hiddenTimer -= Mathf.Max(0, ticksAgo) * Time.fixedDeltaTime;
            if (hiddenTimer <= 0f) Restore();
        }

        /// <summary>Online: the server refused the local kart's pickup: the box is there again.</summary>
        internal void Restore()
        {
            hiddenTimer = 0f;
            Show(true);
        }

        void Show(bool on)
        {
            if (visual != null) visual.SetActive(on);
        }

        bool TryGive(Kart k)
        {
            var content = GameRoot.Instance.Content;
            if (!string.IsNullOrEmpty(fixedPowerupId) && fixedPowerupId != CharacterSpecialId)
            {
                var effect = content.Get<StatusEffectDefinition>(fixedPowerupId);
                if (effect != null) return k.Effects.Apply(effect, k);
            }
            if (!k.Inventory.CanReceive || !PowerupRoller.ItemsEnabled) return false;
            if (OnlineItems.Active) return OnlineItems.RequestPickup(this, k);
            var def = PowerupFor(k, content);
            if (def == null) return false;
            k.Inventory.Give(def);
            return true;
        }

        PowerupDefinition PowerupFor(Kart k, ContentRegistry content)
        {
            if (string.IsNullOrEmpty(fixedPowerupId)) return PowerupRoller.Roll(k);
            if (fixedPowerupId != CharacterSpecialId) return content.Get<PowerupDefinition>(fixedPowerupId);
            var special = k.Character != null ? k.Character.specialPowerup : null;
            // Online (solo races roll here) only a special that is lobby content, like RaceLoaded.specials.
            if (special != null && OnlineRace.Current != null && OnlineLobby.Current != null
                && !ContentManifests.IsLobbyContent(content, special, OnlineLobby.Current.settings.packs))
                special = null;
            return special != null ? special : PowerupRoller.Roll(k);
        }

        void Collect()
        {
            if (resolvedPickupSound == null) resolvedPickupSound = pickupSound != null ? pickupSound : PackPickupSound();
            hiddenTimer = RespawnSeconds;
            Show(false);
            PowerupFx.Spawn(pickupVfx, transform.position, resolvedPickupSound);
        }

        static AudioClip PackPickupSound() =>
            ContentPack.Find(GameRoot.Instance.Content.Packs, p => p.itemPickupSound,
                Race.RaceManager.Current != null ? Race.RaceManager.Current.Track.OwnerPack : null);
    }
}
