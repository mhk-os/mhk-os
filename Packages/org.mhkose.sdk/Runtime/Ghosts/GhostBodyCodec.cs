using System.Collections.Generic;
using System.IO;
using MHK.Modding;
using UnityEngine;
using static MHK.Ghosts.GhostChannelCodec;

namespace MHK.Ghosts
{
    /// <summary>
    /// Body block of a ghost file: the effect-id string table, the tick channels and the events. Each Write/Read pair
    /// below is one section of the format, in file order.
    /// </summary>
    static class GhostBodyCodec
    {
        const float PosScale = 1000f, RotScale = 1e6f, VelScale = 1000f, AngleScale = 1000f, CommandScale = 10000f,
            InputScale = 1000f, SpinScale = 100f, UnitScale = 1000f, FovScale = 100f;
        const float MinQuaternionLength = 1e-6f;

        public static void Write(BinaryWriter w, GhostData data)
        {
            var index = WriteEffectTable(w, data.events);
            var f = data.frames;
            var q = new long[f.Length];
            WritePositions(w, f, q);
            WriteRotations(w, f, q);
            WriteVelocities(w, f, q);
            WriteScalarChannels(w, f, q);
            WriteEvents(w, data.events, index);
        }

        public static void Read(BinaryReader r, GhostData data)
        {
            var strings = ReadEffectTable(r);
            var n = data.header.frameCount;
            var f = new GhostFrame[n];
            var q = new long[n];
            ReadPositions(r, f, q);
            ReadRotations(r, f, q);
            ReadVelocities(r, f, q);
            ReadScalarChannels(r, f, q);
            data.frames = f;
            data.events = ReadEvents(r, strings);
        }

        static Dictionary<string, int> WriteEffectTable(BinaryWriter w, List<GhostEvent> events)
        {
            var strings = new List<string>();
            var index = new Dictionary<string, int>();
            foreach (var e in events)
            {
                if (e.kind is not (GhostEventKind.EffectApplied or GhostEventKind.EffectEnded)) continue;
                var id = e.effectId ?? "";
                if (index.ContainsKey(id)) continue;
                index[id] = strings.Count;
                strings.Add(id);
            }
            WriteVarint(w, (ulong)strings.Count);
            foreach (var s in strings) w.Write(s);
            return index;
        }

        static string[] ReadEffectTable(BinaryReader r)
        {
            var strings = new string[(int)ReadVarint(r)];
            for (var i = 0; i < strings.Length; i++) strings[i] = r.ReadString();
            return strings;
        }

        static void WritePositions(BinaryWriter w, GhostFrame[] f, long[] q)
        {
            for (var c = 0; c < 3; c++)
            {
                for (var i = 0; i < f.Length; i++) q[i] = Quantise(f[i].position[c], PosScale);
                WriteChannel(w, q, 2);
            }
        }

        static void ReadPositions(BinaryReader r, GhostFrame[] f, long[] q)
        {
            for (var c = 0; c < 3; c++)
            {
                ReadChannel(r, q, 2);
                for (var i = 0; i < f.Length; i++) f[i].position[c] = q[i] / PosScale;
            }
        }

        static void WriteRotations(BinaryWriter w, GhostFrame[] f, long[] q)
        {
            // q and -q are the same rotation: keep the sign continuous so the second differences stay small.
            var rot = new Quaternion[f.Length];
            for (var i = 0; i < f.Length; i++)
            {
                var r = f[i].rotation;
                if (i > 0 && Quaternion.Dot(r, rot[i - 1]) < 0f) r = new Quaternion(-r.x, -r.y, -r.z, -r.w);
                rot[i] = r;
            }
            for (var c = 0; c < 4; c++)
            {
                for (var i = 0; i < f.Length; i++) q[i] = Quantise(rot[i][c], RotScale);
                WriteChannel(w, q, 2);
            }
        }

        static void ReadRotations(BinaryReader r, GhostFrame[] f, long[] q)
        {
            var n = f.Length;
            var rot = new float[4][];
            for (var c = 0; c < 4; c++)
            {
                ReadChannel(r, q, 2);
                rot[c] = new float[n];
                for (var i = 0; i < n; i++) rot[c][i] = q[i] / RotScale;
            }
            for (var i = 0; i < n; i++)
            {
                var v = new Vector4(rot[0][i], rot[1][i], rot[2][i], rot[3][i]);
                var m = v.magnitude;
                if (m > MinQuaternionLength) v /= m;
                else v = new Vector4(0f, 0f, 0f, 1f);
                f[i].rotation = new Quaternion(v.x, v.y, v.z, v.w);
            }
        }

        static void WriteVelocities(BinaryWriter w, GhostFrame[] f, long[] q)
        {
            for (var c = 0; c < 3; c++)
            {
                for (var i = 0; i < f.Length; i++) q[i] = Quantise(f[i].velocity[c], VelScale);
                WriteChannel(w, q, 1);
            }
        }

        static void ReadVelocities(BinaryReader r, GhostFrame[] f, long[] q)
        {
            for (var c = 0; c < 3; c++)
            {
                ReadChannel(r, q, 1);
                for (var i = 0; i < f.Length; i++) f[i].velocity[c] = q[i] / VelScale;
            }
        }

        static void WriteScalarChannels(BinaryWriter w, GhostFrame[] f, long[] q)
        {
            WriteScalar(w, f, q, x => x.forwardSpeed, VelScale);
            WriteScalar(w, f, q, x => x.steerAngle, AngleScale);
            WriteScalar(w, f, q, x => x.steerCommand, CommandScale);
            WriteScalar(w, f, q, x => x.inputSteer, InputScale);
            WriteScalar(w, f, q, x => x.inputThrottle, InputScale);
            WriteScalar(w, f, q, x => x.inputBrake, InputScale);
            WriteInts(w, f, q, x => (long)x.flags);
            WriteInts(w, f, q, x => (long)x.keys);
            WriteInts(w, f, q, x => x.contacts);
            WriteScalar(w, f, q, x => x.spinAngle, SpinScale);
            WriteScalar(w, f, q, x => x.scale, UnitScale);
            WriteScalar(w, f, q, x => x.verticalScale, UnitScale);
            WriteScalar(w, f, q, x => x.opacity, UnitScale);
            WriteScalar(w, f, q, x => x.fovBoost, FovScale);
        }

        static void ReadScalarChannels(BinaryReader r, GhostFrame[] f, long[] q)
        {
            var n = f.Length;
            ReadScalar(r, q, n, (i, v) => f[i].forwardSpeed = v, VelScale);
            ReadScalar(r, q, n, (i, v) => f[i].steerAngle = v, AngleScale);
            ReadScalar(r, q, n, (i, v) => f[i].steerCommand = v, CommandScale);
            ReadScalar(r, q, n, (i, v) => f[i].inputSteer = v, InputScale);
            ReadScalar(r, q, n, (i, v) => f[i].inputThrottle = v, InputScale);
            ReadScalar(r, q, n, (i, v) => f[i].inputBrake = v, InputScale);
            ReadInts(r, q, n, (i, v) => f[i].flags = (GhostFrameFlags)v);
            ReadInts(r, q, n, (i, v) => f[i].keys = (GhostInputKeys)v);
            ReadInts(r, q, n, (i, v) => f[i].contacts = (int)v);
            ReadScalar(r, q, n, (i, v) => f[i].spinAngle = v, SpinScale);
            ReadScalar(r, q, n, (i, v) => f[i].scale = v, UnitScale);
            ReadScalar(r, q, n, (i, v) => f[i].verticalScale = v, UnitScale);
            ReadScalar(r, q, n, (i, v) => f[i].opacity = v, UnitScale);
            ReadScalar(r, q, n, (i, v) => f[i].fovBoost = v, FovScale);
        }

        static void WriteEvents(BinaryWriter w, List<GhostEvent> events, Dictionary<string, int> index)
        {
            WriteVarint(w, (ulong)events.Count);
            foreach (var e in events)
            {
                w.Write(e.time);
                w.Write((byte)e.kind);
                switch (e.kind)
                {
                    case GhostEventKind.Action:
                        w.Write((short)e.role);
                        w.Write(e.hold);
                        break;
                    case GhostEventKind.Release:
                        w.Write((short)e.role);
                        break;
                    case GhostEventKind.EffectApplied:
                    case GhostEventKind.EffectEnded:
                        WriteVarint(w, (ulong)index[e.effectId ?? ""]);
                        break;
                }
            }
        }

        static List<GhostEvent> ReadEvents(BinaryReader r, string[] strings)
        {
            var count = (int)ReadVarint(r);
            var events = new List<GhostEvent>(count);
            for (var i = 0; i < count; i++)
            {
                var e = new GhostEvent { time = r.ReadSingle(), kind = (GhostEventKind)r.ReadByte() };
                switch (e.kind)
                {
                    case GhostEventKind.Action:
                        e.role = (CharacterAnimRole)r.ReadInt16();
                        e.hold = r.ReadBoolean();
                        break;
                    case GhostEventKind.Release:
                        e.role = (CharacterAnimRole)r.ReadInt16();
                        break;
                    case GhostEventKind.Snap:
                        break;
                    case GhostEventKind.EffectApplied:
                    case GhostEventKind.EffectEnded:
                        var s = (int)ReadVarint(r);
                        if (s < 0 || s >= strings.Length) throw new InvalidDataException("bad effect index");
                        e.effectId = strings[s];
                        break;
                    default:
                        throw new InvalidDataException($"unknown ghost event {e.kind}");
                }
                events.Add(e);
            }
            return events;
        }
    }
}
