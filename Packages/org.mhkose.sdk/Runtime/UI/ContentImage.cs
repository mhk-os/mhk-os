using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// A content picture (portrait, kart icon, track preview, logo) drawn as the element's background. Its <c>image</c>
    /// attribute is the UI Builder sample, overridden per template instance with <c>AttributeOverrides</c>.
    /// </summary>
    [UxmlElement]
    public partial class ContentImage : VisualElement
    {
        Texture2D image;

        [UxmlAttribute]
        public Texture2D Image
        {
            get => image;
            set
            {
                image = value;
                style.backgroundImage = value != null ? new StyleBackground(value) : new StyleBackground(StyleKeyword.Null);
            }
        }
    }
}
