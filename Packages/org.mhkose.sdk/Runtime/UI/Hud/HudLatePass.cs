using System;
using UnityEngine;

namespace MHK.UI
{
    /// <summary>
    /// Runs <see cref="Pass"/> in LateUpdate after the race camera moved (execution order after the default), so screen
    /// anchors placed from world points (name tags) use this frame's camera and do not trail it.
    /// </summary>
    [DefaultExecutionOrder(1000)]
    [AddComponentMenu("")]
    public sealed class HudLatePass : MonoBehaviour
    {
        public Action Pass { get; set; }

        void LateUpdate() => Pass?.Invoke();
    }
}
