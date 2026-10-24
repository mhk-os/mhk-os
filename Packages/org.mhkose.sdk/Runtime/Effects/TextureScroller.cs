using UnityEngine;

namespace MHK.Effects
{
    /// <summary>
    /// Scrolls a texture over its renderers by animating its <c>&lt;texture&gt;_ST</c> offset through a
    /// <see cref="MaterialPropertyBlock"/>. For mod shaders without MHK/Lit's <c>_UVScroll</c>, which needs no script.
    /// </summary>
    public class TextureScroller : MonoBehaviour
    {
        [Tooltip("Renderers to scroll, empty = all renderers in this object and its children.")]
        [SerializeField] Renderer[] renderers;
        [Tooltip("Texture property whose '<name>_ST' scale/offset is animated.")]
        [SerializeField] string textureProperty = "_BaseMap";
        [Tooltip("UV offset change per second (positive y moves the image towards lower v, i.e. down on an upright UV layout).")]
        [SerializeField] Vector2 speed = new(0f, 0.5f);

        int stId;
        Vector4[] baseSt;
        MaterialPropertyBlock block;
        Vector2 offset;

        void Awake()
        {
            if (renderers == null || renderers.Length == 0) renderers = GetComponentsInChildren<Renderer>(true);
            stId = Shader.PropertyToID(textureProperty + "_ST");
            block = new MaterialPropertyBlock();
            baseSt = new Vector4[renderers.Length];
            for (var i = 0; i < renderers.Length; i++)
            {
                var m = renderers[i] != null ? renderers[i].sharedMaterial : null;
                baseSt[i] = m != null && m.HasProperty(stId) ? m.GetVector(stId) : new Vector4(1f, 1f, 0f, 0f);
            }
        }

        void Update()
        {
            offset.x = Mathf.Repeat(offset.x + speed.x * Time.deltaTime, 1f);
            offset.y = Mathf.Repeat(offset.y + speed.y * Time.deltaTime, 1f);
            for (var i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null) continue;
                r.GetPropertyBlock(block);
                var st = baseSt[i];
                block.SetVector(stId, new Vector4(st.x, st.y, st.z + offset.x, st.w + offset.y));
                r.SetPropertyBlock(block);
            }
        }

        void OnDisable()
        {
            if (renderers == null) return;
            foreach (var r in renderers)
                if (r != null) r.SetPropertyBlock(null);
        }
    }
}
