using System.Collections.Generic;
using UnityEngine;

namespace MHK.Karts
{
    /// <summary>Development tool: replays a fixed input programme (time to throttle, steer and brake) for handling tests.</summary>
    public class ScriptedKartDriver : IKartDriver
    {
        /// <summary>Inputs that apply from <see cref="time"/> (seconds since the driver was created) on.</summary>
        public struct Key
        {
            public float time, throttle, steer, brake;
        }

        readonly List<Key> keys;
        readonly float start;

        /// <summary>Replays <paramref name="keys"/> (sorted by time) starting now.</summary>
        public ScriptedKartDriver(List<Key> keys)
        {
            this.keys = keys;
            start = Time.time;
        }

        public void UpdateInput(Kart kart, ref KartInput input)
        {
            var t = Time.time - start;
            var k = keys.Count > 0 ? keys[0] : default;
            foreach (var key in keys)
                if (key.time <= t) k = key;
            input.throttle = k.throttle;
            input.steer = k.steer;
            input.brake = k.brake;
        }
    }
}
