using MHK.Core;
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

        /// <summary>
        /// Shows <paramref name="character"/> on <paramref name="kart"/> with an engine of <paramref name="engineClass"/>,
        /// rebuilding the model only when one of them changed.
        /// </summary>
        public void Show(CharacterDefinition character, KartDefinition kart, Difficulty engineClass)
        {
            if (character == shownCharacter && kart == shownKart && engineClass == shownClass && shown != null) return;
            shownCharacter = character;
            shownKart = kart;
            shownClass = engineClass;
            if (shown != null) Destroy(shown);
            shown = new GameObject("Shown");
            shown.transform.SetParent(transform, false);

            var rig = KartRigBuilder.Spawn(shown.transform, character, kart, engineClass);
            if (rig.driver != null) rig.driver.SetLocomotion(pose, 0f, poseRate);
            if (rig.kart != null) rig.kart.SetLocomotion(pose, 0f, poseRate);
            foreach (var t in shown.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = GameLayers.Menu;
        }
    }
}
