using System.Collections.Generic;
using MHK.Modding;
using UnityEngine;

namespace MHK.Cosmetics
{
    /// <summary>
    /// Puts cosmetics on a driver at its measured anchors (<see cref="CosmeticAnchors"/>). Each model hangs on the bone of
    /// its anchor, upright in the kart's frame of the measured pose, scaled from its <see cref="CosmeticDefinition.fitWidth"/>
    /// to the driver's head or shoulders and shifted by its <see cref="CosmeticDefinition.offset"/> at that scale. Capes
    /// get the driver's obstacles (<see cref="CosmeticAnchors.Obstacles"/>).
    /// </summary>
    public static class CosmeticDresser
    {
        // Limits of the fit to a driver, so a measurement gone wrong never makes a hat vanish or swallow the kart.
        const float MinFit = 0.4f, MaxFit = 3f;
        // Room (m in the kart's frame) between a rigid body item and the driver's back, the slices of its height that are
        // fitted to the back, and the furthest it tilts back to follow a back that slopes away below the neck (degrees).
        const float BackGap = 0.01f;
        const int BackSlices = 8;
        const float MaxBackTilt = 40f;

        /// <summary>Whether <paramref name="character"/> wears cosmetics of its own (<see cref="CharacterDefinition.ownCosmetics"/>).</summary>
        public static bool HasOwn(CharacterDefinition character)
        {
            if (character == null) return false;
            foreach (var cosmetic in character.ownCosmetics)
                if (cosmetic != null) return true;
            return false;
        }

        /// <summary>
        /// Dresses the driver of <paramref name="anchors"/> in <paramref name="choices"/>, one cosmetic per slot (the first
        /// of a slot wins), then in the own cosmetics of <paramref name="character"/> in the slots still empty. Ids that are
        /// not loaded, and cosmetics without a model, are skipped. The models take the layer of the bone they hang on.
        /// </summary>
        public static void Dress(CosmeticAnchors anchors, IReadOnlyList<CosmeticChoice> choices, CharacterDefinition character, ContentRegistry content)
        {
            if (anchors == null) return;
            var taken = new HashSet<CosmeticSlot>();
            if (choices != null && content != null)
                foreach (var choice in choices)
                    TryWear(anchors, content.Get<CosmeticDefinition>(choice.id), choice.color, taken);
            if (character == null) return;
            foreach (var own in character.ownCosmetics)
                if (own != null) TryWear(anchors, own, own.defaultColor, taken);
        }

        /// <summary>Takes off every cosmetic below <paramref name="driver"/>.</summary>
        public static void Undress(Transform driver)
        {
            if (driver == null) return;
            foreach (var item in driver.GetComponentsInChildren<CosmeticItem>(true)) Object.Destroy(item.gameObject);
        }

        static void TryWear(CosmeticAnchors anchors, CosmeticDefinition definition, Color32 color, HashSet<CosmeticSlot> taken)
        {
            if (definition == null || definition.prefab == null || taken.Contains(definition.slot)) return;
            var anchor = anchors.Of(definition.slot);
            if (!anchor.IsSet) return;
            taken.Add(definition.slot);
            var model = Wear(anchor, definition, color);
            var cloths = model.GetComponentsInChildren<CosmeticCloth>(true);
            foreach (var cloth in cloths) cloth.Obstacles = anchors.Obstacles;
            if (definition.slot == CosmeticSlot.Body && cloths.Length == 0) FitToBack(anchors, model);
        }

        // A rigid body item rests with its top front edge on the back below the neck, then tilts back around that edge until
        // its front clears the back over its whole height, so it lies on a back that leans forward or a round body.
        static void FitToBack(CosmeticAnchors anchors, GameObject model)
        {
            var frame = anchors.Frame;
            if (frame == null || !TryFrameBounds(frame, model, out var bounds) || bounds.size.y <= 0f) return;
            var top = bounds.max.y;
            var slice = bounds.size.y / BackSlices;
            var rest = anchors.BackDepth(top - slice, top) - BackGap;
            if (float.IsInfinity(rest)) return;
            model.transform.position += frame.TransformVector(0f, 0f, rest - bounds.max.z);
            var tilt = 0f;
            for (var k = 1; k < BackSlices; k++)
            {
                var sliceTop = top - k * slice;
                var back = anchors.BackDepth(sliceTop - slice, sliceTop) - BackGap;
                if (!float.IsInfinity(back)) tilt = Mathf.Max(tilt, (rest - back) / (top - sliceTop));
            }
            var degrees = Mathf.Min(MaxBackTilt, Mathf.Atan(tilt) * Mathf.Rad2Deg);
            if (degrees > 0f) model.transform.RotateAround(frame.TransformPoint(new Vector3(bounds.center.x, top, rest)), frame.right, degrees);
        }

        static bool TryFrameBounds(Transform frame, GameObject model, out Bounds bounds)
        {
            bounds = default;
            var found = false;
            foreach (var filter in model.GetComponentsInChildren<MeshFilter>())
            {
                if (filter.sharedMesh == null) continue;
                var toFrame = frame.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                for (var c = 0; c < 8; c++)
                {
                    var p = toFrame.MultiplyPoint3x4(Core.BoundsUtil.Corner(filter.sharedMesh.bounds, c));
                    if (found) bounds.Encapsulate(p);
                    else bounds = new Bounds(p, Vector3.zero);
                    found = true;
                }
            }
            return found;
        }

        static GameObject Wear(CosmeticAnchors.Anchor anchor, CosmeticDefinition definition, Color32 color)
        {
            var fit = definition.fitWidth > 0f && anchor.width > 0f ? Mathf.Clamp(anchor.width / definition.fitWidth, MinFit, MaxFit) : 1f;
            var scale = fit * anchor.frameToBone;
            var model = Object.Instantiate(definition.prefab, anchor.bone, false);
            model.name = "Cosmetic " + definition.Id;
            var t = model.transform;
            t.localRotation = anchor.rotation;
            t.localScale = definition.prefab.transform.localScale * scale;
            t.localPosition = anchor.position + anchor.rotation * (definition.offset * scale);
            var layer = anchor.bone.gameObject.layer;
            foreach (var child in model.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = layer;
            var item = model.TryGetComponent(out CosmeticItem own) ? own : model.AddComponent<CosmeticItem>();
            item.Wear(definition, color);
            return model;
        }
    }
}
