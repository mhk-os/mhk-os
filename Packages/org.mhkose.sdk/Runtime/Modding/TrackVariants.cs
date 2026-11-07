using System;
using System.Collections.Generic;
using System.Text;
using MHK.Core;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Modding
{
    /// <summary>
    /// The variants tracks offer, gathered from the registered <see cref="TrackOverlayDefinition"/>s. A variant is one option
    /// per offered modifier; its key names the options that differ from the track's defaults (<see cref="TrackVariant.Key"/>),
    /// so "" is the track as it is by default and adding an optional modifier to a track keeps its other keys.
    /// </summary>
    public static partial class TrackVariants
    {
        /// <summary>Separator of the option ids in a variant key.</summary>
        public const char Separator = '+';
        /// <summary>Longest variant key.</summary>
        public const int MaxKeyLength = 128;
        /// <summary>Most options a variant key names.</summary>
        public const int MaxKeyOptions = 8;
        /// <summary>Longest option id in a variant key.</summary>
        public const int MaxOptionIdLength = 64;

        [AutoStaticsCleanup] static readonly Dictionary<TrackDefinition, TrackVariantSet> cache = new();
        [AutoStaticsCleanup] static ContentRegistry cacheRegistry;
        [AutoStaticsCleanup] static int cacheVersion = -1;

        /// <summary>What <paramref name="track"/> offers with the content registered in the game now.</summary>
        public static TrackVariantSet Of(TrackDefinition track) => Of(track, GameRoot.Instance != null ? GameRoot.Instance.Content : null);

        /// <summary>What <paramref name="track"/> offers with the content of <paramref name="registry"/> (cached per registry version).</summary>
        public static TrackVariantSet Of(TrackDefinition track, ContentRegistry registry)
        {
            if (track == null) return null;
            if (registry != cacheRegistry || registry == null || registry.Version != cacheVersion)
            {
                cache.Clear();
                cacheRegistry = registry;
                cacheVersion = registry != null ? registry.Version : -1;
            }
            if (cache.TryGetValue(track, out var set)) return set;
            set = new TrackVariantSet(track, registry);
            cache[track] = set;
            return set;
        }

        /// <summary>
        /// The variant <paramref name="key"/> of <paramref name="track"/>, null when the key is malformed or names an option
        /// the track does not offer.
        /// </summary>
        public static TrackVariant Resolve(TrackDefinition track, string key) => Of(track)?.Resolve(key);

        /// <summary>
        /// Overlays of <paramref name="registry"/> that another pack's overlay for the same track and option replaces
        /// through the load order (whatever their ids): each with the overlay that wins instead. Online play and uploads
        /// treat such a variant as modified content, as an id replaced by another pack.
        /// </summary>
        public static List<(TrackOverlayDefinition replaced, TrackOverlayDefinition by)> ReplacedOverlays(ContentRegistry registry)
        {
            var result = new List<(TrackOverlayDefinition, TrackOverlayDefinition)>();
            if (registry == null) return result;
            var winners = new Dictionary<(string track, string option), (TrackOverlayDefinition overlay, int rank)>();
            var all = registry.All<TrackOverlayDefinition>();
            foreach (var overlay in all)
            {
                var pair = (overlay.trackId?.ToLowerInvariant() ?? "", overlay.optionId?.ToLowerInvariant() ?? "");
                var rank = TrackVariantSet.RankOf(registry, overlay);
                if (!winners.TryGetValue(pair, out var winner) || rank >= winner.rank) winners[pair] = (overlay, rank);
            }
            foreach (var overlay in all)
            {
                var pair = (overlay.trackId?.ToLowerInvariant() ?? "", overlay.optionId?.ToLowerInvariant() ?? "");
                var winner = winners[pair].overlay;
                if (winner != overlay && winner.OwnerPack != overlay.OwnerPack) result.Add((overlay, winner));
            }
            return result;
        }

        /// <summary>
        /// Whether <paramref name="key"/> is well-formed: empty, or 1 to <see cref="MaxKeyOptions"/> distinct lowercase option
        /// ids of [a-z0-9_.-] joined by <see cref="Separator"/>, at most <see cref="MaxKeyLength"/> characters. The server
        /// checks the same.
        /// </summary>
        public static bool IsValidKey(string key)
        {
            if (key == null) return false;
            if (key.Length == 0) return true;
            if (key.Length > MaxKeyLength) return false;
            var tokens = key.Split(Separator);
            if (tokens.Length > MaxKeyOptions) return false;
            for (var i = 0; i < tokens.Length; i++)
            {
                if (!IsValidOptionId(tokens[i])) return false;
                for (var j = 0; j < i; j++)
                    if (tokens[j] == tokens[i]) return false;
            }
            return true;
        }

        /// <summary>
        /// Whether option id <paramref name="id"/> can be part of a variant key: 1 to <see cref="MaxOptionIdLength"/>
        /// characters of [a-z0-9_.-]. Options with other ids are not offered (keys travel online and into file names).
        /// </summary>
        public static bool IsValidOptionId(string id)
        {
            if (id.Length == 0 || id.Length > MaxOptionIdLength) return false;
            foreach (var c in id)
                if (!(c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || c is '_' or '.' or '-'))
                    return false;
            return true;
        }
    }

    /// <summary>The options one modifier offers on a track (one overlay definition each, in option order) and its default.</summary>
    public sealed class TrackModifierChoice
    {
        /// <summary>Id of the modifier (its definition may be missing: then the id is all there is).</summary>
        public string ModifierId { get; }
        /// <summary>The modifier's definition, null when no loaded pack defines it.</summary>
        public TrackModifierDefinition Modifier { get; }
        /// <summary>The offered options as their overlay definitions, in option order.</summary>
        public IReadOnlyList<TrackOverlayDefinition> Overlays { get; }
        /// <summary>The options of <see cref="Overlays"/>, same order.</summary>
        public IReadOnlyList<TrackOptionDefinition> Options { get; }
        /// <summary>Index into <see cref="Overlays"/> of the track's default option.</summary>
        public int DefaultIndex { get; }

        /// <summary>The player can pick between options (two or more offered).</summary>
        public bool IsChoice => Overlays.Count > 1;

        /// <summary>Menu name of the modifier.</summary>
        public string DisplayName => Modifier != null ? Modifier.DisplayName : ModifierId;

        internal int Order => Modifier != null ? Modifier.order : int.MaxValue;

        internal TrackModifierChoice(string modifierId, TrackModifierDefinition modifier, List<TrackOverlayDefinition> overlays,
            List<TrackOptionDefinition> options, int defaultIndex)
        {
            ModifierId = modifierId;
            Modifier = modifier;
            Overlays = overlays;
            Options = options;
            DefaultIndex = defaultIndex;
        }

        /// <summary>Index of the option <paramref name="optionId"/> in <see cref="Options"/>, -1 when not offered.</summary>
        public int IndexOf(string optionId)
        {
            for (var i = 0; i < Options.Count; i++)
                if (string.Equals(Options[i].Id, optionId, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }
    }

    /// <summary>
    /// Everything a track offers: the modifiers with their options (modifier order) and the variants made of them. Built by
    /// <see cref="TrackVariants.Of(TrackDefinition)"/>.
    /// </summary>
    public sealed class TrackVariantSet
    {
        readonly Dictionary<string, TrackVariant> byKey = new(StringComparer.Ordinal);

        /// <summary>The track.</summary>
        public TrackDefinition Track { get; }
        /// <summary>The offered modifiers in modifier order (also those with a single option, whose rules always apply).</summary>
        public IReadOnlyList<TrackModifierChoice> Modifiers { get; }
        /// <summary>The track's default variant (key "").</summary>
        public TrackVariant Default { get; }

        /// <summary>Some modifier offers the player a choice.</summary>
        public bool HasChoices
        {
            get
            {
                foreach (var m in Modifiers)
                    if (m.IsChoice) return true;
                return false;
            }
        }

        internal TrackVariantSet(TrackDefinition track, ContentRegistry registry)
        {
            Track = track;
            Modifiers = Gather(track, registry);
            var defaults = new int[Modifiers.Count];
            for (var i = 0; i < defaults.Length; i++) defaults[i] = Modifiers[i].DefaultIndex;
            Default = Make(defaults);
        }

        /// <summary>
        /// The variant named by <paramref name="key"/> ("" or null = <see cref="Default"/>), null when the key is malformed,
        /// names an option the track does not offer or two options of one modifier.
        /// </summary>
        public TrackVariant Resolve(string key)
        {
            if (string.IsNullOrEmpty(key)) return Default;
            if (byKey.TryGetValue(key, out var known)) return known;
            if (!TrackVariants.IsValidKey(key)) return null;
            var picks = DefaultPicks();
            var set = new bool[Modifiers.Count];
            foreach (var token in key.Split(TrackVariants.Separator))
            {
                var found = false;
                for (var m = 0; m < Modifiers.Count && !found; m++)
                {
                    var index = Modifiers[m].IndexOf(token);
                    if (index < 0) continue;
                    if (set[m]) return null;
                    set[m] = true;
                    picks[m] = index;
                    found = true;
                }
                if (!found) return null;
            }
            return Make(picks);
        }

        /// <summary><paramref name="variant"/> with option <paramref name="optionIndex"/> of modifier <paramref name="modifierIndex"/> instead.</summary>
        public TrackVariant With(TrackVariant variant, int modifierIndex, int optionIndex)
        {
            var picks = variant != null && variant.Set == this ? variant.Picks() : DefaultPicks();
            picks[modifierIndex] = Mathf.Clamp(optionIndex, 0, Modifiers[modifierIndex].Overlays.Count - 1);
            return Make(picks);
        }

        /// <summary>A random variant: every modifier with a choice takes one of its options with equal chance.</summary>
        public TrackVariant Random(System.Random rng)
        {
            var picks = DefaultPicks();
            for (var i = 0; i < picks.Length; i++)
                if (Modifiers[i].IsChoice) picks[i] = rng.Next(Modifiers[i].Overlays.Count);
            return Make(picks);
        }

        int[] DefaultPicks()
        {
            var picks = new int[Modifiers.Count];
            for (var i = 0; i < picks.Length; i++) picks[i] = Modifiers[i].DefaultIndex;
            return picks;
        }

        TrackVariant Make(int[] picks)
        {
            var variant = new TrackVariant(this, picks);
            if (byKey.TryGetValue(variant.Key, out var known)) return known;
            byKey[variant.Key] = variant;
            return variant;
        }

        // For each (track, option) the overlay of the pack registered last wins; options are grouped by modifier, a
        // modifier's default is its last-registered default overlay, else its first option. Options whose id cannot be part
        // of a variant key are left out.
        static List<TrackModifierChoice> Gather(TrackDefinition track, ContentRegistry registry)
        {
            var result = new List<TrackModifierChoice>();
            if (registry == null) return result;
            var byOption = new Dictionary<string, (TrackOverlayDefinition overlay, TrackOptionDefinition option, int rank)>(StringComparer.OrdinalIgnoreCase);
            foreach (var overlay in registry.All<TrackOverlayDefinition>())
            {
                if (!string.Equals(overlay.trackId, track.Id, StringComparison.OrdinalIgnoreCase)) continue;
                var option = registry.Get<TrackOptionDefinition>(overlay.optionId);
                if (option == null || string.IsNullOrEmpty(option.modifierId)) continue;
                if (!TrackVariants.IsValidOptionId(option.Id.ToLowerInvariant()))
                {
                    Debug.LogWarning($"[Content] Track option '{option.Id}' is not offered: its id must be 1 to " +
                                     $"{TrackVariants.MaxOptionIdLength} characters of a-z, 0-9, '_', '.' and '-'.");
                    continue;
                }
                var rank = RankOf(registry, overlay);
                if (byOption.TryGetValue(option.Id, out var existing) && existing.rank > rank) continue;
                byOption[option.Id] = (overlay, option, rank);
            }
            var byModifier = new Dictionary<string, List<(TrackOverlayDefinition overlay, TrackOptionDefinition option, int rank)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in byOption.Values)
            {
                if (!byModifier.TryGetValue(entry.option.modifierId, out var list)) byModifier[entry.option.modifierId] = list = new();
                list.Add(entry);
            }
            foreach (var pair in byModifier)
            {
                var list = pair.Value;
                list.Sort((a, b) => a.option.order != b.option.order
                    ? a.option.order.CompareTo(b.option.order)
                    : string.Compare(a.option.Id, b.option.Id, StringComparison.OrdinalIgnoreCase));
                var overlays = new List<TrackOverlayDefinition>(list.Count);
                var options = new List<TrackOptionDefinition>(list.Count);
                int defaultIndex = 0, defaultRank = int.MinValue;
                for (var i = 0; i < list.Count; i++)
                {
                    overlays.Add(list[i].overlay);
                    options.Add(list[i].option);
                    if (list[i].overlay.isDefault && list[i].rank >= defaultRank)
                    {
                        defaultIndex = i;
                        defaultRank = list[i].rank;
                    }
                }
                result.Add(new TrackModifierChoice(pair.Key, registry.Get<TrackModifierDefinition>(pair.Key), overlays, options, defaultIndex));
            }
            result.Sort((a, b) => a.Order != b.Order
                ? a.Order.CompareTo(b.Order)
                : string.Compare(a.ModifierId, b.ModifierId, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        // Later registered packs rank higher (the registry's pack order is the load order, bottom first).
        internal static int RankOf(ContentRegistry registry, ContentDefinition def)
        {
            var packs = registry.Packs;
            for (var i = 0; i < packs.Count; i++)
                if (packs[i] == def.OwnerPack) return i;
            return -1;
        }
    }

    /// <summary>
    /// One variant of a track: an option per offered modifier, the overlays to load and the combined gameplay rules. Made by
    /// <see cref="TrackVariantSet"/>, equal variants are the same object.
    /// </summary>
    public sealed class TrackVariant
    {
        readonly int[] picks;
        readonly TrackOverlayDefinition[] selected;

        /// <summary>The set this variant belongs to.</summary>
        public TrackVariantSet Set { get; }
        /// <summary>The track.</summary>
        public TrackDefinition Track => Set.Track;
        /// <summary>
        /// The options that differ from the track's defaults as lowercase option ids in modifier order, joined by
        /// <see cref="TrackVariants.Separator"/>: "" for the default variant.
        /// </summary>
        public string Key { get; }
        /// <summary>The selected option of each offered modifier (as its overlay definition), modifier order.</summary>
        public IReadOnlyList<TrackOverlayDefinition> Selected => selected;
        /// <summary>The combined rules of the selected options.</summary>
        public TrackOptionRules Rules { get; }
        /// <summary>
        /// Version of the driving content of this variant: the track's version plus every selected overlay's bumps
        /// (<see cref="TrackOverlayDefinition.contentVersion"/> − 1). Records of another version do not count.
        /// </summary>
        public int ContentVersion { get; }

        /// <summary>The track's default variant.</summary>
        public bool IsDefault => Key.Length == 0;
        public bool Reverse => Rules.reverse;
        public bool Headlights => Rules.headlights;
        public float OutdoorGrip => Rules.outdoorGrip;
        public float Wetness => Rules.wetness;

        internal TrackVariant(TrackVariantSet set, int[] picks)
        {
            Set = set;
            this.picks = picks;
            var modifiers = set.Modifiers;
            selected = new TrackOverlayDefinition[modifiers.Count];
            Rules = new TrackOptionRules();
            var key = new StringBuilder();
            var version = set.Track.contentVersion;
            for (var i = 0; i < modifiers.Count; i++)
            {
                var m = modifiers[i];
                selected[i] = m.Overlays[picks[i]];
                Rules.Combine(m.Options[picks[i]].rules);
                version += Mathf.Max(1, selected[i].contentVersion) - 1;
                if (picks[i] == m.DefaultIndex) continue;
                if (key.Length > 0) key.Append(TrackVariants.Separator);
                key.Append(m.Options[picks[i]].Id.ToLowerInvariant());
            }
            Key = key.ToString();
            ContentVersion = version;
        }

        internal int[] Picks() => (int[])picks.Clone();

        /// <summary>Index of the selected option of modifier <paramref name="modifierIndex"/> in its <see cref="TrackModifierChoice.Options"/>.</summary>
        public int PickOf(int modifierIndex) => picks[modifierIndex];

        /// <summary>The option of modifier <paramref name="modifierIndex"/> this variant selects.</summary>
        public TrackOptionDefinition OptionOf(int modifierIndex) => Set.Modifiers[modifierIndex].Options[picks[modifierIndex]];

        /// <summary>Whether the variant selects the option <paramref name="optionId"/>.</summary>
        public bool Has(string optionId)
        {
            for (var i = 0; i < picks.Length; i++)
                if (string.Equals(OptionOf(i).Id, optionId, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Menu names of the options that differ from the track's defaults, joined by <paramref name="separator"/>; "" for the default variant.</summary>
        public string Label(string separator = " · ")
        {
            var sb = new StringBuilder();
            for (var i = 0; i < picks.Length; i++)
            {
                if (picks[i] == Set.Modifiers[i].DefaultIndex) continue;
                if (sb.Length > 0) sb.Append(separator);
                sb.Append(OptionOf(i).DisplayName);
            }
            return sb.ToString();
        }

        /// <summary>Menu names of every selected option of a modifier with a choice, joined by <paramref name="separator"/>.</summary>
        public string FullLabel(string separator = " · ")
        {
            var sb = new StringBuilder();
            for (var i = 0; i < picks.Length; i++)
            {
                if (!Set.Modifiers[i].IsChoice) continue;
                if (sb.Length > 0) sb.Append(separator);
                sb.Append(OptionOf(i).DisplayName);
            }
            return sb.ToString();
        }

        public override string ToString() => $"{Track.Id}[{Key}]";
    }
}
