using System.Collections.Generic;
using MHK.Core;
using MHK.Karts;
using MHK.Modding;
using MHK.Online;
using MHK.Race;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Powerups
{
    /// <summary>
    /// Items in an online race between humans (<see cref="OnlineRace"/>, not solo): the server arbitrates item boxes,
    /// item uses and hits. A pickup of the local kart waits for ItemGranted; uses are announced (ItemUse) and replayed
    /// by the others (ItemUsed, fast-forwarded to the current tick); every client simulates every use and reports the
    /// projectile positions; each racer's client decides the hits on its own kart (HitTaken) and only reports those it
    /// sees on others (HitSeen). Battle eliminations and collapsing bridges are shared here too. Offline and in solo
    /// races nothing here is active.
    /// </summary>
    public static partial class OnlineItems
    {
        [AutoStaticsCleanup] static OnlineRace race;
        [AutoStaticsCleanup] static readonly List<ItemBox> boxes = new();
        [AutoStaticsCleanup] static readonly List<CollapsingBridge> bridges = new();
        [AutoStaticsCleanup] static readonly Dictionary<int, Vector3> remoteVelocity = new();
        [AutoStaticsCleanup] static uint offlineSalt;

        /// <summary>Items are arbitrated by the server in the running race.</summary>
        public static bool Active => race != null && race == OnlineRace.Current;

        /// <summary>
        /// Starts arbitration for <paramref name="r"/> once its track and karts are loaded (call before sending RaceLoaded):
        /// indexes item boxes and bridges in their stable order and listens to the race's pushes. Solo races stay offline.
        /// </summary>
        public static void Begin(OnlineRace r)
        {
            Stop();
            if (r == null || r.Solo) return;
            race = r;
            StableOrder.Collect(boxes);
            for (var i = 0; i < boxes.Count; i++) boxes[i].NetId = i;
            StableOrder.Collect(bridges);
            for (var i = 0; i < bridges.Count; i++) bridges[i].NetId = i;
            r.Pushed += OnPushed;
            r.RemoteState += OnRemoteState;
            if (!r.TryGetComponent<OnlineItemsTicker>(out var ticker)) ticker = r.gameObject.AddComponent<OnlineItemsTicker>();
            ticker.race = r;
        }

        /// <summary>Ends arbitration (the race ended or a new one begins).</summary>
        internal static void Stop()
        {
            if (!ReferenceEquals(race, null) && race != null)
            {
                race.Pushed -= OnPushed;
                race.RemoteState -= OnRemoteState;
            }
            race = null;
            boxes.Clear();
            bridges.Clear();
            remoteVelocity.Clear();
            ResetUses();
        }

        /// <summary>Ends arbitration if it runs for <paramref name="r"/> (its race object goes away).</summary>
        internal static void StopFor(OnlineRace r)
        {
            if (ReferenceEquals(race, r)) Stop();
        }

        /// <summary>The kart is the local racer's in an arbitrated race (online karts are either that one or remote).</summary>
        public static bool IsLocal(Kart k) => Active && k != null && !k.IsRemote;

        static Kart LocalKart => race.KartOf(race.LocalSlot);

        static ContentRegistry Content => GameRoot.Instance.Content;

        /// <summary>
        /// Velocity of <paramref name="k"/>: a remote racer's as its latest state says (its stand-in is kinematic), else
        /// its body's.
        /// </summary>
        public static Vector3 VelocityOf(Kart k)
        {
            if (k == null) return Vector3.zero;
            if (!k.IsRemote) return k.Physics.Body.linearVelocity;
            return Active && remoteVelocity.TryGetValue(race.SlotOf(k), out var v) ? v : Vector3.zero;
        }

        /// <summary>
        /// Seed of the random choices of powerup use <paramref name="usage"/> (scattered hazards): online the race seed
        /// and the use id, so every client scatters alike; offline a per-session salt and the use.
        /// </summary>
        public static uint SeedOf(int usage)
        {
            uint seed;
            if (Active) seed = (uint)race.Setup.seed ^ (uint)(race.Setup.seed >> 32);
            else
            {
                if (offlineSalt == 0) offlineSalt = (uint)Random.Range(1, int.MaxValue);
                seed = offlineSalt;
            }
            return seed ^ unchecked((uint)usage * 0x85EBCA6Bu);
        }

        static void OnRemoteState(int slot, KartState s) => remoteVelocity[slot] = s.vel;

        static void OnPushed(IRacePush push)
        {
            switch (push)
            {
                case ItemGranted m: OnGranted(m); break;
                case ItemBoxTaken m: OnBoxTaken(m); break;
                case ItemDenied m: OnDenied(m); break;
                case ItemUsed m: SpawnRemote(m); break;
                case ItemUseRejected m: RemoveUse(m.useId); break;
                case ProjectileCorrect m: Correct(m); break;
                case HitApplied m: OnHitApplied(m); break;
                case HitForce m: OnHitForce(m); break;
                case Eliminated m: OnEliminated(m); break;
                case BridgeTriggered m: OnBridgeTriggered(m); break;
            }
        }

        // ---- Item boxes -------------------------------------------------------------------------------------------

        static ItemBox BoxOf(int id) => id >= 0 && id < boxes.Count ? boxes[id] : null;

        /// <summary>
        /// The local kart touched an arbitrated item box: asks the server for an item and starts the roulette. False when
        /// the kart cannot take one now (the box stays).
        /// </summary>
        internal static bool RequestPickup(ItemBox box, Kart k)
        {
            if (box.NetId < 0 || !k.Inventory.CanReceive) return false;
            race.Notify(new ItemPickup { raceId = race.RaceId, boxId = box.NetId, tick = race.Tick, pos = k.transform.position });
            k.Inventory.BeginPending(box.NetId);
            return true;
        }

        static void OnGranted(ItemGranted m)
        {
            var k = LocalKart;
            if (k != null) k.Inventory.Grant(m.boxId, Content.Get<PowerupDefinition>(m.powerupId), m.charges);
        }

        static void OnBoxTaken(ItemBoxTaken m)
        {
            var box = BoxOf(m.boxId);
            if (box != null) box.HideTaken(race.Tick - m.tick);
        }

        // A box another racer got first stays hidden: it is not there for anyone.
        static void OnDenied(ItemDenied m)
        {
            var k = LocalKart;
            if (k != null) k.Inventory.Deny(m.boxId);
            var box = BoxOf(m.boxId);
            if (box != null && m.reason != ItemDenied.NotAvailable) box.Restore();
        }

        // ---- Battles and bridges ----------------------------------------------------------------------------------

        /// <summary>The local kart is out of the battle (health below 1): tells the others who knocked it out.</summary>
        internal static void SendEliminatedSelf(Kart k)
        {
            if (!IsLocal(k)) return;
            race.Notify(new EliminatedSelf { raceId = race.RaceId, attackerSlot = race.SlotOf(k.Effects.LastAttacker), tick = race.Tick });
        }

        static void OnEliminated(Eliminated m)
        {
            var k = race.KartOf(m.slot);
            var battle = RaceManager.Current != null ? RaceManager.Current.Battle : null;
            if (k != null && battle != null) battle.EliminateShared(k);
        }

        /// <summary>The local kart set <paramref name="bridge"/> off at <paramref name="tick"/>; the server's first trigger wins.</summary>
        internal static void SendBridgeTrigger(CollapsingBridge bridge, int tick)
        {
            if (bridge.NetId < 0) return;
            race.Notify(new BridgeTrigger { raceId = race.RaceId, bridgeId = bridge.NetId, tick = tick });
        }

        static void OnBridgeTriggered(BridgeTriggered m)
        {
            if (m.bridgeId >= 0 && m.bridgeId < bridges.Count && bridges[m.bridgeId] != null) bridges[m.bridgeId].SetSharedTrigger(m.tick);
        }
    }

    /// <summary>Per-step work of <see cref="OnlineItems"/> (projectile reports, ended uses); lives on the online race object.</summary>
    [DefaultExecutionOrder(1100)]
    sealed class OnlineItemsTicker : MonoBehaviour
    {
        internal OnlineRace race;

        void FixedUpdate() => OnlineItems.Tick();

        void OnDestroy() => OnlineItems.StopFor(race);
    }
}
