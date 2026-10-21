using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace MHK.Karts
{
    /// <summary>
    /// Development tool: records a kart's handling state every physics tick (speed, slip angle, yaw rate, steering,
    /// wheel contacts) and writes it as CSV. Used to tune and regression-check the kart physics.
    /// </summary>
    public class KartTelemetry : MonoBehaviour
    {
        const string Header = "t,speed,vfwd,slipDeg,yawDegS,steerCmd,steerAngle,throttle,grounded,contacts,latAcc,posX,posY,posZ,rearGrip,skidEnergy,gear,rpm,speedScale";
        const string FloatFormat = "0.###";
        const float MinSlipSpeed = 0.5f;
        const int FormatBufferSize = 64;

        readonly StringBuilder csv = new();
        Kart kart;
        float t0;

        /// <summary>Starts recording <paramref name="kart"/> from now.</summary>
        public static KartTelemetry Attach(Kart kart)
        {
            var t = kart.gameObject.AddComponent<KartTelemetry>();
            t.kart = kart;
            t.t0 = Time.time;
            t.csv.AppendLine(Header);
            return t;
        }

        void FixedUpdate()
        {
            if (kart == null) return;
            var rb = kart.Physics.Body;
            var v = rb.linearVelocity;
            var f = kart.transform.forward;
            var r = kart.transform.right;
            var vf = Vector3.Dot(v, f);
            var slip = Mathf.Atan2(Vector3.Dot(v, r), Mathf.Max(MinSlipSpeed, Mathf.Abs(vf))) * Mathf.Rad2Deg;
            var yaw = Vector3.Dot(rb.angularVelocity, kart.transform.up) * Mathf.Rad2Deg;
            var latAcc = yaw * Mathf.Deg2Rad * vf; // centripetal part of the lateral acceleration
            AppendRow(v.magnitude, vf, slip, yaw, latAcc);
        }

        void AppendRow(float speed, float vf, float slip, float yaw, float latAcc)
        {
            var phys = kart.Physics;
            var p = kart.transform.position;
            Add(Time.time - t0); Add(speed); Add(vf); Add(slip); Add(yaw);
            Add(kart.CurrentInput.steer); Add(phys.SteerAngle); Add(phys.Throttle);
            Add(phys.IsGrounded ? 1 : 0); Add(phys.ContactCount); Add(latAcc);
            Add(p.x); Add(p.y); Add(p.z);
            Add(phys.RearGripUsage); Add(phys.SkidEnergy); Add(phys.Gear); Add(phys.Rpm);
            Add(phys.SpeedScale, '\n');
        }

        void Add(float x, char end = ',')
        {
            Span<char> buffer = stackalloc char[FormatBufferSize];
            if (x.TryFormat(buffer, out var n, FloatFormat, CultureInfo.InvariantCulture)) csv.Append(buffer.Slice(0, n));
            else csv.Append(x.ToString(FloatFormat, CultureInfo.InvariantCulture));
            csv.Append(end);
        }

        void Add(int x)
        {
            Span<char> buffer = stackalloc char[FormatBufferSize];
            x.TryFormat(buffer, out var n, default, CultureInfo.InvariantCulture);
            csv.Append(buffer.Slice(0, n)).Append(',');
        }

        /// <summary>Writes the recorded CSV to <paramref name="path"/> and returns the path.</summary>
        public string Save(string path)
        {
            File.WriteAllText(path, csv.ToString());
            return path;
        }
    }
}
