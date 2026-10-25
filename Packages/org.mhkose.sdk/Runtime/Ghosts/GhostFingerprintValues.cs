using MHK.Core;
using MHK.Karts;
using MHK.Modding;
using MHK.Race;
using UnityEngine;

namespace MHK.Ghosts
{
    // The gameplay values each fingerprinted type contributes, in hash order. Presentation (sounds, visuals, names) is left
    // out on purpose. Adding, removing or reordering a value invalidates every ghost: change Scheme together with it.
    public static partial class GhostFingerprint
    {
        const int MaxEffectDepth = 2;

        static void AddTrackPhysics(FingerprintBuilder b, TrackPhysicsSettings p)
        {
            if (b.AddNull(p)) return;
            b.Add(p.overrideSlopeGravity);
            b.Add(p.slopeGravityFactor);
            b.Add(p.gravityIntoGround);
        }

        static void AddStartGrid(FingerprintBuilder b, TrackRuntime runtime)
        {
            b.Add(runtime.startGrid.Count);
            foreach (var slot in runtime.startGrid) AddPose(b, slot);
        }

        static void AddPath(FingerprintBuilder b, TrackPath path)
        {
            if (path == null)
            {
                b.Add("no path");
                return;
            }
            b.Add(path.Count);
            b.Add(path.closed);
            for (var i = 0; i < path.Count; i++)
            {
                b.Add(path.GetNodePosition(i));
                b.Add(path.GetNodeWidth(i));
                b.Add((int)path.GetNodeFlags(i));
            }
        }

        static void AddCollider(FingerprintBuilder b, Collider c)
        {
            b.Add(c.GetType().Name);
            b.Add(c.gameObject.layer);
            b.Add(c.isTrigger);
            AddMaterial(b, c.sharedMaterial);
            AddColliderShape(b, c);
            if (Moves(c.transform))
            {
                b.Add(c.transform.lossyScale);
                return;
            }
            var bounds = c.bounds;
            b.Add(bounds.center);
            b.Add(bounds.size);
        }

        static void AddColliderShape(FingerprintBuilder b, Collider c)
        {
            switch (c)
            {
                case BoxCollider box:
                    b.Add(box.size);
                    b.Add(box.center);
                    break;
                case SphereCollider sphere:
                    b.Add(sphere.radius);
                    b.Add(sphere.center);
                    break;
                case CapsuleCollider capsule:
                    b.Add(capsule.radius);
                    b.Add(capsule.height);
                    b.Add(capsule.direction);
                    b.Add(capsule.center);
                    break;
                case MeshCollider mesh:
                    var m = mesh.sharedMesh;
                    b.Add(m != null ? m.vertexCount : 0);
                    b.Add(m != null ? m.bounds.size : Vector3.zero);
                    b.Add(mesh.convex);
                    break;
            }
        }

        static void AddMaterial(FingerprintBuilder b, PhysicsMaterial m)
        {
            if (b.AddNull(m)) return;
            b.Add(m.dynamicFriction);
            b.Add(m.staticFriction);
            b.Add(m.bounciness);
            b.Add((int)m.frictionCombine);
            b.Add((int)m.bounceCombine);
        }

        static void AddSurface(FingerprintBuilder b, TrackSurface s)
        {
            b.Add((int)s.kind);
            b.Add(s.lateralFriction);
            b.Add(s.rollFriction);
            b.Add(s.offroad);
        }

        static void AddZone(FingerprintBuilder b, TrackZone z)
        {
            b.Add((int)z.kind);
            b.Add(z.value);
            AddEffectById(b, z.effectId);
            b.Add(z.pushFalloff);
            b.Add(z.pushFalloffMinDistance);
            b.Add(z.pushMaxSpeed);
            if (z.respawnPoint != null) AddPose(b, z.respawnPoint);
            else b.Add("no respawn point");
            AddStaticPose(b, z.transform);
        }

        static void AddHazard(FingerprintBuilder b, TrackHazard h)
        {
            b.Add((int)h.kind);
            AddEffectById(b, h.effectId);
            b.Add(h.minDownSpeed);
            b.Add(h.minVerticalNormal);
            b.Add(h.minRoofHeight);
            AddStaticPose(b, h.transform);
        }

        static void AddProp(FingerprintBuilder b, PropAnimator p)
        {
            AddClip(b, p.clip);
            b.Add(p.alternates != null ? p.alternates.Length : 0);
            if (p.alternates != null)
                foreach (var clip in p.alternates) AddClip(b, clip);
            b.Add(p.speed);
            b.Add(p.pingPong);
            b.Add(p.randomizeStart);
            b.Add(p.bodies != null ? p.bodies.Length : 0);
        }

        // Only what decides when and where karts fall; the pieces' fall is visual.
        static void AddBridge(FingerprintBuilder b, CollapsingBridge c, TrackRuntime runtime)
        {
            b.Add(runtime != null && c.route != null ? runtime.alternatePaths.IndexOf(c.route) : -1);
            b.Add(c.gates.Count);
            foreach (var g in c.gates)
            {
                b.Add(c.transform.TransformPoint(g.left));
                b.Add(c.transform.TransformPoint(g.right));
            }
            b.Add(c.sideMargin);
            b.Add(c.heightRange);
            b.Add(c.triggerWait);
            b.Add(c.dropWait);
        }

        static void AddClip(FingerprintBuilder b, AnimationClip clip)
        {
            b.Add(clip != null ? clip.name : "");
            b.Add(clip != null ? clip.length : 0f);
        }

        static void AddEffectById(FingerprintBuilder b, string id)
        {
            b.Add(id ?? "");
            if (!string.IsNullOrEmpty(id)) AddEffect(b, GameRoot.Instance.Content.Get<StatusEffectDefinition>(id));
        }

        static void AddEffect(FingerprintBuilder b, StatusEffectDefinition e, int depth = 0)
        {
            if (b.AddNull(e)) return;
            b.Add(e.Id);
            b.Add(e.duration);
            b.Add(e.durationFromAnimation);
            b.Add(e.refreshOnReapply);
            b.Add(e.exclusiveBuff);
            b.Add(e.accelerationMultiplier);
            b.Add(e.maxSpeedMultiplier);
            b.Add(e.modifiersMinSpeed);
            b.Add(e.steeringMultiplier);
            b.Add(e.gripMultiplier);
            b.Add(e.forwardImpulse);
            b.Add(e.velocityRetain);
            b.Add(e.velocityDamping);
            // Added only when set, so the hashes of every effect without it stay as they were.
            if (e.snapToTopSpeed) b.Add("snapToTopSpeed");
            AddEffectControl(b, e);
            AddEffectProtection(b, e);
            b.Add(e.scaleMultiplier);
            b.Add(e.auraRadius);
            b.Add(e.useCharacterAnimation);
            b.Add((int)e.characterAnimation);
            AddNestedEffect(b, e.landingEffect, depth);
            AddNestedEffect(b, e.auraEffect, depth);
        }

        static void AddEffectControl(FingerprintBuilder b, StatusEffectDefinition e)
        {
            b.Add(e.lockControls);
            b.Add(e.forceThrottle);
            b.Add(e.blockThrottle);
            b.Add(e.blocksItemUse);
            b.Add(e.invertSteering);
            b.Add(e.freezeKart);
            b.Add(e.upwardImpulse);
            b.Add(e.respawnOnEnd);
            b.Add(e.respawnBackDistance);
            b.Add(e.gravityMultiplier);
            b.Add(e.endOnLanding);
        }

        static void AddEffectProtection(FingerprintBuilder b, StatusEffectDefinition e)
        {
            b.Add(e.blocksHostileEffects);
            b.Add(e.hostile);
            b.Add(e.dropsHeldItem);
            b.Add(e.cancelsBuffs);
            b.Add(e.survivesHits);
            b.Add(e.ghost);
            b.Add(e.runOver);
            // Added only when set, so the hashes of every effect without it stay as they were.
            if (e.hideKart)
            {
                b.Add("hideKart");
                b.Add(e.hideDelay);
            }
            b.Add(e.damage);
            b.Add(e.heal);
            b.Add(e.damageDealtMultiplier);
            b.Add(e.damageTakenMultiplier);
        }

        static void AddNestedEffect(FingerprintBuilder b, StatusEffectDefinition e, int depth)
        {
            if (depth < MaxEffectDepth) AddEffect(b, e, depth + 1);
            else b.Add(e != null ? e.Id : "null");
        }

        static void AddProfile(FingerprintBuilder b, KartPhysicsProfile p)
        {
            if (b.AddNull(p)) return;
            b.Add(p.mass);
            b.Add(p.chassisSize);
            b.Add(p.centerOfMass);
            b.Add(p.chassisCollOffset);
            AddWheels(b, p);
            AddDrivetrain(b, p);
            AddHandling(b, p);
        }

        static void AddWheels(FingerprintBuilder b, KartPhysicsProfile p)
        {
            b.Add(p.wheelsRadiusFront);
            b.Add(p.wheelsRadiusRear);
            b.Add(p.wheelsStiffnessFront);
            b.Add(p.wheelsStiffnessRear);
            b.Add(p.wheelsDampingFront);
            b.Add(p.wheelsDampingRear);
            b.Add(p.lateralFrictionCoeffFront);
            b.Add(p.lateralFrictionCoeffRear);
            b.Add(p.rollFrictionCoeffFront);
            b.Add(p.rollFrictionCoeffRear);
            b.Add(p.rollFrictionCoeffLockedFront);
            b.Add(p.rollFrictionCoeffLockedRear);
            b.Add(p.wheelCollOffsets);
            b.Add(p.brakeCoefficientFront);
            b.Add(p.brakeCoefficientRear);
            b.Add(p.maxBrakeFront);
            b.Add(p.maxBrakeRear);
            b.Add(p.torqueCoefficientFront);
            b.Add(p.torqueCoefficientRear);
        }

        static void AddDrivetrain(FingerprintBuilder b, KartPhysicsProfile p)
        {
            b.Add(p.gears);
            b.Add(p.gearReverse);
            b.Add(p.diffRatio);
            b.Add(p.changeUpRpm);
            b.Add(p.torqueCurve != null ? p.torqueCurve.Length : 0);
            if (p.torqueCurve == null) return;
            foreach (var t in p.torqueCurve)
            {
                b.Add(t.rpm);
                b.Add(t.torque);
                b.Add(t.engineBraking);
            }
        }

        static void AddHandling(FingerprintBuilder b, KartPhysicsProfile p)
        {
            b.Add(p.steeringWashout);
            b.Add(p.steeringWashoutPower);
            b.Add(p.dragCoefficient);
            b.Add(p.downforceCoefficient);
            b.Add(p.steeringSpeed);
            b.Add(p.steeringRestSpeed);
            b.Add(p.maxSteeringAngle);
            b.Add(p.accelerationSpeed);
            b.Add(p.brakeSpeed);
            b.Add(p.frictionEqualizer);
            b.Add(p.angularVelDamping);
            b.Add(p.angularVelCollisionLimit);
            b.Add(p.angularVelCollisionDamping);
            b.Add(p.extraSteeringTorque);
            b.Add(p.extraSteeringTorqueImpulse);
            b.Add(p.uprightTorqueFactor);
            b.Add(p.lateralSlipAngle);
            b.Add(p.steeringTorqueGripFollow);
            b.Add(p.slopeGravityFactor);
            b.Add(p.boostOverspeedDecay);
        }

        // The chassis component of the kart root prefab. Its profile reference is replaced per kart (AddProfile).
        static void AddChassis(FingerprintBuilder b, KartPhysics k)
        {
            if (b.AddNull(k)) return;
            b.Add(k.GroundMask.value);
            AddMaterial(b, k.ChassisMaterial);
            b.Add(k.SolverIterations);
            b.Add(k.ContactMargin);
            b.Add(k.LateralForceHeight);
            b.Add(k.WallMaxNormalY);
            b.Add(k.GhostExcludeLayers.value);
            b.Add(k.RunOverExcludeLayers.value);
            b.Add(k.GhostExitTimeout);
        }

        static void AddPose(FingerprintBuilder b, Transform t)
        {
            if (b.AddNull(t)) return;
            b.Add(t.position);
            b.Add(t.rotation.eulerAngles);
        }

        static void AddStaticPose(FingerprintBuilder b, Transform t)
        {
            if (Moves(t)) return;
            AddPose(b, t);
            b.Add(t.lossyScale);
        }
    }
}
