#if UNITY_STANDALONE_OSX && !UNITY_EDITOR
using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace MHK.Core
{
    /// <summary>
    /// Gatekeeper runs a quarantined, not notarized app from a random read-only copy (App Translocation) without the
    /// folders next to it. Maps such a path back to where the user put the app.
    /// </summary>
    static class MacAppTranslocation
    {
        const string Marker = "/AppTranslocation/";
        const string Security = "/System/Library/Frameworks/Security.framework/Security";
        const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        const int MaxPathBytes = 4096;

        [DllImport(Security)] static extern IntPtr SecTranslocateCreateOriginalPathForURL(IntPtr translocatedPath, IntPtr error);
        [DllImport(CoreFoundation)] static extern IntPtr CFURLCreateFromFileSystemRepresentation(IntPtr allocator, byte[] buffer, long length, [MarshalAs(UnmanagedType.I1)] bool isDirectory);
        [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)]
        static extern bool CFURLGetFileSystemRepresentation(IntPtr url, [MarshalAs(UnmanagedType.I1)] bool resolveAgainstBase, byte[] buffer, long maxLength);
        [DllImport(CoreFoundation)] static extern void CFRelease(IntPtr cf);

        /// <summary>The original path of the .app at <paramref name="appPath"/>, the path itself when it is not translocated or cannot be mapped.</summary>
        public static string Original(string appPath)
        {
            if (appPath.IndexOf(Marker, StringComparison.Ordinal) < 0) return appPath;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(appPath);
                var url = CFURLCreateFromFileSystemRepresentation(IntPtr.Zero, bytes, bytes.Length, true);
                if (url == IntPtr.Zero) return appPath;
                var original = SecTranslocateCreateOriginalPathForURL(url, IntPtr.Zero);
                CFRelease(url);
                if (original == IntPtr.Zero) return appPath;
                var buffer = new byte[MaxPathBytes];
                var ok = CFURLGetFileSystemRepresentation(original, true, buffer, buffer.Length);
                CFRelease(original);
                return ok ? Encoding.UTF8.GetString(buffer, 0, Array.IndexOf(buffer, (byte)0)) : appPath;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Mods] The game runs from a translocated copy ({appPath}) and its original folder is unknown: {e.Message}");
                return appPath;
            }
        }
    }
}
#endif
