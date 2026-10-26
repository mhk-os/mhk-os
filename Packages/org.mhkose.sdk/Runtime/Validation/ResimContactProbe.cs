using UnityEngine;

namespace MHK.Validation
{
    /// <summary>
    /// Notes whether a re-simulated kart's chassis hit a wall: chassis contacts with walls are where PhysX makes small
    /// state differences grow, so what a re-simulation shows after one says nothing about the recorded inputs. Ground
    /// contacts (a chassis grazing the road) do not count.
    /// </summary>
    [AddComponentMenu("")]
    public class ResimContactProbe : MonoBehaviour
    {
        /// <summary>Contact normals flatter than this (|y|) are walls, as for <see cref="Karts.KartPhysics.WallMaxNormalY"/>.</summary>
        const float WallMaxNormalY = 0.6f;

        public bool Touched { get; set; }

        void OnCollisionEnter(Collision c) => Check(c);
        void OnCollisionStay(Collision c) => Check(c);

        void Check(Collision c)
        {
            for (var i = 0; i < c.contactCount && !Touched; i++)
                if (Mathf.Abs(c.GetContact(i).normal.y) < WallMaxNormalY) Touched = true;
        }
    }
}
