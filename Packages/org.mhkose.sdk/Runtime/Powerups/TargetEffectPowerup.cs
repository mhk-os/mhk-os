using System.Collections;
using System.Collections.Generic;
using MHK.Karts;
using MHK.Modding;
using UnityEngine;

namespace MHK.Powerups
{
    /// <summary>
    /// Applies an effect directly to selected opponents (16-tons weight on the leader, flash on everybody ahead, ...),
    /// optionally dropping a visual onto each target during <see cref="delay"/>. The activation sound plays at once,
    /// then the hit sound at each target, or the fail sound when there is none.
    /// </summary>
    public class TargetEffectPowerup : PowerupBehaviour
    {
        const float VisualLinger = 1.5f;
        const float SelfDestroyDelay = 2f;
        const float MinDelay = 0.01f;

        public TargetSelection targets = TargetSelection.Leader;
        [SerializeField] StatusEffectDefinition effect;
        [Tooltip("Seconds between activation and the effect hitting.")]
        [SerializeField] float delay = 0.6f;
        [Tooltip("Spawned at each target when the effect lands (e.g. falling weight).")]
        [SerializeField] GameObject targetVfx;
        [Tooltip("Played at each target when the effect lands (Extra [PowerUp_16t_Hit]). The fail sound plays instead when " +
                 "the target refuses it (shielded, already flattened or shot down).")]
        [SerializeField] AudioClip hitSound;
        [Tooltip("Played on activation, at the first target (or the user when there is none) (Extra [PowerUp_16t_Fall]).")]
        [SerializeField] AudioClip activationSound;
        [Tooltip("Played at the user when no kart was in range, once the delay has passed, and at a target that refused " +
                 "the effect (Extra [PowerUp_16t_Fail]).")]
        [SerializeField] AudioClip failSound;
        [Tooltip("How far the hit or fail sound at each target carries (match an ImpactQuake on the visual to fade both alike).")]
        [SerializeField] Audio.SoundRange landSoundRange = new();
        [Tooltip("Visual dropped from this height above the target during the delay.")]
        [SerializeField] float dropHeight;
        [Tooltip("Seconds of the delay the dropped visual hangs at the drop height before it falls (0 = falls at once). " +
                 "The effect still lands after the full delay, so the fall gets shorter.")]
        [SerializeField] float hangTime;
        [Tooltip("Height of the up and down bob of the dropped visual while it hangs (m, 0 = no bob). It fades out " +
                 "towards the end of the hang and stops when the fall starts.")]
        [SerializeField] float bobAmplitude;
        [Tooltip("Seconds per up and down cycle of the bob.")]
        [SerializeField] float bobPeriod = 1f;
        [Tooltip("Only karts within this distance are targeted (0 = unlimited).")]
        public float maxDistance;

        protected override void OnLaunch() => StartCoroutine(Run());

        IEnumerator Run()
        {
            var victims = SelectVictims();
            var visuals = SpawnDropVisuals(victims);
            PlaySfx(activationSound, victims.Count > 0 ? victims[0].transform.position : Owner.transform.position);
            var t = 0f;
            var hang = Mathf.Clamp(hangTime, 0f, delay);
            while (t < delay)
            {
                t += Time.deltaTime;
                MoveDropVisuals(victims, visuals, Mathf.Clamp01((t - hang) / Mathf.Max(delay - hang, MinDelay)), BobOffset(t, hang));
                yield return null;
            }
            ApplyToVictims(victims, visuals);
            if (victims.Count == 0 && Owner != null) PlaySfx(failSound, Owner.transform.position);
            Destroy(gameObject, SelfDestroyDelay);
        }

        List<Kart> SelectVictims()
        {
            var victims = new List<Kart>();
            PowerupTargeting.Select(Owner, targets, maxDistance > 0f ? maxDistance : float.PositiveInfinity, victims);
            if (maxDistance > 0f)
                victims.RemoveAll(v => Vector3.Distance(v.transform.position, Owner.transform.position) > maxDistance);
            return victims;
        }

        List<GameObject> SpawnDropVisuals(List<Kart> victims)
        {
            var visuals = new List<GameObject>(victims.Count);
            foreach (var v in victims)
                visuals.Add(targetVfx != null && dropHeight > 0
                    ? PowerupFx.Instantiate(targetVfx, v.transform.position + Vector3.up * dropHeight, Quaternion.identity)
                    : null);
            return visuals;
        }

        void MoveDropVisuals(List<Kart> victims, List<GameObject> visuals, float k, float bob)
        {
            var height = dropHeight * (1f - k * k) + bob;
            for (var i = 0; i < victims.Count; i++)
            {
                if (visuals[i] == null || victims[i] == null) continue;
                visuals[i].transform.position = victims[i].transform.position + Vector3.up * height;
            }
        }

        float BobOffset(float t, float hang)
        {
            if (t >= hang || bobAmplitude <= 0f || bobPeriod <= 0f) return 0f;
            var u = t / hang;
            return bobAmplitude * (1f - u * u) * Mathf.Sin(2f * Mathf.PI * t / bobPeriod);
        }

        void ApplyToVictims(List<Kart> victims, List<GameObject> visuals)
        {
            for (var i = 0; i < victims.Count; i++)
            {
                var v = victims[i];
                if (v == null) continue;
                var landed = KartReactions.Apply(v, effect, Owner);
                if (visuals[i] != null) Destroy(visuals[i], VisualLinger);
                PowerupFx.Spawn(visuals[i] != null ? null : targetVfx, v.transform.position, landed ? hitSound : failSound, soundVolume,
                    landSoundRange);
            }
        }
    }
}
