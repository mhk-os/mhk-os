using System.Collections.Generic;
using MHK.Core;
using MHK.Modding;
using MHK.Powerups;
using MHK.Race;
using Unity.Scripting.LifecycleManagement;

namespace MHK.AI
{
    /// <summary>What an item box gives, as far as a computer driver in a battle cares.</summary>
    internal enum BattleBoxKind : byte
    {
        /// <summary>A powerup for the item slot.</summary>
        Item,
        /// <summary>A status effect that restores health (Thunder health pickup).</summary>
        Health,
        /// <summary>A status effect that raises the damage the kart deals (Thunder quad damage).</summary>
        Power,
        /// <summary>Any other status effect.</summary>
        Other,
        /// <summary>No ground under the box (outside the drivable area): never driven to.</summary>
        Unreachable,
    }

    /// <summary>What each item box of the current track gives (boxes without ground under them are unreachable), cached per track.</summary>
    internal static partial class AIBattleBoxes
    {
        const float GroundAbove = 0.5f, GroundBelow = 3f;

        [AutoStaticsCleanup] static readonly Dictionary<ItemBox, BattleBoxKind> kinds = new();
        [AutoStaticsCleanup] static TrackRuntime cacheTrack;

        /// <summary>What <paramref name="box"/> gives.</summary>
        public static BattleBoxKind KindOf(ItemBox box)
        {
            if (cacheTrack != TrackRuntime.Current)
            {
                kinds.Clear();
                cacheTrack = TrackRuntime.Current;
            }
            if (kinds.TryGetValue(box, out var kind)) return kind;
            kind = Classify(box);
            kinds[box] = kind;
            return kind;
        }

        static BattleBoxKind Classify(ItemBox box)
        {
            var track = TrackRuntime.Current;
            if (track != null && !GroundProbe.Down(box.transform.position, GroundAbove, GroundBelow, track.ObstacleMask, out _)) return BattleBoxKind.Unreachable;
            if (box.GivesPowerups()) return BattleBoxKind.Item;
            var effect = GameRoot.Instance.Content.Get<StatusEffectDefinition>(box.fixedPowerupId);
            if (effect == null) return BattleBoxKind.Other;
            if (effect.heal > 0) return BattleBoxKind.Health;
            return effect.damageDealtMultiplier > 1f ? BattleBoxKind.Power : BattleBoxKind.Other;
        }
    }
}
