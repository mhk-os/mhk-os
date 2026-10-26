using System.Globalization;
using MHK.Core;
using MHK.Online;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Online account (Account.uxml): log in or register (username, password, confirmation, checked against the server's
    /// rules first), or, logged in, the account with its country and Log out. Below, the connection to the server and,
    /// behind the server button, the server address (<see cref="UserSettings.onlineServer"/>). Opening it connects
    /// (<see cref="OnlineService.Connect"/>).
    /// </summary>
    public class AccountScreen : MenuScreen
    {
        const int CountryApplyDelayMs = 600;

        [SerializeField, Tooltip("Log in / Register chip (Templates/Chip.uxml).")]
        VisualTreeAsset chipTemplate;
        [SerializeField, Tooltip("Chip texts of the two modes.")]
        string loginTab = "Log in", registerTab = "Register";
        [SerializeField, Tooltip("Submit button texts: logging in, registering, and while the server is asked.")]
        string loginText = "Log in", registerText = "Create Account", busyText = "Please wait";
        [SerializeField, Tooltip("Error when the confirmation differs from the password.")]
        string mismatchText = "The passwords do not match.";
        [SerializeField, Tooltip("Status after registering. {0} = username.")]
        string welcomeFormat = "Welcome, {0}! Your account is ready.";
        [SerializeField, Tooltip("Country value that follows the registration IP: with the detected country ({0} = code), and while it is unknown.")]
        string autoCountryFormat = "Auto (from IP: {0})", autoCountryText = "Auto (from IP)";
        [SerializeField, Tooltip("Line under the username. {0} = registration date.")]
        string sinceFormat = "Racing online since {0}";
        [SerializeField, Tooltip("Connection line: connecting, connected ({0} = server), unencrypted suffix, offline, offline with a reason ({1}).")]
        string connectingFormat = "Connecting to {0}...", connectedFormat = "Connected to {0}", plaintextSuffix = " (unencrypted)",
            offlineFormat = "Offline: {0}", offlineReasonFormat = "Offline: {1}";
        [SerializeField, Tooltip("Appended to the offline line while a reconnect is scheduled. {0} = seconds.")]
        string retryFormat = " Retrying in {0} s.";
        [SerializeField, Tooltip("Server note while the -server command-line option sets the server. {0} = that server.")]
        string commandLineNote = "The -server option on the command line sets the server ({0}). This field applies when the game starts without it.";

        bool register;
        bool serverOpen;
        TextField username, password, confirm, server;
        Button submit, reconnect;
        VisualElement signedOut, signedIn, confirmBlock, status, statusIcon, connectionBar, connectionIcon, serverBox;
        Label submitLabel, statusLabel, connectionLabel;
        VisualElement submitIcon;
        SettingChoice country;
        IVisualElementScheduledItem tick, countryApply;
        bool busy, shownLoggedIn;
        // Country picked but not sent yet, and the one sent and not answered yet (-1: none); both show as the value.
        int pendingCountry = -1, sentCountry = -1, countryRequests;

        void Reset() => showcaseVisible = false;

        protected override void Bind()
        {
            signedOut = Q<VisualElement>("signed-out");
            signedIn = Q<VisualElement>("signed-in");
            confirmBlock = Q<VisualElement>("confirm-block");
            username = Q<TextField>("username");
            password = Q<TextField>("password");
            confirm = Q<TextField>("confirm");
            submit = OnClick("submit", Submit);
            submitLabel = Q<Label>("submit-label");
            submitIcon = Q<VisualElement>("submit-icon");
            status = Q<VisualElement>("form-status");
            statusIcon = Q<VisualElement>("form-status-icon");
            statusLabel = Q<Label>("form-status-label");
            connectionBar = Q<VisualElement>("connection-bar");
            connectionIcon = Q<VisualElement>("connection-icon");
            connectionLabel = Q<Label>("connection-label");
            reconnect = OnClick("reconnect", OnlineService.Connect);
            OnClick("logout", () => LogOutAsync().LogExceptionsAndForget());
            ChipGroup.Fill(Q<VisualElement>("modes"), chipTemplate, new[] { loginTab, registerTab }, register ? 1 : 0, index =>
            {
                Click();
                register = index == 1;
                ShowStatus(null, false);
                ShowMode();
            });
            foreach (var field in new[] { username, password, confirm })
            {
                field.RegisterCallback<KeyDownEvent>(OnFormKey, TrickleDown.TrickleDown);
                field.RegisterValueChangedCallback(_ => ShowStatus(null, false));
            }
            country = BindChoice("country", () => Countries.All.Count + 1, CountryIndex, CountryText, SelectCountry);
            countryApply = Root.schedule.Execute(ApplyCountry);
            countryApply.Pause();
            BindServer();
            OnlineService.Changed += ShowState;
            tick = Root.schedule.Execute(ShowConnection).Every(500);
            shownLoggedIn = OnlineService.IsLoggedIn;
            FocusTarget = shownLoggedIn ? country.Button : username;
            ShowMode();
            ShowStatus(null, false);
            ShowState();
            OnlineService.Connect();
        }

        protected override void Unbind()
        {
            base.Unbind();
            OnlineService.Changed -= ShowState;
            tick?.Pause();
            countryApply?.Pause();
            // A country picked just before leaving is still sent.
            ApplyCountry();
            tick = countryApply = null;
            username = password = confirm = server = null;
            submit = reconnect = null;
            signedOut = signedIn = confirmBlock = status = statusIcon = connectionBar = connectionIcon = serverBox = submitIcon = null;
            submitLabel = statusLabel = connectionLabel = null;
            country = null;
            busy = false;
        }

        void BindServer()
        {
            serverBox = Q<VisualElement>("server-box");
            server = Q<TextField>("server");
            server.SetValueWithoutNotify(Settings.onlineServer);
            server.textEdition.placeholder = OnlineEndpoint.DefaultText(BuildInfo.Number);
            server.RegisterCallback<KeyDownEvent>(OnServerKey, TrickleDown.TrickleDown);
            if (OnlineEndpoint.FromCommandLine) Q<Label>("server-note").text = string.Format(commandLineNote, OnlineService.EndpointText);
            OnClick("server-toggle", () =>
            {
                serverOpen = !serverOpen;
                ShowServer();
                if (serverOpen) FocusLater(server);
            });
            OnClick("server-apply", ApplyServer);
            // An unusable address opens the box so it can be fixed.
            if (OnlineService.EndpointError != null) serverOpen = true;
            ShowServer();
        }

        void ShowServer() => serverBox.SetVisible(serverOpen);

        void ApplyServer()
        {
            var text = server.value.Trim();
            if (text.Length > 0 && !OnlineEndpoint.TryParse(text, out _, out var error))
            {
                ShowStatus(error, false);
                FocusLater(server);
                return;
            }
            ShowStatus(null, false);
            OnlineService.SetServer(text);
            OnlineService.Connect();
        }

        void OnServerKey(KeyDownEvent e)
        {
            if (e.keyCode != KeyCode.Return && e.keyCode != KeyCode.KeypadEnter) return;
            e.StopPropagation();
            Click();
            ApplyServer();
        }

        void OnFormKey(KeyDownEvent e)
        {
            if (e.keyCode != KeyCode.Return && e.keyCode != KeyCode.KeypadEnter) return;
            e.StopPropagation();
            Click();
            Submit();
        }

        void ShowMode()
        {
            confirmBlock.SetVisible(register);
            submitLabel.text = busy ? busyText : register ? registerText : loginText;
            submitIcon.SetIcon(register ? "person_add" : "login");
        }

        void SetBusy(bool on)
        {
            busy = on;
            username.SetEnabled(!on);
            password.SetEnabled(!on);
            confirm.SetEnabled(!on);
            submit.SetEnabled(!on);
            ShowMode();
        }

        void ShowStatus(string text, bool good)
        {
            if (status == null) return;
            status.SetVisible(!string.IsNullOrEmpty(text));
            status.EnableInClassList("good", good);
            statusIcon.SetIcon(good ? "check" : "error");
            statusLabel.text = text ?? "";
        }

        void Submit()
        {
            if (busy) return;
            var name = username.value.Trim();
            var pass = password.value;
            string error;
            TextField wrong;
            if (register)
            {
                error = AccountRules.CheckUsername(name);
                wrong = username;
                if (error == null)
                {
                    error = AccountRules.CheckPassword(pass, name);
                    wrong = password;
                }
                if (error == null && pass != confirm.value)
                {
                    error = mismatchText;
                    wrong = confirm;
                }
            }
            else
            {
                error = AccountRules.CheckLogin(name, pass);
                wrong = string.IsNullOrWhiteSpace(name) ? username : password;
            }
            if (error != null)
            {
                ShowStatus(error, false);
                FocusLater(wrong);
                return;
            }
            SubmitAsync(name, pass, register).LogExceptionsAndForget();
        }

        async Awaitable SubmitAsync(string name, string pass, bool registering)
        {
            var root = Root;
            ShowStatus(null, false);
            SetBusy(true);
            var result = registering ? await OnlineService.Register(name, pass) : await OnlineService.Login(name, pass);
            if (Root != root || root == null) return;
            SetBusy(false);
            if (result.Ok)
            {
                ClearPasswords();
                ShowStatus(registering ? string.Format(welcomeFormat, OnlineService.Username) : null, true);
                return;
            }
            ShowStatus(result.message, false);
            if (result.status == Status.InvalidCredentials || result.status == Status.InvalidPassword)
            {
                ClearPasswords();
                FocusLater(password);
            }
            else if (result.status == Status.UsernameTaken || result.status == Status.InvalidUsername) FocusLater(username);
            else FocusLater(submit);
        }

        // Without the change callback, which would hide the status just shown.
        void ClearPasswords()
        {
            password.SetValueWithoutNotify("");
            confirm.SetValueWithoutNotify("");
        }

        async Awaitable LogOutAsync()
        {
            var root = Root;
            var result = await OnlineService.Logout();
            if (Root != root || root == null) return;
            ShowStatus(result.Ok ? null : result.message, false);
        }

        // Login state, account and connection, whenever the service changes.
        void ShowState()
        {
            if (signedIn == null) return;
            var account = OnlineService.Account;
            var loggedIn = account != null;
            signedOut.SetVisible(!loggedIn);
            signedIn.SetVisible(loggedIn);
            if (loggedIn)
            {
                Q<Label>("account-name").text = account.username;
                SetVisible("moderator-badge", account.IsModerator);
                Q<Label>("account-since").text = string.Format(sinceFormat, account.created.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.InvariantCulture));
                country.Refresh();
            }
            if (loggedIn != shownLoggedIn)
            {
                shownLoggedIn = loggedIn;
                FocusLater(loggedIn ? country.Button : username);
            }
            ShowConnection();
        }

        void ShowConnection()
        {
            if (connectionLabel == null) return;
            var endpoint = OnlineService.EndpointText;
            var error = OnlineService.LastError;
            string text;
            switch (OnlineService.State)
            {
                case OnlineState.Connecting:
                    text = string.Format(connectingFormat, endpoint);
                    break;
                case OnlineState.Connected:
                case OnlineState.LoggedIn:
                    text = string.Format(connectedFormat, endpoint) + (OnlineService.Encrypted ? "" : plaintextSuffix);
                    // A session the server refused while the connection stays open.
                    if (!string.IsNullOrEmpty(error)) text += ". " + error;
                    break;
                default:
                    text = string.Format(string.IsNullOrEmpty(error) ? offlineFormat : offlineReasonFormat, endpoint, error);
                    var retry = Mathf.CeilToInt(OnlineService.RetryIn);
                    if (retry > 0) text += string.Format(retryFormat, retry);
                    break;
            }
            connectionLabel.text = text;
            var online = OnlineService.State >= OnlineState.Connected;
            connectionBar.EnableInClassList("online", online);
            connectionIcon.SetIcon(online ? "cloud_done" : OnlineService.State == OnlineState.Connecting ? "sync" : "cloud_off");
            reconnect.SetVisible(OnlineService.State == OnlineState.Offline && OnlineService.EndpointError == null);
        }

        int CountryIndex()
        {
            if (pendingCountry >= 0) return pendingCountry;
            if (sentCountry >= 0) return sentCountry;
            var account = OnlineService.Account;
            if (account == null || account.countryFromIp) return 0;
            return Countries.IndexOf(account.country) + 1;
        }

        string CountryText(int index)
        {
            if (index > 0) return Countries.All[index - 1].name;
            var ip = OnlineService.IpCountry;
            return string.IsNullOrEmpty(ip) ? autoCountryText : string.Format(autoCountryFormat, ip);
        }

        // Stepping through countries with Left / Right only sends the one the player stops at.
        void SelectCountry(int index)
        {
            pendingCountry = index;
            countryApply?.ExecuteLater(CountryApplyDelayMs);
        }

        void ApplyCountry()
        {
            var index = pendingCountry;
            if (index < 0) return;
            pendingCountry = -1;
            sentCountry = index;
            ApplyCountryAsync(index == 0 ? "" : Countries.All[index - 1].code, ++countryRequests).LogExceptionsAndForget();
        }

        async Awaitable ApplyCountryAsync(string code, int request)
        {
            var result = await OnlineService.SetCountry(code);
            // A later pick replaces this one.
            if (request != countryRequests) return;
            sentCountry = -1;
            if (country == null) return;
            country.Refresh();
            ShowStatus(result.Ok ? null : result.message, false);
        }
    }
}
