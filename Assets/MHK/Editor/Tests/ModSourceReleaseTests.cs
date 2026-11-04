using System;
using System.Collections;
using System.Reflection;
using System.Threading;
using MHK.Modding;
using NUnit.Framework;
using UnityEngine;

namespace MHK.Tests
{
    public class ModSourceReleaseTests
    {
        const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
        const string OldCommit = "e8d34d2", NewCommit = "e09c752";
        const string OldDate = "2026-11-04T03:15:40Z", NewDate = "2026-11-04T12:28:40Z";

        [Test]
        public void OlderFirstInGitHubResponseStillOffersTheNewerPublishedMod()
        {
            var source = new ModSources.Source
            {
                repo = "tests/" + Guid.NewGuid().ToString("N"),
                kind = ModSources.Kind.Mod, installed = true, installedCommit = OldCommit
            };
            Choose(source, "[" + Release(OldCommit, OldDate, 10) + "," + Release(NewCommit, NewDate, 20) + "]",
                (10, Manifest(OldCommit, 22)), (20, Manifest(NewCommit, 25)));

            Assert.AreEqual(NewCommit, source.latestCommit);
            Assert.AreEqual(ModSources.State.UpdateAvailable, Evaluate(source));
        }

        [Test]
        public void NewestIncompatibleModDoesNotHideTheLatestCompatibleUpdate()
        {
            var source = new ModSources.Source
            {
                repo = "tests/" + Guid.NewGuid().ToString("N"),
                kind = ModSources.Kind.Mod, installed = true, installedCommit = NewCommit
            };
            Choose(source, "[" + Release(OldCommit, OldDate, 10) + "," +
                Release("fffffff", "2026-11-04T13:00:00Z", 30) + "," + Release(NewCommit, NewDate, 20) + "]",
                (10, Manifest(OldCommit, 22)), (20, Manifest(NewCommit, 25)),
                (30, Manifest("fffffff", 26, ModSdk.ApiLevel + 1)));

            Assert.AreEqual(NewCommit, source.latestCommit);
            Assert.AreEqual(ModSources.State.NeedsNewerGame, Evaluate(source));
        }

        [Test]
        public void AssetPacksAlsoUsePublicationTimeInsteadOfResponseOrder()
        {
            var source = new ModSources.Source
            {
                repo = "tests/" + Guid.NewGuid().ToString("N"), kind = ModSources.Kind.AssetPack
            };
            Choose(source, "[" + Release("older-pack", OldDate, 10, false) + "," +
                Release("newer-pack", NewDate, 20, false) + "]");

            Assert.AreEqual("newer-pack", source.latestCommit);
            Assert.AreEqual(ModSources.State.NotInstalled, Evaluate(source));
        }

        static string Release(string tag, string published, long manifestId, bool mod = true) =>
            "{\"tag_name\":\"" + tag + "\",\"published_at\":\"" + published + "\",\"assets\":[" +
            (mod ? Asset("mod.json", manifestId) + "," : "") + Asset("pack.zip", manifestId + 1) + "]}";

        static string Asset(string name, long id) =>
            "{\"id\":" + id + ",\"name\":\"" + name + "\",\"browser_download_url\":\"https://example.invalid/" + id + "\"}";

        static string Manifest(string commit, int sdkBuild, int api = ModSdk.ApiLevel) =>
            "{\"packId\":\"mhke\",\"commit\":\"" + commit + "\",\"sdkBuild\":" + sdkBuild + ",\"sdkApi\":" + api +
            ",\"targets\":{\"StandaloneOSX\":\"pack.zip\",\"StandaloneWindows64\":\"pack.zip\"," +
            "\"StandaloneLinux64\":\"pack.zip\",\"Android\":\"pack.zip\",\"iOS\":\"pack.zip\"}}";

        static void Choose(ModSources.Source source, string body, params (long id, string json)[] manifests)
        {
            var type = typeof(ModSources);
            var cache = Activator.CreateInstance(type.GetNestedType("RepoCache", BindingFlags.NonPublic), true);
            var entries = (IList)cache.GetType().GetField("manifests").GetValue(cache);
            var manifestType = type.GetNestedType("CachedManifest", BindingFlags.NonPublic);
            foreach (var (id, json) in manifests)
            {
                var entry = Activator.CreateInstance(manifestType, true);
                manifestType.GetField("assetId").SetValue(entry, id);
                manifestType.GetField("json").SetValue(entry, json);
                entries.Add(entry);
            }
            var releases = type.GetMethod("ParseReleases", PrivateStatic).Invoke(null, new object[] { body });
            var pending = (Awaitable)type.GetMethod("ChooseAsync", PrivateStatic)
                .Invoke(null, new[] { source, releases, cache, CancellationToken.None });
            pending.GetAwaiter().GetResult();
        }

        static ModSources.State Evaluate(ModSources.Source source) =>
            (ModSources.State)typeof(ModSources).GetMethod("Evaluate", PrivateStatic).Invoke(null, new object[] { source });
    }
}
