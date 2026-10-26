using MHK.Modding;
using UnityEngine;
using UnityEngine.Pool;
using UnityEngine.Serialization;

namespace MHK.Powerups
{
    /// <summary>
    /// Drops a held item with its held item animation (MHK Extra Nagelbrett-Action, PUNB_01): the model starts where the held
    /// visual was and plays where it was released. It ends lying on the ground where the clip's last frame puts it, then
    /// enables <see cref="activateOnLanding"/> (e.g. a <see cref="HazardTrap"/>). The root moves to the resting spot.
    /// </summary>
    public class HeldItemDropPowerup : PowerupBehaviour
    {
        const float GroundProbeUp = 2f;
        const float GroundProbeDown = 6f;

        [SerializeField, FormerlySerializedAs("addOn")] HeldItemAnimation model;
        [Tooltip("Clip time at which the item lies on the ground (s). The hazard becomes active then.")]
        [SerializeField] float landTime = 0.5f;
        [Tooltip("Size on the ground relative to the held model (1 = as held). The item grows to it while it drops, " +
                 "about its resting point. Hazard radii are set separately.")]
        [SerializeField, Min(0.01f)] float restScale = 1f;
        [SerializeField] LayerMask groundMask = ~0;
        [Tooltip("Disabled until landing, then enabled.")]
        [SerializeField] Behaviour[] activateOnLanding;
        [Tooltip("One-shot when the item comes to lie on the ground (landTime).")]
        [SerializeField] AudioClip landSound;

        float t;
        bool landed, onGround;
        Vector3 heldScale;

        protected override void OnLaunch()
        {
            SetEnabled(activateOnLanding, false);
            heldScale = transform.localScale;
            if (model == null)
            {
                Remove();
                return;
            }
            var parent = KartMounts.Frame(Owner);
            var yaw = Quaternion.Euler(0f, parent.eulerAngles.y, 0f);
            transform.SetPositionAndRotation(parent.TransformPoint(KartMounts.Point(Owner, HeldMountOr(HeldMount.Back))), yaw);
            model.Sample(0f);
            model.transform.GetPositionAndRotation(out var modelPosition, out var modelRotation);
            var rest = RestPointOfLastFrame(modelPosition);
            model.transform.SetPositionAndRotation(modelPosition, modelRotation);
            SnapToGround(rest, yaw, modelPosition, modelRotation);
        }

        // Bottom centre of the renderers in the clip's last frame, on the original's flat ground.
        Vector3 RestPointOfLastFrame(Vector3 fallback)
        {
            model.Sample(model.Length);
            var rest = PoseBounds(model.gameObject, out var end) ? new Vector3(end.center.x, end.min.y, end.center.z) : fallback;
            model.Sample(0f);
            return rest;
        }

        // Moves and tilts the whole drop onto the real ground below the rest point.
        void SnapToGround(Vector3 rest, Quaternion yaw, Vector3 modelPosition, Quaternion modelRotation)
        {
            var ground = rest;
            var tilt = Quaternion.identity;
            if (ItemPhysics.TryGroundBelow(rest, GroundProbeUp, GroundProbeDown, out var hit, groundMask))
            {
                ground = hit.point;
                tilt = Quaternion.FromToRotation(Vector3.up, hit.normal);
                onGround = true;
            }
            transform.SetPositionAndRotation(ground, tilt * yaw);
            model.transform.SetPositionAndRotation(ground + tilt * (modelPosition - rest), tilt * modelRotation);
        }

        protected override void Step(float dt)
        {
            if (model == null || t > model.Length) return;
            t += dt;
            if (restScale != 1f) transform.localScale = heldScale * Mathf.Lerp(1f, restScale, landTime > 0f ? t / landTime : 1f);
            if (landed || t < landTime) return;
            landed = true;
            if (!onGround)
            {
                Remove();
                return;
            }
            SetEnabled(activateOnLanding, true);
            PlaySfx(landSound, transform.position);
        }

        // The drop animation is drawn at the frame's time between steps.
        void Update()
        {
            if (model == null || t > model.Length + Time.fixedDeltaTime) return;
            model.Sample(Mathf.Min(t + PoseInterpolation.StepShare * Time.fixedDeltaTime, model.Length));
        }

        // Skinned renderers only update their bounds when skinned for rendering, so they are measured from a baked copy.
        static bool PoseBounds(GameObject root, out Bounds bounds)
        {
            bounds = default;
            var any = false;
            Mesh baked = null;
            using (ListPool<Renderer>.Get(out var renderers))
            using (ListPool<Vector3>.Get(out var vertices))
            {
                root.GetComponentsInChildren(renderers);
                foreach (var r in renderers)
                {
                    if (r is SkinnedMeshRenderer skin && skin.sharedMesh != null)
                    {
                        if (baked == null) baked = new Mesh();
                        skin.BakeMesh(baked); // unscaled renderer space: localToWorldMatrix adds the transform's scale
                        baked.GetVertices(vertices);
                        var m = skin.transform.localToWorldMatrix;
                        foreach (var v in vertices) Encapsulate(ref bounds, ref any, m.MultiplyPoint3x4(v));
                    }
                    else if (r is MeshRenderer)
                    {
                        if (!any) { bounds = r.bounds; any = true; }
                        else bounds.Encapsulate(r.bounds);
                    }
                }
            }
            if (baked != null) Destroy(baked);
            return any;
        }

        static void Encapsulate(ref Bounds bounds, ref bool any, Vector3 p)
        {
            if (!any) { bounds = new Bounds(p, Vector3.zero); any = true; }
            else bounds.Encapsulate(p);
        }
    }
}
