using System;

namespace MHK.Online
{
    /// <summary>
    /// The server's username and password rules (docs/protocol.md, accounts), checked before sending so the player gets
    /// a clear message without a round trip. The server enforces them anyway.
    /// </summary>
    public static class AccountRules
    {
        public const int MinUsername = 3, MaxUsername = 20;
        public const int MinPassword = 8, MaxPassword = 128;

        static readonly string[] Reserved = { "admin", "administrator", "moderator", "mod", "system", "server", "root", "mhk", "mhkos", "mhk-os" };

        /// <summary>Why <paramref name="username"/> cannot be registered, or null when it follows the rules.</summary>
        public static string CheckUsername(string username)
        {
            username ??= "";
            if (username.Length < MinUsername) return $"The username needs at least {MinUsername} characters.";
            if (username.Length > MaxUsername) return $"The username can have at most {MaxUsername} characters.";
            foreach (var c in username)
                if (!IsLetterOrDigit(c) && c != '_' && c != '.' && c != '-')
                    return "The username may only use letters A-Z, digits and _ . -";
            if (!IsLetterOrDigit(username[0]) || !IsLetterOrDigit(username[username.Length - 1]))
                return "The username must start and end with a letter or digit.";
            foreach (var r in Reserved)
                if (string.Equals(r, username, StringComparison.OrdinalIgnoreCase)) return $"\"{username}\" is reserved. Pick another username.";
            return null;
        }

        /// <summary>Why <paramref name="password"/> cannot be used for <paramref name="username"/>, or null when it follows the rules.</summary>
        public static string CheckPassword(string password, string username)
        {
            password ??= "";
            var length = CodePoints(password);
            if (length < 0) return "The password contains characters that cannot be used.";
            if (length < MinPassword) return $"The password needs at least {MinPassword} characters.";
            if (length > MaxPassword) return $"The password can have at most {MaxPassword} characters.";
            if (string.Equals(password, username ?? "", StringComparison.OrdinalIgnoreCase)) return "The password must differ from the username.";
            return null;
        }

        /// <summary>Whether a login may be attempted at all (the server checks the rest).</summary>
        public static string CheckLogin(string username, string password)
        {
            if (string.IsNullOrWhiteSpace(username)) return "Enter your username.";
            if (string.IsNullOrEmpty(password)) return "Enter your password.";
            if (username.Length > CredentialsRequest.MaxUsername) return "No account has that username.";
            if (CodePoints(password) < 0) return "The password contains characters that cannot be used.";
            return null;
        }

        static bool IsLetterOrDigit(char c) => c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9';

        // Unicode code points of s, -1 when it holds a lone surrogate (cannot be sent as UTF-8).
        static int CodePoints(string s)
        {
            var count = 0;
            for (var i = 0; i < s.Length; i++, count++)
            {
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
                else if (char.IsSurrogate(s[i])) return -1;
            }
            return count;
        }
    }
}
