using System.Collections.Generic;
using MHK.Karts;
using UnityEngine;
using UnityEngine.Rendering;

namespace MHK.Effects
{
    /// <summary>
    /// Ghost copies of a kart and its driver trailing behind it (TINcan Viper's phase). Put it on an effect's attached
    /// visual. Each copy shows where the kart was a moment ago, the farther back the fainter, drawn in one see-through
    /// colour. The copies keep the pose kart and driver had when the effect started. When the effect ends they fade out
    /// with its visual (<see cref="FadeOut"/>). Visual only.
    /// </summary>
    [AddComponentMenu("MHK/Afterimage Trail")]
    public class AfterimageTrail : MonoBehaviour, IFadeOutListener
    {
        // Longest stretch of poses kept (s), beyond the farthest copy.
        const float HistoryMargin = 0.1f;
        const int MaxSamples = 256;
        const float MinPartScaleSq = 1e-6f;

        [SerializeField, Min(1), Tooltip("Number of ghost copies.")]
        int copies = 3;
        [SerializeField, Min(0.01f), Tooltip("Seconds between the poses the copies show. The nearest copy is this far behind the kart.")]
        float spacing = 0.07f;
        [SerializeField, Tooltip("Material the copies are drawn with, additive or transparent. Its base colour is set per copy.")]
        Material material;
        [SerializeField, Tooltip("Colour of the nearest copy. Farther copies fade towards transparent.")]
        Color colour = new(0.35f, 0.85f, 1f, 0.7f);
        [SerializeField, Tooltip("Seconds the copies take to fade in.")]
        float fadeIn = 0.15f;

        struct Part
        {
            public Mesh mesh;
            public Matrix4x4 local;
            public bool baked;
        }

        struct Sample
        {
            public float time;
            public Matrix4x4 pose;
        }

        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

        readonly List<Part> parts = new();
        readonly Sample[] history = new Sample[MaxSamples];
        MaterialPropertyBlock[] blocks;
        Transform body;
        int head, count;
        float age;
        // Share still shown while the effect's visual fades out (FadeOut), 1 before that.
        float visibility = 1f;

        void Start()
        {
            var view = GetComponentInParent<IKartView>();
            var visual = view != null ? view.Visual : null;
            if (visual == null || material == null)
            {
                enabled = false;
                return;
            }
            body = visual.Body;
            Snapshot(view);
            blocks = new MaterialPropertyBlock[copies];
            for (var i = 0; i < copies; i++) blocks[i] = new MaterialPropertyBlock();
        }

        // Meshes of kart and driver in the body's space, skinned ones baked in their pose now. Effect attachments, the
        // headlight glows, this trail and parts scaled to nothing are left out.
        void Snapshot(IKartView view)
        {
            var toBody = body.worldToLocalMatrix;
            foreach (var r in body.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled || r.transform.IsChildOf(transform) || view.IsEffectAttachment(r.transform) || r.GetComponentInParent<KartHeadlights>() != null) continue;
                if (r.transform.lossyScale.sqrMagnitude < MinPartScaleSq || r.shadowCastingMode == ShadowCastingMode.ShadowsOnly) continue;
                if (r is SkinnedMeshRenderer skinned && skinned.sharedMesh != null)
                {
                    var mesh = new Mesh { name = skinned.sharedMesh.name + " afterimage" };
                    skinned.BakeMesh(mesh, true);
                    parts.Add(new Part { mesh = mesh, local = toBody * skinned.transform.localToWorldMatrix, baked = true });
                }
                else if (r is MeshRenderer && r.TryGetComponent<MeshFilter>(out var filter) && filter.sharedMesh != null)
                    parts.Add(new Part { mesh = filter.sharedMesh, local = toBody * r.transform.localToWorldMatrix });
            }
        }

        void LateUpdate()
        {
            age += Time.deltaTime;
            Record();
            var fade = (fadeIn > 0f ? Mathf.Clamp01(age / fadeIn) : 1f) * visibility;
            if (fade <= 0f) return;
            for (var i = 0; i < copies; i++)
            {
                if (!TryPoseAt(age - spacing * (i + 1), out var pose)) continue;
                var c = colour;
                c.a *= fade * (1f - (float)i / copies);
                blocks[i].SetColor(BaseColor, c);
                var rp = new RenderParams(material) { matProps = blocks[i], shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false, layer = gameObject.layer };
                foreach (var p in parts)
                {
                    var matrix = pose * p.local;
                    for (var s = 0; s < p.mesh.subMeshCount; s++) Graphics.RenderMesh(rp, p.mesh, s, matrix);
                }
            }
        }

        void Record()
        {
            head = (head + 1) % MaxSamples;
            history[head] = new Sample { time = age, pose = body.localToWorldMatrix };
            count = Mathf.Min(count + 1, MaxSamples);
            // Samples older than the farthest copy are dropped.
            var keep = spacing * copies + HistoryMargin;
            while (count > 1 && age - history[(head - count + 1 + MaxSamples) % MaxSamples].time > keep) count--;
        }

        // The recorded pose nearest after time t, none before the first sample.
        bool TryPoseAt(float t, out Matrix4x4 pose)
        {
            pose = default;
            if (count == 0) return false;
            var oldest = history[(head - count + 1 + MaxSamples) % MaxSamples];
            if (t < oldest.time) return false;
            for (var n = count - 1; n >= 0; n--)
            {
                var s = history[(head - n + MaxSamples) % MaxSamples];
                if (s.time < t) continue;
                pose = s.pose;
                return true;
            }
            pose = history[head].pose;
            return true;
        }

        void IFadeOutListener.OnFadeOut(float shown) => visibility = shown;

        void OnDestroy()
        {
            foreach (var p in parts)
                if (p.baked && p.mesh != null) Destroy(p.mesh);
        }
    }
}
