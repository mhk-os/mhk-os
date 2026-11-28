using System.Collections.Generic;
using MHK.Modding;
using MHK.Rendering;
using UnityEngine;

namespace MHK.Cosmetics
{
    /// <summary>
    /// The root of a cosmetic a driver wears, added by <see cref="CosmeticDresser"/> when the model has none. A tintable
    /// cosmetic draws with its own copies of the model's materials, so <see cref="SetColor"/> recolours it without building
    /// it again. Materials in <see cref="KeepColor"/> are left as they are, such as painted details drawn over a tinted base.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CosmeticItem : MonoBehaviour
    {
        [SerializeField, Tooltip("Materials of the model that keep their colour when the player picks one, such as an alpha-clipped " +
                                 "detail layer (gems, straps, a ribbon) drawn over the tinted base.")]
        List<Material> keepColor = new();

        readonly List<Material> tinted = new();
        readonly List<Color> baseColors = new();

        /// <summary>Materials of the model that are never tinted.</summary>
        public IReadOnlyList<Material> KeepColor => keepColor;

        public CosmeticDefinition Definition { get; private set; }
        public Color32 Color { get; private set; }

        internal void Wear(CosmeticDefinition definition, Color32 color)
        {
            Definition = definition;
            if (definition.tintable) CopyMaterials();
            SetColor(color);
        }

        /// <summary>Multiplies the colours of the model's materials with <paramref name="color"/> (nothing when it is not tintable).</summary>
        public void SetColor(Color32 color)
        {
            Color = color;
            for (var i = 0; i < tinted.Count; i++)
            {
                var c = baseColors[i] * (Color)color;
                c.a = baseColors[i].a;
                tinted[i].SetColor(MHKLitIds.BaseColor, c);
            }
        }

        // One copy per material, shared by the model's renderers that use it.
        void CopyMaterials()
        {
            var copies = new Dictionary<Material, Material>();
            foreach (var r in GetComponentsInChildren<Renderer>(true))
            {
                var materials = r.sharedMaterials;
                var changed = false;
                for (var i = 0; i < materials.Length; i++)
                {
                    var m = materials[i];
                    if (m == null || !m.HasColor(MHKLitIds.BaseColor) || keepColor.Contains(m)) continue;
                    if (!copies.TryGetValue(m, out var copy))
                    {
                        copy = new Material(m) { name = m.name + " (tinted)" };
                        copies[m] = copy;
                        tinted.Add(copy);
                        baseColors.Add(m.GetColor(MHKLitIds.BaseColor));
                    }
                    materials[i] = copy;
                    changed = true;
                }
                if (changed) r.sharedMaterials = materials;
            }
        }

        void OnDestroy()
        {
            foreach (var m in tinted)
            {
                if (m == null) continue;
                if (Application.isPlaying) Destroy(m);
                else DestroyImmediate(m);
            }
            tinted.Clear();
        }
    }
}
