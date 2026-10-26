using System.Xml;
using MHK.Online;
#if UNITY_ANDROID
using System.IO;
using UnityEditor.Android;
#endif

namespace MHK.Editor.Build
{
    /// <summary>
    /// Android room links: an intent filter for <c>mhkos://join/CODE</c> on the game's launcher activity, so opening a
    /// link starts the game (or brings it back, single task) and Unity hands the link to <see cref="JoinLinks"/> through
    /// Application.deepLinkActivated / absoluteURL. Applied to the generated Gradle project's manifest: no custom manifest.
    /// </summary>
    public static class JoinLinksManifest
    {
        const string AndroidNs = "http://schemas.android.com/apk/res/android";

        /// <summary>
        /// Adds the room link filter to every launcher activity of <paramref name="manifest"/> that lacks it and makes it
        /// single task (a link reaches the running game). Returns how many activities changed.
        /// </summary>
        public static int AddJoinLinkFilter(XmlDocument manifest)
        {
            var ns = new XmlNamespaceManager(manifest.NameTable);
            ns.AddNamespace("android", AndroidNs);
            var changed = 0;
            var activities = manifest.SelectNodes("/manifest/application/activity[intent-filter/category/@android:name='android.intent.category.LAUNCHER']", ns);
            if (activities == null) return 0;
            foreach (XmlElement activity in activities)
            {
                if (activity.SelectSingleNode($"intent-filter/data[@android:scheme='{JoinLinks.Scheme}']", ns) != null) continue;
                var filter = manifest.CreateElement("intent-filter");
                filter.AppendChild(Child(manifest, "action", "android.intent.action.VIEW"));
                filter.AppendChild(Child(manifest, "category", "android.intent.category.DEFAULT"));
                filter.AppendChild(Child(manifest, "category", "android.intent.category.BROWSABLE"));
                var data = manifest.CreateElement("data");
                data.SetAttribute("scheme", AndroidNs, JoinLinks.Scheme);
                data.SetAttribute("host", AndroidNs, "join");
                filter.AppendChild(data);
                activity.AppendChild(filter);
                activity.SetAttribute("launchMode", AndroidNs, "singleTask");
                changed++;
            }
            return changed;
        }

        static XmlElement Child(XmlDocument doc, string element, string name)
        {
            var e = doc.CreateElement(element);
            e.SetAttribute("name", AndroidNs, name);
            return e;
        }
    }

#if UNITY_ANDROID
    /// <summary>Applies <see cref="JoinLinksManifest"/> to the unityLibrary manifest of every Android build.</summary>
    public sealed class JoinLinksAndroid : IPostGenerateGradleAndroidProject
    {
        public int callbackOrder => 0;

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            var file = Path.Combine(path, "src", "main", "AndroidManifest.xml");
            var doc = new XmlDocument { PreserveWhitespace = true };
            doc.Load(file);
            if (JoinLinksManifest.AddJoinLinkFilter(doc) > 0) doc.Save(file);
        }
    }
#endif
}
