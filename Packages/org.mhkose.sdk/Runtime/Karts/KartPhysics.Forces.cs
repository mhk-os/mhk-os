using MHK.Race;
using UnityEngine;

namespace MHK.Karts
{
    public partial class KartPhysics
    {
        const float DownshiftRpmShare = 0.95f;
        const float FreeWheelSpinDecay = 0.995f;
        const float LandingMinAirTime = 0.3f;
        const float AirAlignMinSpeedSq = 4f, AirAlignMinNoseSq = 0.04f;

        /// <summary>Values of one <see cref="Step"/> shared by its phases.</summary>
        struct StepFrame
        {
            public KartPhysicsProfile p;
            public StatusModifiers fx;
            public float h, m, classFactor, speedScale, diff, steer, steerTorque, driveTorque;
            public Quaternion rot;
            public Vector3 fwd, up, v, w, g, force, torqueW, com;
            public int contacts;
            public float avgLoad, resMax, gripShare;
            public Vector3 groundNormal;
        }

        StepFrame BeginStep(float h)
        {
            var s = new StepFrame
            {
                p = profile,
                fx = effects != null ? effects.Modifiers : StatusModifiers.Identity,
                h = h,
                rot = Body.rotation,
            };
            s.fwd = s.rot * Vector3.forward;
            s.up = s.rot * Vector3.up;
            s.v = Body.linearVelocity;
            s.w = Body.angularVelocity;
            ForwardSpeed = Vector3.Dot(s.v, s.fwd);
            return s;
        }

        // Steering wash-out (SetSteering 0x43ee70), δ + = left. Effects scale the wheel angle and the extra steering torque.
        float UpdateSteering(KartPhysicsProfile p, in StatusModifiers fx)
        {
            var steer = steerCommandDeg * fx.steering;
            var washed = steer / (1f + p.steeringWashout * Mathf.Pow(Mathf.Abs(ForwardSpeed), p.steeringWashoutPower));
            SteerAngle = -washed;
            return steer;
        }

        // Gearbox and engine (6.2): diff ÷ s stretches every gear over s× the road speed and torque × s² gives s× the
        // wheel force, so the speed/time curve is the original one × s.
        float UpdateGearbox(ref StepFrame s)
        {
            var p = s.p;
            s.classFactor = ClampClassFactor(ClassFactor);
            s.speedScale = ClampSpeedScale(SpeedScale);
            s.diff = DiffRatio(p, s.classFactor, s.speedScale);
            var nGears = Reversing ? 1 : Mathf.Max(1, p.gears.Length);
            if (Gear >= nGears) Gear = nGears - 1;
            if (Rpm > p.changeUpRpm) { if (Gear < nGears - 1) Gear++; }
            else if (Gear > 0 && GearRatio(p, Gear - 1) / GearRatio(p, Gear) * Rpm < DownshiftRpmShare * p.changeUpRpm) Gear--;
            var torque = TorqueCurve(p, Rpm / Mathf.Max(0.01f, s.fx.maxSpeed), Throttle, s.classFactor, DriveTorqueScale) * (s.speedScale * s.speedScale);
            return GearRatio(p, Gear) * s.diff * torque * s.fx.acceleration;
        }

        float GearRatio(KartPhysicsProfile p, int g) => Reversing ? -p.gearReverse : p.gears[Mathf.Clamp(g, 0, p.gears.Length - 1)];

        void BeginForces(ref StepFrame s)
        {
            var p = s.p;
            s.m = Body.mass;
            s.g = Physics.gravity * s.fx.gravity;
            s.force = s.g * s.m - LinearDamping * s.m * s.v;
            s.torqueW = Vector3.zero;
            var angDamp = s.w.magnitude >= p.angularVelCollisionLimit ? p.angularVelCollisionDamping : p.angularVelDamping;
            s.torqueW -= angDamp * WorldInertiaTimes(s.w, s.rot);
            s.steerTorque = ForwardSpeed > 0f
                ? p.extraSteeringTorque * s.steer + (s.steer - prevSteer) * p.extraSteeringTorqueImpulse
                : 0f;
            prevSteer = s.steer;
            s.torqueW += p.uprightTorqueFactor * Vector3.Cross(s.up, Vector3.up);
        }

        void GatherContacts(ref StepFrame s)
        {
            var totalLoad = 0f;
            for (var i = 0; i < WheelCount; i++)
            {
                if (!wheels[i].contact || (!wheels[i].touching && wheels[i].loadNewtons <= 0f)) continue;
                s.contacts++;
                totalLoad += wheels[i].loadNewtons;
                s.groundNormal += wheels[i].normal;
                if (wheels[i].surface != null && wheels[i].surface.offroad) s.resMax = Mathf.Max(s.resMax, OffroadResistance);
                var matLat = wheels[i].surface != null ? wheels[i].surface.lateralFriction : 1f;
                s.gripShare += Mathf.Min(1f, matLat * s.fx.grip) / WheelCount;
            }
            s.avgLoad = totalLoad / 4f;
        }

        // Remake: only part of the gravity along the ground acts on grounded karts (profile or track override), optionally
        // the full gravity pulls into the ground. Scaled by the touching wheels for a smooth take-off.
        void ApplySlopeGravity(ref StepFrame s)
        {
            var slopeFactor = Mathf.Clamp01(s.p.slopeGravityFactor);
            var intoGround = false;
            var race = RaceManager.Current;
            var trackPhysics = race != null && race.Track != null ? race.Track.physics : null;
            if (trackPhysics != null && trackPhysics.overrideSlopeGravity)
            {
                slopeFactor = Mathf.Clamp01(trackPhysics.slopeGravityFactor);
                intoGround = trackPhysics.gravityIntoGround;
            }
            if (!(s.contacts > 0 && (slopeFactor < 1f || intoGround) && s.groundNormal.sqrMagnitude > 1e-6f)) return;
            var g = s.g;
            var gn = s.groundNormal.normalized;
            var gNormal = Vector3.Dot(g, gn) * gn;
            var target = slopeFactor * (g - gNormal) + (intoGround ? -g.magnitude * gn : gNormal);
            s.force += (s.contacts / 4f) * s.m * (target - g);
        }

        // Extra steering torque (6.4), whenever the kart rolls forwards, also while it slides and in the air (MHK2 has no
        // grip, slide or contact condition, the angular velocity collision damping caps the spin). Remake: on the ground
        // scaled towards the side grip under the wheels (steeringTorqueGripFollow), so loose ground does not swing the
        // rear out.
        void ApplySteeringTorque(ref StepFrame s)
        {
            var grip = s.contacts == 0 ? 1f : Mathf.Lerp(1f, s.gripShare, s.p.steeringTorqueGripFollow);
            s.torqueW += s.up * (-s.steerTorque * grip);
        }

        void ApplyDrag(ref StepFrame s)
        {
            var v = s.v;
            if (!(v.sqrMagnitude > 1e-6f)) return;
            if (s.contacts > 0) s.force += -s.up * (s.p.downforceCoefficient * ForwardSpeed * ForwardSpeed);
            s.force += -(s.resMax / s.speedScale + Mathf.Abs(s.p.dragCoefficient)) * v.sqrMagnitude * v.normalized;
        }

        void ApplyDriveAndLimits(ref StepFrame s)
        {
            s.com = Body.worldCenterOfMass;
            for (var i = 0; i < WheelCount; i++)
            {
                ref var wh = ref wheels[i];
                if (!wh.contact) { wh.spinDegPerSec *= FreeWheelSpinDecay; continue; }
                if (!wh.touching && wh.loadNewtons <= 0f)
                {
                    wh.latLimit = wh.lonLimit = 0f;
                    continue;
                }
                var n = s.p.frictionEqualizer * s.avgLoad + (1f - s.p.frictionEqualizer) * wh.loadNewtons;
                ComputeFrictionLimits(ref wh, s.p, n, s.fx.grip, out var matRoll);
                ApplyWheelDrive(ref s, in wh, n, matRoll);
            }
        }

        void ComputeFrictionLimits(ref Wheel wh, KartPhysicsProfile p, float n, float grip, out float matRoll)
        {
            var matLat = 1f;
            matRoll = 1f;
            if (wh.surface != null) { matLat = wh.surface.lateralFriction; matRoll = wh.surface.rollFriction; }
            var muLat = wh.front ? p.lateralFrictionCoeffFront : p.lateralFrictionCoeffRear;
            var muRoll = wh.front ? p.rollFrictionCoeffFront : p.rollFrictionCoeffRear;
            var muLock = wh.front ? p.rollFrictionCoeffLockedFront : p.rollFrictionCoeffLockedRear;
            var brakeW = Mathf.Max(0f, (wh.front ? p.brakeCoefficientFront : p.brakeCoefficientRear) * BrakeAmount);
            var maxBrake = wh.front ? p.maxBrakeFront : p.maxBrakeRear;
            wh.latLimit = matLat * muLat * n * grip;
            wh.lonLimit = matRoll * n * muRoll * brakeW;
            if (wh.lonLimit > maxBrake) wh.lonLimit = muLock * matRoll * n * brakeW;
        }

        void ApplyWheelDrive(ref StepFrame s, in Wheel wh, float n, float matRoll)
        {
            var p = s.p;
            var torqueCoef = wh.front ? p.torqueCoefficientFront : p.torqueCoefficientRear;
            if (torqueCoef == 0f) return;
            var muRoll = wh.front ? p.rollFrictionCoeffFront : p.rollFrictionCoeffRear;
            var fd = s.driveTorque / wh.radius * 0.5f * torqueCoef;
            var lim = matRoll * n * muRoll * s.fx.acceleration;
            fd = Mathf.Clamp(fd, -lim, lim);
            var df = s.fwd * fd;
            s.force += df;
            s.torqueW += Vector3.Cross(wh.point - s.com, df);
        }

        void Integrate(ref StepFrame s)
        {
            s.v += s.force / s.m * s.h;
            s.w += InverseWorldInertiaTimes(s.torqueW, s.rot) * s.h;
            if (s.contacts == 0)
            {
                var steerShare = s.p.maxSteeringAngle > 0f ? Mathf.Clamp(s.steer / s.p.maxSteeringAngle, -1f, 1f) : 0f;
                SteerInAir(ref s.v, steerShare, s.h);
                AlignHeadingInAir(ref s.w, s.v, s.fwd, steerShare, s.h);
            }
            SolveContacts(ref s.v, ref s.w, s.rot, s.h, s.p);
            Body.linearVelocity = s.v;
            Body.angularVelocity = s.w;
        }

        void UpdateWheelState(ref StepFrame s)
        {
            var share = ShareWheelLoads(s.h);
            var contacts = UpdateWheelSlip(ref s, share, out var slip, out var rearUse);
            Rpm = Mathf.Abs(GearRatio(s.p, Gear) * s.diff * (wheels[2].spinDegPerSec + wheels[3].spinDegPerSec) * RpmPerWheelSpin);
            LateralSlip = slip;
            RearGripUsage = Mathf.Clamp01(rearUse);
            UpdateGroundState(contacts, s.h);
        }

        float ShareWheelLoads(float h)
        {
            var totalN = 0f;
            var loaded = 0;
            for (var i = 0; i < WheelCount; i++)
            {
                if (!wheels[i].contact || wheels[i].lambdaN <= 0f) continue;
                totalN += wheels[i].lambdaN / h;
                loaded++;
            }
            return loaded > 0 ? totalN / loaded : 0f;
        }

        int UpdateWheelSlip(ref StepFrame s, float share, out float slip, out float rearUse)
        {
            slip = 0f;
            rearUse = 0f;
            var contacts = 0;
            for (var i = 0; i < WheelCount; i++)
            {
                ref var wh = ref wheels[i];
                var onGround = wh.contact && (wh.lambdaN > 0f || wh.touching);
                wh.loadNewtons = wh.contact && wh.lambdaN > 0f ? share : 0f;
                wh.onGround = onGround;
                wh.lateralSlipSpeed = wh.gripUse = 0f;
                if (!onGround) continue;
                contacts++;
                var pv = s.v + Vector3.Cross(s.w, wh.point - s.com);
                var wf = Quaternion.AngleAxis(wh.front ? SteerAngle : 0f, s.up) * s.fwd;
                wh.spinDegPerSec = Vector3.Dot(pv, wf) / wh.radius * Mathf.Rad2Deg;
                wh.lateralSlipSpeed = Mathf.Abs(Vector3.Dot(pv, Vector3.Cross(s.up, wf)));
                slip += wh.lateralSlipSpeed;
                if (wh.latLimit > 0f) wh.gripUse = Mathf.Clamp01(Mathf.Abs(wh.lambdaLat) / (wh.latLimit * s.h));
                if (!wh.front) rearUse = Mathf.Max(rearUse, wh.gripUse);
            }
            return contacts;
        }

        void UpdateGroundState(int contacts, float h)
        {
            IsGrounded = contacts > 0;
            ContactCount = contacts;
            JustLanded = IsGrounded && !wasGrounded && AirTime > LandingMinAirTime;
            AirTime = IsGrounded ? 0f : AirTime + h;
            wasGrounded = IsGrounded;
        }

        // Remake: in the air the steering turns the velocity about the world up axis (airSteerRate at full lock, steer
        // + = left) while the steering torque turns the nose (MHK2). Only the share of the steering let go steers the yaw
        // rate towards the direction of travel, with ω' = 4r·(r·error − ω), a double pole at −2r, so a kart that leaves a
        // crest turning and is not steered lands straight without overshoot. Pitch and roll stay.
        void SteerInAir(ref Vector3 v, float steerShare, float h)
        {
            var rate = AirSteerRate;
            if (rate <= 0f || ForwardSpeed <= 0f) return;
            v = Quaternion.AngleAxis(-steerShare * rate * h, Vector3.up) * v;
        }

        static void AlignHeadingInAir(ref Vector3 w, Vector3 v, Vector3 fwd, float steerShare, float h)
        {
            var rate = AirHeadingAlign;
            var letGo = 1f - Mathf.Abs(steerShare);
            if (rate <= 0f || letGo <= 0f) return;
            var travel = Vector3.ProjectOnPlane(v, Vector3.up);
            var nose = Vector3.ProjectOnPlane(fwd, Vector3.up);
            if (travel.sqrMagnitude < AirAlignMinSpeedSq || nose.sqrMagnitude < AirAlignMinNoseSq || Vector3.Dot(travel, nose) <= 0f) return;
            var error = Vector3.SignedAngle(nose, travel, Vector3.up) * Mathf.Deg2Rad;
            var yaw = Vector3.Dot(w, Vector3.up);
            w += Vector3.up * ((rate * error - yaw) * (1f - Mathf.Exp(-4f * rate * h)) * letGo);
        }
    }
}
