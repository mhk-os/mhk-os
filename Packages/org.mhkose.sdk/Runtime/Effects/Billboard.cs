using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace MHK.Effects
{
    /// <summary>
    /// Turns this object to face each camera right before it renders (glows inside item boxes). In edit mode only
    /// unsaved previews turn, so assets stay unchanged.
    /// </summary>
    [ExecuteAlways]
    public partial class Billboard : MonoBehaviour
    {
        [AutoStaticsCleanup] static readonly List<Billboard> active = new();
        // The camera hook survives entering play mode without a domain reload, so the flag must survive too.
        [NoAutoStaticsCleanup] static bool hooked;

        void OnEnable()
        {
            active.Add(this);
            if (hooked) return;
            RenderPipelineManager.beginCameraRendering += Face;
            hooked = true;
        }

        void OnDisable() => active.Remove(this);

        static void Face(ScriptableRenderContext context, Camera cam)
        {
            var rotation = cam.transform.rotation;
            var playing = Application.isPlaying;
            for (var i = 0; i < active.Count; i++)
            {
                var b = active[i];
                if (playing || (b.gameObject.hideFlags & HideFlags.DontSaveInEditor) != 0) b.transform.rotation = rotation;
            }
        }
    }
}
