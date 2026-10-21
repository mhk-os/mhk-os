using UnityEngine;

namespace MHK.Karts
{
    public partial class KartPhysics
    {
        const float BounceMinApproach = -1f, BounceRestitution = -0.5f;
        const float MaxSlipAngleDeg = 45f;

        /// <summary>Constants of one <see cref="SolveContacts"/> call.</summary>
        readonly struct SolverFrame
        {
            public readonly KartPhysicsProfile p;
            public readonly Quaternion rot;
            public readonly Vector3 fwd, up, com;
            public readonly float m, h, kFront, kRear;

            public SolverFrame(KartPhysicsProfile p, Quaternion rot, float h, Vector3 com, float m)
            {
                this.p = p;
                this.rot = rot;
                this.h = h;
                this.com = com;
                this.m = m;
                fwd = rot * Vector3.forward;
                up = rot * Vector3.up;
                kFront = p.wheelsStiffnessFront * m;
                kRear = p.wheelsStiffnessRear * m;
            }
        }

        // Sequential impulses per iteration and wheel: normal row, then lateral, then longitudinal (the order is part of
        // the handling).
        void SolveContacts(ref Vector3 v, ref Vector3 w, Quaternion rot, float h, KartPhysicsProfile p)
        {
            var f = new SolverFrame(p, rot, h, Body.worldCenterOfMass, Body.mass);
            for (var i = 0; i < WheelCount; i++) PrepareContactRows(ref wheels[i], v, w, in f);
            for (var it = 0; it < solverIterations; it++)
            {
                for (var i = 0; i < WheelCount; i++)
                {
                    ref var wh = ref wheels[i];
                    if (!wh.contact) continue;
                    var r = wh.point - f.com;
                    SolveNormalRow(ref wh, ref v, ref w, r, in f);
                    SolveLateralRow(ref wh, ref v, ref w, r, in f);
                    if (wh.lonLimit > 0f) SolveLongitudinalRow(ref wh, ref v, ref w, r, in f);
                }
            }
        }

        void PrepareContactRows(ref Wheel wh, Vector3 v, Vector3 w, in SolverFrame f)
        {
            wh.lambdaN = wh.lambdaLat = wh.lambdaLon = 0f;
            if (!wh.contact) return;
            var n = wh.normal;
            var steer = Quaternion.AngleAxis(wh.front ? SteerAngle : 0f, f.up);
            var sideW = steer * (f.rot * Vector3.right);
            wh.dirLat = Vector3.ProjectOnPlane(sideW, n).normalized;
            wh.dirLon = Vector3.Cross(wh.dirLat, n).normalized;
            if (Vector3.Dot(wh.dirLon, f.fwd) < 0f) wh.dirLon = -wh.dirLon;
            var r = wh.point - f.com;
            var vn0 = Vector3.Dot(v + Vector3.Cross(w, r), n);
            wh.restitutionTarget = wh.touching && vn0 < BounceMinApproach ? BounceRestitution * vn0 : 0f;
            PrepareLateralCompliance(ref wh, v, w, r, in f);
        }

        // Remake tyre: the side force grows with the slip angle and reaches latLimit at lateralSlipAngle × the surface's
        // slip angle scale, a damper of c = latLimit / (tan α · |v_long|) on the lateral slip speed (γ = 1/(h·c)).
        void PrepareLateralCompliance(ref Wheel wh, Vector3 v, Vector3 w, Vector3 r, in SolverFrame f)
        {
            wh.lateralCompliance = 0f;
            if (!(f.p.lateralSlipAngle > 0f && wh.latLimit > 0f)) return;
            var vLong = Mathf.Abs(Vector3.Dot(v + Vector3.Cross(w, r), wh.dirLon));
            var alpha = f.p.lateralSlipAngle * wh.slipScale;
            var c = wh.latLimit / (Mathf.Tan(Mathf.Min(alpha, MaxSlipAngleDeg) * Mathf.Deg2Rad) * Mathf.Max(vLong, 1f));
            wh.lateralCompliance = 1f / (f.h * c);
        }

        // Penetrating: soft spring (ERP/CFM) or bounce. Within the margin: speculative, a hard limit on the approach (a
        // soft one let a hovering axle overshoot under downforce and the kart see-sawed between its axles).
        void SolveNormalRow(ref Wheel wh, ref Vector3 v, ref Vector3 w, Vector3 r, in SolverFrame f)
        {
            var kk = wh.front ? f.kFront : f.kRear;
            var c = (wh.front ? f.p.wheelsDampingFront : f.p.wheelsDampingRear) * f.m;
            var hk = f.h * kk;
            var gamma = 1f / (f.h * (c + hk));
            var beta = hk / (c + hk);
            var n = wh.normal;
            var vn = Vector3.Dot(v + Vector3.Cross(w, r), n);
            var invMass = InvEffMass(r, n, f.rot, f.m);
            var target = wh.depth >= 0f ? Mathf.Max(beta / f.h * wh.depth, wh.restitutionTarget) : wh.depth / f.h;
            var soft = wh.depth >= 0f ? gamma : 0f;
            var dl = (target - vn - soft * wh.lambdaN) / (invMass + soft);
            var newL = Mathf.Max(0f, wh.lambdaN + dl);
            dl = newL - wh.lambdaN;
            wh.lambdaN = newL;
            ApplyImpulse(ref v, ref w, n * dl, r, f.rot, f.m);
        }

        // Velocity and impulse both at the raised force point, so roll is left alone.
        void SolveLateralRow(ref Wheel wh, ref Vector3 v, ref Vector3 w, Vector3 r, in SolverFrame f)
        {
            var rLat = r - f.up * (Vector3.Dot(r, f.up) * lateralForceHeight);
            var vl = Vector3.Dot(v + Vector3.Cross(w, rLat), wh.dirLat);
            var invL = InvEffMass(rLat, wh.dirLat, f.rot, f.m);
            var dLat = (-vl - wh.lateralCompliance * wh.lambdaLat) / (invL + wh.lateralCompliance);
            var maxLat = wh.latLimit * f.h;
            var nl = Mathf.Clamp(wh.lambdaLat + dLat, -maxLat, maxLat);
            dLat = nl - wh.lambdaLat;
            wh.lambdaLat = nl;
            ApplyImpulse(ref v, ref w, wh.dirLat * dLat, rLat, f.rot, f.m);
        }

        void SolveLongitudinalRow(ref Wheel wh, ref Vector3 v, ref Vector3 w, Vector3 r, in SolverFrame f)
        {
            var vlo = Vector3.Dot(v + Vector3.Cross(w, r), wh.dirLon);
            var invLo = InvEffMass(r, wh.dirLon, f.rot, f.m);
            var dLon = -vlo / invLo;
            var maxLon = wh.lonLimit * f.h;
            var nlo = Mathf.Clamp(wh.lambdaLon + dLon, -maxLon, maxLon);
            dLon = nlo - wh.lambdaLon;
            wh.lambdaLon = nlo;
            ApplyImpulse(ref v, ref w, wh.dirLon * dLon, r, f.rot, f.m);
        }

        float InvEffMass(Vector3 r, Vector3 d, Quaternion rot, float m)
        {
            var rxd = Vector3.Cross(r, d);
            return 1f / m + Vector3.Dot(rxd, InverseWorldInertiaTimes(rxd, rot));
        }

        void ApplyImpulse(ref Vector3 v, ref Vector3 w, Vector3 impulse, Vector3 r, Quaternion rot, float m)
        {
            v += impulse / m;
            w += InverseWorldInertiaTimes(Vector3.Cross(r, impulse), rot);
        }

        Vector3 WorldInertiaTimes(Vector3 wWorld, Quaternion rot)
        {
            var local = Quaternion.Inverse(rot) * wWorld;
            return rot * Vector3.Scale(inertiaLocal, local);
        }

        Vector3 InverseWorldInertiaTimes(Vector3 tWorld, Quaternion rot)
        {
            var local = Quaternion.Inverse(rot) * tWorld;
            return rot * new Vector3(local.x / inertiaLocal.x, local.y / inertiaLocal.y, local.z / inertiaLocal.z);
        }
    }
}
