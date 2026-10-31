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
    /// Items in an online race (<see cref="OnlineRace"/>): the server arbitrates item boxes, item uses and hits. A pickup
    /// waits for ItemGranted; uses are announced (ItemUse) and replayed by the others (ItemUsed, fast-forwarded to the
    /// current tick); every client simulates every use and reports the projectile positions; each racer's client decides
    /// the hits on its own kart (HitTaken) and only reports those it sees on other humans (HitSeen). AI karts run on
    /// every client: the AI host reports their pickups, uses, hits, eliminations and finishes like its own kart's
    /// (<see cref="IsReported"/>); everywhere else they are mirrors (<see cref="IsMirror"/>) that get those from the
    /// server only. Battle eliminations and collapsing bridges are shared here too. Offline nothing here is active.
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
        /// indexes item boxes and bridges in their stable order and listens to the race's pushes.
        /// </summary>
        public static void Begin(OnlineRace r)
        {
            Stop();
            if (r == null) return;
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

        /// <summary>
        /// The kart's item events go to the server from this client: the local racer's kart, or an AI kart while this
        /// client is the AI host. Remote racers' stand-ins and mirrored AI karts are not.
        /// </summary>
        public static bool IsReported(Kart k) => Active && k != null && !k.IsRemote && (!race.IsAiKart(k) || race.IsAiHost);

        /// <summary>
        /// An AI kart simulated here while another client is the AI host: it never takes boxes, uses items or takes hits
        /// from uses by itself; the server's ItemGranted, ItemUsed, HitApplied and Eliminated drive those.
        /// </summary>
        public static bool IsMirror(Kart k) => Active && k != null && !race.IsAiHost && race.IsAiKart(k);

        static Kart LocalKart => race.KartOf(race.LocalSlot);

        // The slot field of a notify about k: none for the local kart, the AI kart's slot for a hosted one.
        static int NotifySlot(Kart k) => race.IsAiKart(k) ? race.SlotOf(k) : RaceProtocol.NoSlot;

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
        /// A reported kart (<see cref="IsReported"/>) touched an arbitrated item box: asks the server for an item and starts
        /// the roulette. False when the kart cannot take one now or is not reported from here (the box stays).
        /// </summary>
        internal static bool RequestPickup(ItemBox box, Kart k)
        {
            if (box.NetId < 0 || !k.Inventory.CanReceive || !IsReported(k)) return false;
            race.Notify(new ItemPickup { raceId = race.RaceId, boxId = box.NetId, tick = race.Tick, pos = k.transform.position, slot = NotifySlot(k) });
            k.Inventory.BeginPending(box.NetId);
            return true;
        }

        // Own pickups end their roulette; an AI kart's item (sent to everyone) goes into every client's copy of that kart.
        static void OnGranted(ItemGranted m)
        {
            var k = race.KartOf(m.slot);
            if (k == null) return;
            var def = Content.Get<PowerupDefinition>(m.powerupId);
            if (!race.IsAiKart(k) || k.Inventory.IsPending(m.boxId))
            {
                k.Inventory.Grant(m.boxId, def, m.charges);
                return;
            }
            var ago = Mathf.Max(0, race.Tick - m.tick);
            k.Inventory.ReceiveShared(def, m.charges, ago * Time.fixedDeltaTime);
            var box = BoxOf(m.boxId);
            if (box != null) box.HideTaken(ago);
        }

        static void OnBoxTaken(ItemBoxTaken m)
        {
            var box = BoxOf(m.boxId);
            if (box != null) box.HideTaken(race.Tick - m.tick);
        }

        // A box another racer got first stays hidden: it is not there for anyone.
        static void OnDenied(ItemDenied m)
        {
            var k = race.KartOf(m.slot);
            if (k != null) k.Inventory.Deny(m.boxId);
            var box = BoxOf(m.boxId);
            if (box != null && m.reason != ItemDenied.NotAvailable) box.Restore();
        }

        // ---- Battles and bridges ----------------------------------------------------------------------------------

        /// <summary>A reported kart is out of the battle (health below 1): tells the others who knocked it out.</summary>
        internal static void SendEliminatedSelf(Kart k)
        {
            if (!IsReported(k)) return;
            race.Notify(new EliminatedSelf
            {
                raceId = race.RaceId, attackerSlot = race.SlotOf(k.Effects.LastAttacker), tick = race.Tick, slot = NotifySlot(k),
            });
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
