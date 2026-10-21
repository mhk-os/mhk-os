using System.Collections.Generic;
using UnityEngine;

namespace MHK.Karts
{
    /// <summary>
    /// Hides the drawn wheels of a kart model (<see cref="IKartView.WheelsHidden"/>): every mesh renderer below its front
    /// and rear wheels. Uses <see cref="Renderer.forceRenderingOff"/>, so hiding the whole kart stays independent of it.
    /// </summary>
    public sealed class KartWheelVisibility
    {
        readonly Renderer[] renderers;
        bool hidden;

        /// <summary>The wheels of <paramref name="model"/> (none without a model).</summary>
        public KartWheelVisibility(KartModel model)
        {
            var list = new List<Renderer>();
            if (model != null)
            {
                Collect(model.frontWheels, list);
                Collect(model.rearWheels, list);
            }
            renderers = list.ToArray();
        }

        /// <summary>Shows or hides the wheels. Only a change touches the renderers.</summary>
        public void Update(bool hide)
        {
            if (hide == hidden) return;
            hidden = hide;
            foreach (var r in renderers)
                if (r != null) r.forceRenderingOff = hide;
        }

        static void Collect(Transform[] wheels, List<Renderer> list)
        {
            foreach (var w in wheels)
            {
                if (w == null) continue;
                foreach (var r in w.GetComponentsInChildren<Renderer>(true))
                    if ((r is MeshRenderer || r is SkinnedMeshRenderer) && !list.Contains(r)) list.Add(r);
            }
        }
    }
}
