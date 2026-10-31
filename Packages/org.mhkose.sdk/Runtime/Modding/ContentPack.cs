using System.Collections.Generic;
using UnityEngine;

namespace MHK.Modding
{
    /// <summary>
    /// A mod: a named bundle of content definitions. The original games ship as the 'core' mod exactly like third-party
    /// mods (an <c>.mhkmod</c> AssetBundle in the build's mods folder, see <see cref="ModLoader"/>) and can be disabled.
    /// </summary>
    [CreateAssetMenu(menuName = "MHK/Content Pack", fileName = "ContentPack")]
    public class ContentPack : ScriptableObject
    {
        [Tooltip("Unique pack id, also the id namespace of its content (e.g. 'mhk2').")]
        public string packId;
        public string title;
        public string author;
        [Tooltip("Commit of the repository the pack was built from, written by the mod build. Leave it empty in the project.")]
        public string version;
        [TextArea] public string description;
        [Tooltip("Default place in the mod load order: a higher value is higher in the Mods screen list and replaces content " +
                 "with the same id of the mods below (logged at load). The player can reorder mods.")]
        public int loadOrder;
        [Tooltip("Packs bundled inside this one (a mod made of several packs, e.g. 'core' = the packs of the original " +
                 "games). They load, enable and disable together with it and are not listed separately.")]
        public List<ContentPack> includes = new();
        [Tooltip("The pack's logo in the mods list (the game's own logo on the title and loading screens is " +
                 "GameConfig.gameLogo).")]
        public Texture2D logo;
        [Tooltip("Optional menu theme of this pack (a file in the pack's Music folder). The default main menu playlist " +
                 "plays the menu theme of every loaded pack.")]
        public AudioClip menuMusic;
        [Tooltip("Optional UI click sound.")]
        public AudioClip uiClick;
        [Tooltip("Item box look on the tracks of this game (tracks whose source game is this pack id). No model = the item " +
                 "box's own look (the game's default).")]
        public Powerups.ItemBoxAppearance itemBoxLook = new();

        [Header("Default race sounds (used when karts/tracks don't define their own)")]
        [Tooltip("Use this pack's default sounds for content whose own pack has none (the reference game).")]
        public bool preferredDefaults;
        public AudioClip engineLoop;
        [Tooltip("Skid / drift loop while the kart slides: volume = skid intensity, the same rule that draws the skid marks " +
                 "(MHK2 CAL_brake).")]
        public AudioClip skidLoop;
        [Tooltip("Hard collision (MHK2 CAL_crash).")]
        public AudioClip crashSound;
        public AudioClip landSound;
        public AudioClip countdownSound;
        public AudioClip goSound;
        public AudioClip itemPickupSound;
        [Tooltip("Light collision / wall scrape (MHK2 CAL_scratch: 0.75 to 1× the crash threshold).")]
        public AudioClip scratchSound;
        [Tooltip("Loop while driving on offroad ground without its own surface sound, louder with speed (MHK2 UGSAND).")]
        public AudioClip offroadLoop;

        public List<ContentDefinition> content = new();

        [Header("Build stamp (written by the mod build)")]
        [Tooltip("Mod API level (ModSdk.ApiLevel) of the SDK the mod's bundles were built with: the game loads only mods with its own level. 0 in the project and in bundles built before the level existed (they count as level 1).")]
        public int sdkApi;
        [Tooltip("Commit of the SDK (org.mhkose.sdk) the mod's bundles were built with. Empty in the project and in bundles built before the stamp existed.")]
        public string sdkVersion;
        [Tooltip("Unity version that built the mod's bundles.")]
        public string unityVersion;
        [Tooltip("Platform the bundles were built for, a BuildTarget name such as StandaloneOSX or Android.")]
        public string buildTarget;
        [Tooltip("Replaceable textures and sounds of the bundle with their paths, set by the mod build on the bundle's root pack. Empty in the project.")]
        public MHK.Assets.PackAssetIndex assetIndex;

        /// <summary>
        /// Resolves a default asset (sound, UI click): the <paramref name="owner"/> pack's own one (the track's or
        /// kart's game), else the one of a pack marked <see cref="preferredDefaults"/>, else the first loaded pack that has one.
        /// </summary>
        public static T Find<T>(IEnumerable<ContentPack> packs, System.Func<ContentPack, T> pick, ContentPack owner = null) where T : Object
        {
            if (owner != null)
            {
                var own = pick(owner);
                if (own != null) return own;
            }
            T fallback = null;
            foreach (var p in packs)
            {
                var c = pick(p);
                if (c == null) continue;
                if (p.preferredDefaults) return c;
                if (fallback == null) fallback = c;
            }
            return fallback;
        }

        /// <summary>This pack and all packs it includes, depth first (each pack once).</summary>
        public IEnumerable<ContentPack> SelfAndIncluded()
        {
            var seen = new HashSet<ContentPack>();
            var stack = new Stack<ContentPack>();
            stack.Push(this);
            while (stack.Count > 0)
            {
                var p = stack.Pop();
                if (p == null || !seen.Add(p)) continue;
                yield return p;
                for (var i = p.includes.Count - 1; i >= 0; i--) stack.Push(p.includes[i]);
            }
        }

        /// <summary>Definition count of this pack and everything it includes.</summary>
        public int TotalContentCount
        {
            get
            {
                var n = 0;
                foreach (var p in SelfAndIncluded()) n += p.content.Count;
                return n;
            }
        }

        public IEnumerable<T> GetAll<T>() where T : ContentDefinition
        {
            foreach (var c in content)
                if (c is T t) yield return t;
        }

        /// <summary>Definitions of type <typeparamref name="T"/> in this pack and every pack it includes.</summary>
        public IEnumerable<T> GetAllIncluded<T>() where T : ContentDefinition
        {
            foreach (var p in SelfAndIncluded())
            foreach (var d in p.GetAll<T>())
                yield return d;
        }
    }
}
