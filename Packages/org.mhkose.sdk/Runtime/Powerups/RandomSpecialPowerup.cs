using System.Collections.Generic;
using MHK.Core;
using MHK.Modding;
using MHK.Online;
using MHK.Race;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Powerups
{
    /// <summary>
    /// TINcan's question mark can. The user gets the special powerup of another driver at random, put into the item slot
    /// with a new roulette like a box item, and uses it later. The draw covers the specials of every driver of the race's
    /// content (<see cref="CollectPool"/>), never the user's own special or another random special. Without one the kart
    /// gets a random item box roll. Online the server draws and grants it (RaceLoaded.specialPool), and the roulette spins
    /// from the use until its answer arrives.
    /// </summary>
    public partial class RandomSpecialPowerup : PowerupBehaviour
    {
        [Tooltip("One-shot at the kart when the can is opened.")]
        [SerializeField] AudioClip revealSound;

        // Shared scratch list of the specials of one draw.
        [AutoStaticsCleanup] static readonly List<PowerupDefinition> pool = new();

        // The inventory clears the used can right after the launch. Online the roulette starts now and waits for the
        // server's ItemGranted. Offline the give waits for the first step, when the can is gone.
        protected override void OnLaunch()
        {
            if (Owner == null) return;
            if (revealSound != null) PlaySfx(revealSound, Owner.transform);
            if (!OnlineItems.Active) return;
            if (OnlineItems.IsReported(Owner)) Owner.Inventory.BeginPending(RaceProtocol.UseGrantBox);
            Remove();
        }

        protected override void Step(float dt)
        {
            if (Owner != null && !Owner.Eliminated && !Owner.IsRemote)
            {
                var pick = Draw();
                Owner.Inventory.Give(pick != null ? pick : PowerupRoller.Roll(Owner));
            }
            Remove();
        }

        PowerupDefinition Draw()
        {
            var race = RaceManager.Current;
            CollectPool(GameRoot.Instance.Content, null, race != null && race.IsBattle, pool);
            if (Owner.Character != null) pool.Remove(Owner.Character.specialPowerup);
            PowerupDefinition pick = null;
            if (pool.Count > 0)
                pick = pool[Mathf.Min(pool.Count - 1, (int)(RaceClock.Value01(OnlineItems.SeedOf(Usage), 0) * pool.Count))];
            pool.Clear();
            return pick;
        }

        /// <summary>
        /// Fills <paramref name="into"/> with the specials a random special may turn into, sorted by id. These are the
        /// specials of every loaded character, battle-only ones only in a <paramref name="battle"/>, never a random special
        /// itself. With <paramref name="lobbyPacks"/> (online) both the character and its special must be lobby content.
        /// </summary>
        public static void CollectPool(ContentRegistry content, IReadOnlyList<string> lobbyPacks, bool battle, List<PowerupDefinition> into)
        {
            into.Clear();
            var characters = content.All<CharacterDefinition>();
            for (var i = 0; i < characters.Count; i++)
            {
                var special = characters[i].specialPowerup;
                if (special == null || into.Contains(special) || IsRandomSpecial(special) || (special.battleOnly && !battle)) continue;
                if (lobbyPacks != null && (!ContentManifests.IsLobbyContent(content, characters[i], lobbyPacks)
                                           || !ContentManifests.IsLobbyContent(content, special, lobbyPacks))) continue;
                into.Add(special);
            }
            into.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        }

        /// <summary>Using <paramref name="def"/> grants a random special, as its use prefab carries this behaviour.</summary>
        public static bool IsRandomSpecial(PowerupDefinition def) =>
            def != null && def.usePrefab != null && def.usePrefab.GetComponent<RandomSpecialPowerup>() != null;
    }
}
