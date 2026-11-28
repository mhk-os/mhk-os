using System;
using System.Collections.Generic;
using MHK.Core;
using MHK.Cosmetics;
using MHK.Karts;
using MHK.Modding;
using UnityEngine;

namespace MHK.UI
{
    /// <summary>
    /// The highlighted character on its kart in the menu scene, built exactly like in races (<see cref="KartRigBuilder.Spawn"/>)
    /// in the driving pose, on the Menu layer (only the menu camera draws it). The turntable spin is <see cref="MenuStage"/>'s.
    /// </summary>
    public class MenuShowcase : MonoBehaviour
    {
        [SerializeField, Tooltip("Role the driver and kart play on the turntable (races show Drive while driving).")]
        CharacterAnimRole pose = CharacterAnimRole.Drive;
        [SerializeField, Tooltip("Playback rate of clips that follow the kart's speed (races: 0.05 when standing, 0.55 at full speed).")]
        float poseRate = 0.6f;

        GameObject shown;
        CharacterDefinition shownCharacter;
        KartDefinition shownKart;
        Difficulty shownClass;
        Transform driver;
        CosmeticAnchors anchors;
        readonly List<CosmeticChoice> worn = new();

        /// <summary>
        /// Shows <paramref name="character"/> on <paramref name="kart"/> with an engine of <paramref name="engineClass"/>,
        /// wearing <paramref name="cosmetics"/>. The model is rebuilt only when one of the first three changed. Other
        /// cosmetics are put on the shown driver, other colours only recolour them.
        /// </summary>
        public void Show(CharacterDefinition character, KartDefinition kart, Difficulty engineClass, IReadOnlyList<CosmeticChoice> cosmetics)
        {
            cosmetics ??= Array.Empty<CosmeticChoice>();
            if (character == shownCharacter && kart == shownKart && engineClass == shownClass && shown != null)
            {
                Dress(cosmetics);
                return;
            }
            shownCharacter = character;
            shownKart = kart;
            shownClass = engineClass;
            if (shown != null) Destroy(shown);
            shown = new GameObject("Shown");
            shown.transform.SetParent(transform, false);

            var rig = KartRigBuilder.Spawn(shown.transform, character, kart, engineClass, cosmetics);
            driver = rig.driver != null ? rig.driver.transform : null;
            anchors = rig.anchors;
            Remember(cosmetics);
            if (rig.driver != null) rig.driver.SetLocomotion(pose, 0f, poseRate);
            if (rig.kart != null) rig.kart.SetLocomotion(pose, 0f, poseRate);
            foreach (var t in shown.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = GameLayers.Menu;
        }

        void Dress(IReadOnlyList<CosmeticChoice> cosmetics)
        {
            if (driver == null) return;
            if (SameItems(cosmetics))
            {
                foreach (var item in driver.GetComponentsInChildren<CosmeticItem>())
                    foreach (var c in cosmetics)
                        if (string.Equals(c.id, item.Definition.Id, StringComparison.OrdinalIgnoreCase)) item.SetColor(c.color);
            }
            else
            {
                CosmeticDresser.Undress(driver);
                CosmeticDresser.Dress(anchors, cosmetics, shownCharacter, GameRoot.Instance.Content);
            }
            Remember(cosmetics);
        }

        bool SameItems(IReadOnlyList<CosmeticChoice> cosmetics)
        {
            if (cosmetics.Count != worn.Count) return false;
            for (var i = 0; i < worn.Count; i++)
                if (!string.Equals(cosmetics[i].id, worn[i].id, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        void Remember(IReadOnlyList<CosmeticChoice> cosmetics)
        {
            worn.Clear();
            worn.AddRange(cosmetics);
        }
    }
}
