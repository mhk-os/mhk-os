using System;
using MHK.Core;
using MHK.Effects;
using UnityEngine;

namespace MHK.Cameras
{
    /// <summary>
    /// Speed feel of the race camera: widens the horizontal FOV of 2·atan(viewX) with the kart's speed and speed-rush
    /// boosts, and drives the screen-edge <see cref="SpeedLines"/> on the same camera.
    /// </summary>
    [Serializable]
    public class CameraSpeedFeel
    {
        const float FallbackAspect = 16f / 9f;
        const float MinAspect = 0.01f;

        [Tooltip("Horizontal view window: horizontal FOV = 2·atan(viewX).")]
        [SerializeField] private float viewX = 0.8f;
        [Tooltip("Speed rush (StatusEffectDefinition.cameraFovBoost): how fast the extra FOV builds up (1/s).")]
        [SerializeField] private float fovBoostRise = 4f;
        [Tooltip("How fast the speed-rush FOV eases back once the boost ends or the kart slows down (1/s).")]
        [SerializeField] private float fovBoostFall = 2f;
        [Tooltip("Share of the kart's normal top speed where the speed FOV (ViewPreset.speedFov) starts widening. Full " +
                 "at top speed, eased at both ends.")]
        [Range(0, 1)] [SerializeField] private float speedFovFrom = 0.3f;
        [Tooltip("Share of the kart's normal top speed where the edge effect (ViewPreset.speedLines) fades in. Full at " +
                 "top speed.")]
        [Range(0, 1)] [SerializeField] private float speedLinesFrom = 0.6f;
        [Tooltip("How quickly the speed FOV and the edge effect follow the kart's speed (1/s).")]
        [SerializeField] private float speedFollowRate = 3f;
        [Tooltip("Extra edge-effect strength of a speed rush (boost FOV) of boostLinesFullFov degrees or more.")]
        [Min(0)] [SerializeField] private float boostLines = 0.6f;
        [Tooltip("Boost FOV (degrees: StatusEffectDefinition.cameraFovBoost × speed) that adds the full boostLines.")]
        [Min(0.1f)] [SerializeField] private float boostLinesFullFov = 10f;
        [Tooltip("Upper limit of the extra horizontal FOV (speed FOV + boost FOV, degrees).")]
        [Min(0)] [SerializeField] private float maxExtraFov = 20f;

        Camera cam;
        SpeedLines lines;
        float fovBoost;
        float shownSpeed01;

        /// <summary>Sets the camera whose FOV is driven and its optional edge effect.</summary>
        public void Bind(Camera camera, SpeedLines speedLines)
        {
            cam = camera;
            lines = speedLines;
        }

        /// <summary>Drops the eased speed and boost so the next <see cref="Tick"/> starts from rest.</summary>
        public void ResetState()
        {
            fovBoost = 0f;
            shownSpeed01 = 0f;
        }

        /// <summary>Turns the edge effect off (no target to follow).</summary>
        public void Hide()
        {
            if (lines != null) lines.Set(0f, 0f, 0f);
        }

        /// <summary>
        /// Eases towards <paramref name="boostGoal"/> (boost FOV, degrees) and <paramref name="speed01"/> (share of the
        /// top speed), then applies the edge effect and the FOV. Inactive views get the base FOV and no edge effect.
        /// </summary>
        public void Tick(float boostGoal, float speed01, bool active, float presetFov, float presetLines, float dt)
        {
            fovBoost = Mathf.Lerp(fovBoost, boostGoal, Smoothing.Factor(boostGoal > fovBoost ? fovBoostRise : fovBoostFall, dt));
            shownSpeed01 = Mathf.Lerp(shownSpeed01, speed01, Smoothing.Factor(speedFollowRate, dt));
            var extraFov = active ? Mathf.Min(maxExtraFov, presetFov * SpeedRamp(speedFovFrom, shownSpeed01) + fovBoost) : 0f;
            if (lines != null)
            {
                var strength = active
                    ? presetLines * SpeedRamp(speedLinesFrom, shownSpeed01) + boostLines * Mathf.Clamp01(fovBoost / boostLinesFullFov)
                    : 0f;
                lines.Set(strength, shownSpeed01 + fovBoost / boostLinesFullFov, dt);
            }
            ApplyFov(extraFov);
        }

        void ApplyFov(float extraFov)
        {
            var aspect = cam.aspect > MinAspect ? cam.aspect : FallbackAspect;
            var hfov = 2f * Mathf.Atan(viewX) * Mathf.Rad2Deg + extraFov;
            cam.fieldOfView = Camera.HorizontalToVerticalFieldOfView(hfov, aspect);
        }

        static float SpeedRamp(float from, float speed01) => Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(from, 1f, speed01));
    }
}
