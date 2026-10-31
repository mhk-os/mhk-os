using MHK.Race;
using NUnit.Framework;
using UnityEngine;

namespace MHK.Tests
{
    public class WorldSkyDomeTests
    {
        [TestCase("tincan", true)]
        [TestCase("MHK3", true)]
        [TestCase("mhkt", true)]
        [TestCase("mhk2", false)]
        [TestCase("mhke", false)]
        [TestCase(null, false)]
        public void OnlyWorldSkyGamesConvertLegacyFollowers(string game, bool expected)
        {
            Assert.AreEqual(expected, WorldSkyDome.IsWorldSkyGame(game));
        }

        [Test]
        public void LegacyFollowerRestoresAuthoredPlacementAfterMovingAndShrinking()
        {
            var dome = new GameObject("Legacy sky");
            var shell = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            try
            {
                shell.transform.SetParent(dome.transform, false);
                shell.transform.localScale = Vector3.one * 100f;
                var position = new Vector3(12f, 34f, 56f);
                var scale = new Vector3(2f, 3f, 4f);
                dome.transform.localPosition = position;
                dome.transform.localScale = scale;
                var follow = dome.AddComponent<FollowCameraPosition>();

                follow.Place(new Vector3(500f, 600f, 700f), 10f);
                Assert.AreNotEqual(position, dome.transform.localPosition);
                Assert.AreNotEqual(scale, dome.transform.localScale);

                follow.RestorePlacement();
                Assert.AreEqual(position, dome.transform.localPosition);
                Assert.AreEqual(scale, dome.transform.localScale);
            }
            finally
            {
                Object.DestroyImmediate(dome);
            }
        }
    }
}
