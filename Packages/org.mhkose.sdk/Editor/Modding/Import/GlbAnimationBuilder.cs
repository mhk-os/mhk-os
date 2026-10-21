using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MHK.Editor.Modding.Import
{
    /// <summary>
    /// Generic looping animation clips (30 fps) from glTF animations: translation, rotation and scale channels with
    /// LINEAR, STEP or CUBICSPLINE keys, quaternion signs kept continuous and duplicate key times turned into jumps.
    /// </summary>
    static class GlbAnimationBuilder
    {
        const float ClipFrameRate = 30f;
        const float JumpTime = 1e-4f;

        static readonly string[] Axes = { "x", "y", "z", "w" };
        static readonly float[] TranslationSign = { -1f, 1f, 1f };
        static readonly float[] RotationSign = { 1f, -1f, -1f, 1f };
        static readonly float[] ScaleSign = { 1f, 1f, 1f };

        /// <summary>Builds the clip of animation <paramref name="index"/> and stores it in the model and library.</summary>
        public static void Import(GlbImport g, JObject anim, int index)
        {
            var animName = GlbReader.Str(anim["name"]) ?? $"animation{index}";
            if (g.model.clips.ContainsKey(animName)) animName += "_" + index;
            var clip = new AnimationClip { name = $"{g.prefix}_{animName}", frameRate = ClipFrameRate, legacy = false };
            var samplers = (JArray)anim["samplers"];
            foreach (var ch in (JArray)anim["channels"]) AddChannel(g, clip, ch, samplers, animName);
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = true;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            clip.wrapMode = WrapMode.Loop;
            g.model.clips[animName] = g.Store(clip);
        }

        static void AddChannel(GlbImport g, AnimationClip clip, JToken ch, JArray samplers, string animName)
        {
            var path = GlbReader.Str(ch["target"]?["path"]);
            var node = GlbReader.I(ch["target"]?["node"]);
            if (path == "weights")
            {
                g.WarnOnce("morph", "morph targets are not supported and are skipped");
                return;
            }
            if (node < 0 || node >= g.nodes.Length || g.nodes[node] == null)
            {
                g.WarnOnce("animNode" + node, $"animation '{animName}' targets node {node}, which is not in the scene; channel skipped");
                return;
            }
            if (!TryProperty(path, out var property, out var sign))
            {
                g.WarnOnce("path" + path, $"animation target path '{path}' is not supported; channel skipped");
                return;
            }
            var sampler = (JObject)samplers[(int)ch["sampler"]];
            var times = g.doc.ReadFloats((int)sampler["input"], out _);
            var values = g.doc.ReadFloats((int)sampler["output"], out var comps);
            if (comps != sign.Length)
                throw new InvalidDataException($"{g.doc.path}: animation '{animName}' {path} output is not VEC{sign.Length}");
            var curves = BuildCurves(g, times, values, sign, GlbReader.Str(sampler["interpolation"], "LINEAR"), path == "rotation", animName);
            if (curves == null) return;
            var relPath = AnimationUtility.CalculateTransformPath(g.nodes[node], g.root.transform);
            for (var c = 0; c < curves.Length; c++)
                clip.SetCurve(relPath, typeof(Transform), $"{property}.{Axes[c]}", curves[c]);
        }

        static bool TryProperty(string path, out string property, out float[] sign)
        {
            (property, sign) = path switch
            {
                "translation" => ("m_LocalPosition", TranslationSign),
                "rotation" => ("m_LocalRotation", RotationSign),
                "scale" => ("m_LocalScale", ScaleSign),
                _ => (null, null),
            };
            return property != null;
        }

        static AnimationCurve[] BuildCurves(GlbImport g, float[] times, float[] values, float[] sign, string interpolation, bool rotation, string animName)
        {
            var n = times.Length;
            var cubic = interpolation == "CUBICSPLINE";
            var step = interpolation == "STEP";
            if (!cubic && !step && interpolation != "LINEAR")
                g.WarnOnce("interp" + interpolation, $"interpolation '{interpolation}' is not supported; imported as LINEAR");
            if (n == 0) return null;
            if (values.Length < n * sign.Length * (cubic ? 3 : 1))
            {
                g.WarnOnce("animSize" + animName, $"animation '{animName}' has fewer output values than keyframes; channel skipped");
                return null;
            }
            ReadKeyValues(values, sign, cubic, rotation, n, out var val, out var inT, out var outT);
            var (t, jump) = StrictlyIncreasing(times);
            var curves = new AnimationCurve[sign.Length];
            for (var i = 0; i < sign.Length; i++) curves[i] = new AnimationCurve(Keys(i, t, jump, val, inT, outT, step, cubic));
            return curves;
        }

        static void ReadKeyValues(float[] values, float[] sign, bool cubic, bool rotation, int n, out float[,] val, out float[,] inT, out float[,] outT)
        {
            var c = sign.Length;
            val = new float[n, c];
            inT = new float[n, c];
            outT = new float[n, c];
            for (var k = 0; k < n; k++)
            {
                var vi = (cubic ? 3 * k + 1 : k) * c;
                for (var i = 0; i < c; i++)
                {
                    val[k, i] = values[vi + i] * sign[i];
                    if (!cubic) continue;
                    inT[k, i] = values[(3 * k) * c + i] * sign[i];
                    outT[k, i] = values[(3 * k + 2) * c + i] * sign[i];
                }
                if (rotation && k > 0) KeepHemisphere(val, inT, outT, k);
            }
        }

        // Keeps the quaternion sign continuous so component-wise interpolation takes the short way.
        static void KeepHemisphere(float[,] val, float[,] inT, float[,] outT, int k)
        {
            var dot = 0f;
            for (var i = 0; i < 4; i++) dot += val[k, i] * val[k - 1, i];
            if (dot >= 0f) return;
            for (var i = 0; i < 4; i++)
            {
                val[k, i] = -val[k, i];
                inT[k, i] = -inT[k, i];
                outT[k, i] = -outT[k, i];
            }
        }

        // A duplicate key time becomes an instant jump just after its predecessor.
        static (float[] times, bool[] jump) StrictlyIncreasing(float[] times)
        {
            var t = new float[times.Length];
            var jump = new bool[times.Length];
            for (var k = 0; k < times.Length; k++)
            {
                t[k] = times[k];
                if (k == 0 || t[k] > t[k - 1]) continue;
                t[k] = t[k - 1] + JumpTime;
                jump[k] = true;
            }
            return (t, jump);
        }

        static Keyframe[] Keys(int i, float[] t, bool[] jump, float[,] val, float[,] inT, float[,] outT, bool step, bool cubic)
        {
            var n = t.Length;
            var keys = new Keyframe[n];
            for (var k = 0; k < n; k++)
            {
                float tin, tout;
                if (step) tin = tout = float.PositiveInfinity;
                else if (cubic)
                {
                    tin = inT[k, i];
                    tout = outT[k, i];
                }
                else
                {
                    tin = k > 0 ? (val[k, i] - val[k - 1, i]) / (t[k] - t[k - 1]) : 0f;
                    tout = k + 1 < n ? (val[k + 1, i] - val[k, i]) / (t[k + 1] - t[k]) : 0f;
                    if (k == 0) tin = tout;
                    if (k == n - 1) tout = tin;
                }
                if (jump[k]) tin = float.PositiveInfinity;
                if (k + 1 < n && jump[k + 1]) tout = float.PositiveInfinity;
                keys[k] = new Keyframe(t[k], val[k, i], tin, tout);
            }
            return keys;
        }
    }
}
