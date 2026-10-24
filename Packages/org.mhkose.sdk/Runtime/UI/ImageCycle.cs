using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Cross-fades a picture element through a list of pictures (a cup's track previews). The element's own background
    /// and a fade layer on top of it (USS class <see cref="LayerClass"/>, its opacity transition is the fade) take turns.
    /// </summary>
    public sealed class ImageCycle
    {
        /// <summary>USS class of the fade layer (MHKMenu.uss: fills the element, opacity transition, <c>.shown</c> = opaque).</summary>
        public const string LayerClass = "image-fade";
        const string ShownClass = "shown";

        readonly VisualElement host, layer;
        IReadOnlyList<Texture2D> pictures = Array.Empty<Texture2D>();
        bool layerShown;

        public ImageCycle(VisualElement host)
        {
            this.host = host;
            layer = new VisualElement { pickingMode = PickingMode.Ignore };
            layer.AddToClassList(LayerClass);
            host.Add(layer);
        }

        /// <summary>Index of the picture shown now.</summary>
        public int Index { get; private set; }

        /// <summary>Shows <c>list[start]</c> at once (no fade) and cycles through <paramref name="list"/> from there.</summary>
        public void Show(IReadOnlyList<Texture2D> list, int start = 0)
        {
            pictures = list;
            Index = list.Count > 0 ? Mathf.Abs(start) % list.Count : 0;
            var picture = list.Count > 0 ? list[Index] : null;
            // Both layers get the picture: a fade still running from the previous list then changes nothing visible.
            host.SetImage(picture);
            layer.SetImage(picture);
            layerShown = false;
            layer.RemoveFromClassList(ShownClass);
        }

        /// <summary>Fades to the next picture (nothing with fewer than two).</summary>
        public void Step()
        {
            if (pictures.Count < 2) return;
            Index = (Index + 1) % pictures.Count;
            layerShown = !layerShown;
            (layerShown ? layer : host).SetImage(pictures[Index]);
            layer.EnableInClassList(ShownClass, layerShown);
        }
    }
}
