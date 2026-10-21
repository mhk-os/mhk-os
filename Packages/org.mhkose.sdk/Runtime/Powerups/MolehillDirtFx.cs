using MHK.Race;
using UnityEngine;

namespace MHK.Powerups
{
    /// <summary>Dust and clods of earth a <see cref="MolehillPowerup"/> throws up at the head of its dig.</summary>
    sealed class MolehillDirtFx
    {
        const float ClodLift = 0.3f;
        const float DustProbeAbove = 0.5f;
        const float DustProbeBelow = 1f;
        const float MinDustAlpha = 0.8f;

        readonly ParticleSystem spray, burst, clods;
        readonly ParticleSystemRenderer clodRenderer;
        readonly MolehillMound mound;
        readonly int burstCount, clodBurstCount, groundMask;
        readonly float clodsPerMetre;
        readonly Color defaultDust;
        float clodDebt;

        public MolehillDirtFx(ParticleSystem spray, ParticleSystem burst, ParticleSystem clods, MolehillMound mound,
            int burstCount, int clodBurstCount, float clodsPerMetre, Color defaultDust, int groundMask)
        {
            this.spray = spray;
            this.burst = burst;
            this.clods = clods;
            this.mound = mound;
            this.burstCount = burstCount;
            this.clodBurstCount = clodBurstCount;
            this.clodsPerMetre = clodsPerMetre;
            this.defaultDust = defaultDust;
            this.groundMask = groundMask;
            if (clods != null) clodRenderer = clods.GetComponent<ParticleSystemRenderer>();
        }

        /// <summary>Starts or stops the continuous spray at the head.</summary>
        public void Spray(bool on)
        {
            if (spray == null) return;
            if (on) spray.Play(true);
            else spray.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        }

        /// <summary>Moves the spray to the head and throws clods for the ground dug at <paramref name="speed"/> over <paramref name="dt"/>.</summary>
        public void Dig(Vector3 ground, Vector3 normal, Vector3 forward, float speed, float dt)
        {
            var dust = DustColor(ground, normal);
            if (spray != null)
            {
                spray.transform.SetPositionAndRotation(ground, Quaternion.LookRotation(forward, normal));
                var main = spray.main;
                main.startColor = dust;
            }
            if (clods == null) return;
            clodDebt += speed * dt * clodsPerMetre;
            var n = Mathf.FloorToInt(clodDebt);
            if (n <= 0) return;
            clodDebt -= n;
            EmitClods(ground + normal * ClodLift, normal, forward, n);
        }

        /// <summary>A burst of dust and clods at the head (<paramref name="share"/> of the full burst).</summary>
        public void Burst(Vector3 ground, Vector3 normal, Vector3 forward, float share = 1f)
        {
            var dust = DustColor(ground, normal);
            if (burst != null)
            {
                burst.transform.SetPositionAndRotation(ground, Quaternion.LookRotation(forward, normal));
                var main = burst.main;
                main.startColor = dust;
                burst.Emit(Mathf.CeilToInt(burstCount * share));
            }
            EmitClods(ground + normal * ClodLift, normal, forward, Mathf.CeilToInt(clodBurstCount * share));
        }

        // Clods are drawn with the mound's current ground material.
        void EmitClods(Vector3 at, Vector3 normal, Vector3 forward, int count)
        {
            if (clods == null || count <= 0) return;
            var mat = mound != null ? mound.Material : null;
            if (mat != null && clodRenderer != null && clodRenderer.sharedMaterial != mat) clodRenderer.sharedMaterial = mat;
            clods.transform.SetPositionAndRotation(at, Quaternion.LookRotation(forward, normal));
            clods.Emit(count);
        }

        Color DustColor(Vector3 ground, Vector3 normal)
        {
            if (!ItemPhysics.TryGroundAlong(ground, normal, DustProbeAbove, DustProbeBelow, out var hit, groundMask)) return defaultDust;
            var surface = hit.collider.GetComponentInParent<TrackSurface>();
            if (surface == null) return defaultDust;
            var c = surface.dustColor;
            c.a = Mathf.Max(c.a, MinDustAlpha);
            return c;
        }
    }
}
