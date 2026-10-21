using System;
using System.IO;
using MHK.Modding;
using MHK.Race;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MHK.Editor.Content
{
    /// <summary>Frames and renders one track preview picture from a running race (karts hidden, camera above the grid).</summary>
    internal static class TrackPreviewShot
    {
        const int Width = 1280, Height = 720;
        const int Supersample = 2, Msaa = 4;
        const float BackDistance = 12f;
        const float EyeHeight = 6f;
        const float KartRoofClearance = 1.8f, RoofMargin = 0.6f, MinEyeHeight = 1.5f;
        const float LookAhead = 36f, LookHeight = 0.5f;
        const float FieldOfView = 55f;
        const float MinForwardSqr = 0.01f;

        /// <summary>Renders the race camera above and behind the grid of <paramref name="race"/>, writes the PNG and returns its path.</summary>
        public static string TakePicture(RaceManager race)
        {
            var runtime = race.TrackRuntime;
            var cam = race.RaceCamera;
            HideKarts(race);
            Frame(runtime, out var eye, out var target);
            var tex = RenderFrom(cam, eye, target);
            var path = OutputPath(race.Track);
            TextureCapture.WritePng(tex, path);
            return path;
        }

        static void HideKarts(RaceManager race)
        {
            foreach (var kart in race.Karts)
            foreach (var r in kart.GetComponentsInChildren<Renderer>(true))
                r.enabled = false;
        }

        static Texture2D RenderFrom(Camera cam, Vector3 eye, Vector3 target)
        {
            var t = cam.transform;
            var (oldPos, oldRot, oldFov) = (t.position, t.rotation, cam.fieldOfView);
            t.SetPositionAndRotation(eye, Quaternion.LookRotation(target - eye, Vector3.up));
            cam.fieldOfView = FieldOfView;
            foreach (var follow in Object.FindObjectsByType<FollowCameraPosition>())
                follow.Place(eye, cam.farClipPlane);
            try
            {
                return TextureCapture.Render(cam, Width, Height, TextureFormat.RGB24, supersample: Supersample, msaa: Msaa);
            }
            finally
            {
                t.SetPositionAndRotation(oldPos, oldRot);
                cam.fieldOfView = oldFov;
            }
        }

        static string OutputPath(TrackDefinition t)
        {
            var current = t.previewImage != null ? AssetDatabase.GetAssetPath(t.previewImage) : null;
            return !string.IsNullOrEmpty(current) && current.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && File.Exists(current)
                ? current
                : PreviewRenderer.OutputPath(t, "preview");
        }

        static void Frame(TrackRuntime runtime, out Vector3 eye, out Vector3 target)
        {
            var pole = runtime.GetGridPose(0);
            var fwd = Vector3.ProjectOnPlane(pole.forward, Vector3.up).normalized;
            if (fwd.sqrMagnitude < MinForwardSqr) fwd = Vector3.forward;
            Vector3 behind;
            var path = runtime.path;
            if (path != null && path.Count > 1)
            {
                var d = path.Project(pole.position);
                behind = path.GetPoint(d - BackDistance);
                target = path.GetPoint(d + LookAhead);
            }
            else
            {
                behind = pole.position - fwd * BackDistance;
                target = pole.position + fwd * LookAhead;
            }
            target += Vector3.up * LookHeight;
            eye = behind + Vector3.up * EyeHeightBelowRoof(behind);
        }

        static float EyeHeightBelowRoof(Vector3 behind)
        {
            // Cast from above kart height so a kart on a rear grid slot does not count as a roof.
            if (!Physics.Raycast(behind + Vector3.up * KartRoofClearance, Vector3.up, out var roof, EyeHeight, Physics.AllLayers, QueryTriggerInteraction.Ignore))
                return EyeHeight;
            return Mathf.Clamp(KartRoofClearance + roof.distance - RoofMargin, MinEyeHeight, EyeHeight);
        }
    }
}
