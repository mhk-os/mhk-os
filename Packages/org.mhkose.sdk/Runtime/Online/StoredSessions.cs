using System;
using System.Collections.Generic;
using MHK.Core;
using UnityEngine;

namespace MHK.Online
{
    /// <summary>
    /// The player's session tokens (account.json in the persistent data folder, apart from the settings), one per server
    /// endpoint so a development and the release server keep their own login. Never holds a password. Field names are
    /// JSON keys: never rename them.
    /// </summary>
    [Serializable]
    public sealed class StoredSessions
    {
        public const string File = "account.json";

        [Serializable]
        public sealed class Entry
        {
            [Tooltip("OnlineEndpoint.Text of the server the session belongs to.")]
            public string endpoint = "";
            [Tooltip("Opaque session token, sent with Resume.")]
            public string token = "";
            [Tooltip("Expiry, Unix milliseconds UTC (sliding: every Resume extends it).")]
            public long expires;
            [Tooltip("Username of the account, shown while the session resumes.")]
            public string username = "";
        }

        public List<Entry> sessions = new();

        public static StoredSessions Load()
        {
            var store = JsonStore.Load<StoredSessions>(File);
            store.sessions ??= new List<Entry>();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            store.sessions.RemoveAll(e => e == null || string.IsNullOrEmpty(e.token) || e.expires <= now);
            return store;
        }

        public void Save() => JsonStore.Save(File, this);

        /// <summary>The unexpired session for <paramref name="endpoint"/>, or null.</summary>
        public Entry Find(string endpoint)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var e in sessions)
                if (e.endpoint == endpoint && e.expires > now) return e;
            return null;
        }

        /// <summary>Stores (or replaces) the session of <paramref name="endpoint"/> and saves.</summary>
        public void Put(string endpoint, Session session)
        {
            var entry = Find(endpoint);
            if (entry == null)
            {
                sessions.RemoveAll(e => e.endpoint == endpoint);
                entry = new Entry { endpoint = endpoint };
                sessions.Add(entry);
            }
            entry.token = session.token;
            entry.expires = session.expires.ToUnixTimeMilliseconds();
            entry.username = session.account.username;
            Save();
        }

        /// <summary>Forgets the session of <paramref name="endpoint"/> and saves when there was one.</summary>
        public void Remove(string endpoint)
        {
            if (sessions.RemoveAll(e => e.endpoint == endpoint) > 0) Save();
        }
    }
}
