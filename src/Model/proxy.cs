using System;
using System.IO;
using System.Text;
using System.Net;
using System.Net.Cache;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Xml;
using System.Net.Http;


namespace XmlNotepad
{

    public class XmlProxyResolver : XmlUrlResolver
    {
        private readonly WebProxyService _ps;

        public XmlProxyResolver(IServiceProvider site)
        {
            _ps = site.GetService(typeof(WebProxyService)) as WebProxyService;
            Proxy = HttpWebRequest.DefaultWebProxy;
        }

        public override object GetEntity(Uri absoluteUri, string role, Type ofObjectToReturn)
        {
            if (absoluteUri == null)
            {
                throw new ArgumentNullException("absoluteUri");
            }
            if ((absoluteUri.Scheme == "http" || absoluteUri.Scheme == "https")
                && (ofObjectToReturn == null || ofObjectToReturn == typeof(Stream)))
            {
                try
                {
                    return GetResponse(absoluteUri);
                }
                catch (Exception e)
                {
                    if (WebProxyService.ProxyAuthenticationRequired(e))
                    {
                        WebProxyState state = _ps.PrepareWebProxy(this.GetProxy(), absoluteUri.AbsoluteUri, WebProxyState.DefaultCredentials, true);
                        if (state != WebProxyState.Abort)
                        {
                            // try again...
                            return GetResponse(absoluteUri);
                        }
                    }
                    throw;
                }

            }
            else
            {
                Debug.WriteLine($"Loading {absoluteUri}");
                return base.GetEntity(absoluteUri, role, ofObjectToReturn);
            }
        }

        Stream GetResponse(Uri uri)
        {
            Debug.WriteLine($"Loading Uri {uri}");
            HttpClient client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(60);
            var result = client.GetAsync(uri).Result;
            result.EnsureSuccessStatusCode();
            return result.Content.ReadAsStreamAsync().Result;            

            //WebRequest webReq = WebRequest.Create(uri);
            //webReq.CachePolicy = new HttpRequestCachePolicy(HttpRequestCacheLevel.Default);
            //webReq.Credentials = CredentialCache.DefaultCredentials;
            //webReq.Proxy = this.GetProxy();
            //webReq.Timeout = 60;
            //WebResponse resp = webReq.GetResponse();
            //return resp.GetResponseStream();
        }

        IWebProxy GetProxy()
        {
            return HttpWebRequest.DefaultWebProxy;
        }
    }

    public enum WebProxyState
    {
        NoCredentials = 0,
        DefaultCredentials = 1,
        CachedCredentials = 2,
        PromptForCredentials = 3,
        Abort = 4
    };

    public enum CredentialPromptResult
    {
        OK,
        Cancel,
        Error
    }

    public class WebProxyService
    {
        private readonly IServiceProvider _site;
        private NetworkCredential _cachedCredentials;
        private string _currentProxyUrl;

        public WebProxyService(IServiceProvider site)
        {
            this._site = site;
        }

        //---------------------------------------------------------------------
        // public methods
        //---------------------------------------------------------------------
        public static bool ProxyAuthenticationRequired(Exception ex)
        {
            bool authNeeded = false;

            System.Net.WebException wex = ex as System.Net.WebException;

            if ((wex != null) && (wex.Status == System.Net.WebExceptionStatus.ProtocolError))
            {
                System.Net.HttpWebResponse hwr = wex.Response as System.Net.HttpWebResponse;
                if ((hwr != null) && (hwr.StatusCode == System.Net.HttpStatusCode.ProxyAuthenticationRequired))
                {
                    authNeeded = true;
                }
            }

            return authNeeded;
        }

        /// <summary>
        /// This method attaches credentials to the web proxy object.
        /// </summary>
        /// <param name="proxy">The proxy to attach credentials to.</param>
        /// <param name="webCallUrl">The url for the web call.</param>
        /// <param name="oldProxyState">The current state fo the web call.</param>
        /// <param name="newProxyState">The new state for the web call.</param>
        /// <param name="okToPrompt">Prompt user for credentials if they are not available.</param>
        public WebProxyState PrepareWebProxy(IWebProxy proxy, string webCallUrl, WebProxyState oldProxyState, bool okToPrompt)
        {
            WebProxyState newProxyState = WebProxyState.Abort;

            if (string.IsNullOrEmpty(webCallUrl))
            {
                Debug.Fail("PrepareWebProxy called with an empty WebCallUrl.");
                webCallUrl = "http://go.microsoft.com/fwlink/?LinkId=81947";
            }

            // Get the web proxy url for the the current web call.
            Uri webCallProxy = null;
            if (proxy != null)
            {
                webCallProxy = proxy.GetProxy(new Uri(webCallUrl));
            }

            if ((proxy != null) && (webCallProxy != null))
            {
                // get proxy url.
                string proxyUrl = webCallProxy.Host;
                if (string.IsNullOrEmpty(_currentProxyUrl))
                {
                    _currentProxyUrl = proxyUrl;
                }

                switch (oldProxyState)
                {
                    case WebProxyState.NoCredentials:
                        // Add the default credentials only if there aren't any credentials attached to
                        // the DefaultWebProxy. If the first calls attaches the correct credentials, the
                        // second call will just use them, instead of overwriting it with the default credentials.
                        // This avoids multiple web calls. Note that state is transitioned to DefaultCredentials
                        // instead of CachedCredentials. This ensures that web calls be tried with the
                        // cached credentials if the currently attached credentials don't result in successful web call.
                        if ((proxy.Credentials == null))
                        {
                            proxy.Credentials = CredentialCache.DefaultCredentials;
                        }
                        newProxyState = WebProxyState.DefaultCredentials;
                        break;

                    case WebProxyState.DefaultCredentials:
                        // Fetch cached credentials if they are null or if the proxy url has changed.
                        if ((_cachedCredentials == null) ||
                            !string.Equals(_currentProxyUrl, proxyUrl, StringComparison.OrdinalIgnoreCase))
                        {
                            _cachedCredentials = GetCachedCredentials(proxyUrl);
                        }

                        if (_cachedCredentials != null)
                        {
                            proxy.Credentials = _cachedCredentials;
                            newProxyState = WebProxyState.CachedCredentials;
                            break;
                        }

                        // Proceed to next step if cached credentials are not available.
                        goto case WebProxyState.CachedCredentials;

                    case WebProxyState.CachedCredentials:
                    case WebProxyState.PromptForCredentials:
                        if (okToPrompt)
                        {
                            if (PromptForCredentials(proxyUrl) == CredentialPromptResult.OK)
                            {
                                proxy.Credentials = _cachedCredentials;
                                newProxyState = WebProxyState.PromptForCredentials;
                            }
                            else
                            {
                                newProxyState = WebProxyState.Abort;
                            }
                        }
                        else
                        {
                            newProxyState = WebProxyState.Abort;
                        }
                        break;

                    case WebProxyState.Abort:
                        throw new InvalidOperationException();

                    default:
                        throw new ArgumentException(string.Empty, "oldProxyState");
                }
            }
            else
            {
                // No proxy for the webCallUrl scenario.
                if (oldProxyState == WebProxyState.NoCredentials)
                {
                    // if it is the first call, change the state and let the web call proceed.
                    newProxyState = WebProxyState.DefaultCredentials;
                }
                else
                {
                    Debug.Fail("This method is called a second time when 407 occurs. A 407 shouldn't have occurred as there is no default proxy.");
                    // We dont have a good idea of the circumstances under which
                    // WebProxy might be null for a url. To be safe, for VS 2005 SP1,
                    // we will just return the abort state, instead of throwing
                    // an exception. Abort state will ensure that no further procesing
                    // occurs and we will not bring down the app.
                    // throw new InvalidOperationException();
                    newProxyState = WebProxyState.Abort;
                }
            }
            return newProxyState;
        }

        //---------------------------------------------------------------------
        // private methods
        //---------------------------------------------------------------------
        /// <summary>
        /// Retrieves credentials from the credential store.
        /// </summary>
        /// <param name="proxyUrl">The proxy url for which credentials are retrieved.</param>
        /// <returns>The credentails for the proxy.</returns>
        private static NetworkCredential GetCachedCredentials(string proxyUrl)
        {
            return Credentials.GetCachedCredentials(proxyUrl);
        }

        /// <summary>
        /// Prompt the use to provider credentials and optionally store them.
        /// </summary>
        /// <param name="proxyUrl">The server that requires credentials.</param>
        /// <returns>Returns the dialog result of the prompt dialog.</returns>
        private CredentialPromptResult PromptForCredentials(string proxyUrl)
        {
            CredentialPromptResult dialogResult = CredentialPromptResult.Cancel;
            bool prompt = true;
            while (prompt)
            {
                prompt = false;

                dialogResult = Credentials.PromptForCredentials(proxyUrl, out NetworkCredential cred);
                if (CredentialPromptResult.OK == dialogResult)
                {
                    if (cred != null)
                    {
                        _cachedCredentials = cred;
                        _currentProxyUrl = proxyUrl;
                    }
                    else
                    {
                        // Prompt again for credential as we are not able to create
                        // a NetworkCredential object from the supplied credentials.
                        prompt = true;
                    }
                }
            }

            return dialogResult;
        }

    }

    internal sealed class Credentials
    {
        /// <summary>
        /// Prompt the user for credentials.
        /// </summary>
        /// <param name="target">
        /// The credential target. It is displayed in the prompt dialog and is
        /// used for credential storage.
        /// </param>
        /// <param name="credential">The user supplied credentials.</param>
        /// <returns>
        /// CredentialPromptResult.OK = if Successfully prompted user for credentials.
        /// CredentialPromptResult.Cancel = if user cancelled the prompt dialog.
        /// </returns>
        public static CredentialPromptResult PromptForCredentials(string target, out NetworkCredential credential)
        {
            CredentialPromptResult dr;
            credential = null;

            IntPtr hwndOwner = IntPtr.Zero;
            // Show the OS credential dialog.
            dr = ShowOSCredentialDialog(target, hwndOwner, out string username, out string password);
            // Create the NetworkCredential object.
            if (dr == CredentialPromptResult.OK)
            {
                credential = CreateCredentials(username, password, target);
            }

            return dr;
        }

        /// <summary>
        /// Get the cached credentials from the credentials store.
        /// </summary>
        /// <param name="target">The credential target.</param>
        /// <returns>
        /// The cached credentials. It will return null if credentails are found
        /// in the cache.
        /// </returns>
        public static NetworkCredential GetCachedCredentials(string target)
        {
            NetworkCredential cred = null;

            // Retrieve credentials from the OS credential store.
            if (ReadOSCredentials(target, out string username, out string password))
            {
                // Create the NetworkCredential object if we successfully
                // retrieved the credentails from the OS store.
                cred = CreateCredentials(username, password, target);
            }

            return cred;

        }

        //---------------------------------------------------------------------
        // private methods
        //---------------------------------------------------------------------


        /// <summary>
        /// This function calls the OS dialog to prompt user for credential.
        /// </summary>
        /// <param name="target">
        /// The credential target. It is displayed in the prompt dialog and is
        /// used for credential storage.
        /// </param>
        /// <param name="hwdOwner">The parent for the dialog.</param>
        /// <param name="userName">The username supplied by the user.</param>
        /// <param name="password">The password supplied by the user.</param>
        /// <returns>
        /// CredentialPromptResult.OK = if Successfully prompted user for credentials.
        /// CredentialPromptResult.Cancel = if user cancelled the prompt dialog.
        /// </returns>
        private static CredentialPromptResult ShowOSCredentialDialog(string target, IntPtr hwdOwner, out string userName, out string password)
        {
            CredentialPromptResult retValue;
            userName = string.Empty;
            password = string.Empty;

            string titleFormat = Strings.CredentialDialog_TitleFormat;
            string descriptionFormat = Strings.CredentialDialog_DescriptionTextFormat;

            // Create the CREDUI_INFO structure. 
            ProxyNativeMethods.CREDUI_INFO info = new ProxyNativeMethods.CREDUI_INFO();
            info.pszCaptionText = string.Format(CultureInfo.CurrentUICulture, titleFormat, target);
            info.pszMessageText = string.Format(CultureInfo.CurrentUICulture, descriptionFormat, target);
            info.hwndParentCERParent = hwdOwner;
            info.hbmBannerCERHandle = IntPtr.Zero;
            info.cbSize = Marshal.SizeOf(info);

            // We do not use CREDUI_FLAGS_VALIDATE_USERNAME flag as it doesn't allow plain user
            // (one with no domain component). Instead we use CREDUI_FLAGS_COMPLETE_USERNAME.
            // It does some basic username validation (like doesnt allow two "\" in the user name.
            // It does adds the target to the username. For example, if user entered "foo" for
            // taget "bar.com", it will return username as "bar.com\foo". We trim out bar.com
            // while parsing the username. User can input "foo@bar.com" as workaround to provide
            // "bar.com\foo" as the username.
            // We specify CRED_TYPE_SERVER_CREDENTIAL flag as the stored credentials appear in the 
            // "Control Panel->Stored Usernames and Password". It is how IE stores and retrieve
            // credentials. By using the CRED_TYPE_SERVER_CREDENTIAL flag allows IE and VS to
            // share credentials.
            // We dont specify the CREDUI_FLAGS_EXPECT_CONFIRMATION as the VS proxy service consumers
            // dont call back into the service to confirm that the call succeeded.
            ProxyNativeMethods.CREDUI_FLAGS flags = ProxyNativeMethods.CREDUI_FLAGS.SERVER_CREDENTIAL |
                                                ProxyNativeMethods.CREDUI_FLAGS.SHOW_SAVE_CHECK_BOX |
                                                ProxyNativeMethods.CREDUI_FLAGS.COMPLETE_USERNAME |
                                                ProxyNativeMethods.CREDUI_FLAGS.EXCLUDE_CERTIFICATES;

            StringBuilder user = new StringBuilder(Convert.ToInt32(ProxyNativeMethods.CREDUI_MAX_USERNAME_LENGTH));
            StringBuilder pwd = new StringBuilder(Convert.ToInt32(ProxyNativeMethods.CREDUI_MAX_PASSWORD_LENGTH));
            int saveCredentials = 0;
            // Ensures that CredUPPromptForCredentials results in a prompt.
            int netError = ProxyNativeMethods.ERROR_LOGON_FAILURE;

            // Call the OS API to prompt for credentials.
            ProxyNativeMethods.CredUIReturnCodes result = ProxyNativeMethods.CredUIPromptForCredentials(
                info,
                target,
                IntPtr.Zero,
                netError,
                user,
                ProxyNativeMethods.CREDUI_MAX_USERNAME_LENGTH,
                pwd,
                ProxyNativeMethods.CREDUI_MAX_PASSWORD_LENGTH,
                ref saveCredentials,
                flags);


            if (result == ProxyNativeMethods.CredUIReturnCodes.NO_ERROR)
            {
                userName = user.ToString();
                password = pwd.ToString();

                try
                {
                    if (Convert.ToBoolean(saveCredentials))
                    {
                        // Try reading the credentials back to ensure that we can read the stored credentials. If
                        // the CredUIPromptForCredentials() function is not able successfully call CredGetTargetInfo(),
                        // it will store credentials with credential type as DOMAIN_PASSWORD. For DOMAIN_PASSWORD
                        // credential type we can only retrive the user name. As a workaround, we store the credentials
                        // as credential type as GENERIC.
                        bool successfullyReadCredentials = ReadOSCredentials(target, out string storedUserName, out string storedPassword);
                        if (!successfullyReadCredentials ||
                            !string.Equals(userName, storedUserName, StringComparison.Ordinal) ||
                            !string.Equals(password, storedPassword, StringComparison.Ordinal))
                        {
                            // We are not able to retrieve the credentials. Try storing them as GENERIC credetails.

                            // Create the NativeCredential object.
                            ProxyNativeMethods.NativeCredential customCredential = new ProxyNativeMethods.NativeCredential();
                            customCredential.userName = userName;
                            customCredential.type = ProxyNativeMethods.CRED_TYPE_GENERIC;
                            customCredential.targetName = CreateCustomTarget(target);
                            // Store credentials across sessions.
                            customCredential.persist = (uint)ProxyNativeMethods.CRED_PERSIST.LOCAL_MACHINE;
                            if (!string.IsNullOrEmpty(password))
                            {
                                customCredential.credentialBlobSize = (uint)Marshal.SystemDefaultCharSize * ((uint)password.Length);
                                customCredential.credentialBlob = Marshal.StringToCoTaskMemAuto(password);
                            }

                            try
                            {
                                ProxyNativeMethods.CredWrite(ref customCredential, 0);
                            }
                            finally
                            {
                                if (customCredential.credentialBlob != IntPtr.Zero)
                                {
                                    Marshal.FreeCoTaskMem(customCredential.credentialBlob);
                                }

                            }
                        }
                    }
                }
                catch
                {
                    // Ignore that failure to read back the credentials. We still have
                    // username and password to use in the current session.
                }

                retValue = CredentialPromptResult.OK;
            }
            else if (result == ProxyNativeMethods.CredUIReturnCodes.ERROR_CANCELLED)
            {
                retValue = CredentialPromptResult.Cancel;
            }
            else
            {
                Debug.Fail("CredUIPromptForCredentials failed with result = " + result.ToString());
                retValue = CredentialPromptResult.Cancel;
            }

            info.Dispose();
            return retValue;
        }

        /// <summary>
        /// Generates a NetworkCredential object from username and password. The function will
        /// parse username part and invoke the correct NetworkCredential construction.
        /// </summary>
        /// <param name="username">username retrieved from user/registry.</param>
        /// <param name="password">password retrieved from user/registry.</param>
        /// <returns></returns>
        private static NetworkCredential CreateCredentials(string username, string password, string targetServer)
        {
            NetworkCredential cred = null;

            if ((!string.IsNullOrEmpty(username)) && (!string.IsNullOrEmpty(password)))
            {
                if (ParseUsername(username, targetServer, out string user, out string domain))
                {
                    if (string.IsNullOrEmpty(domain))
                    {
                        cred = new NetworkCredential(user, password);
                    }
                    else
                    {
                        cred = new NetworkCredential(user, password, domain);
                    }
                }
            }

            return cred;
        }

        /// <summary>
        /// This fuction calls CredUIParseUserName() to parse the user name.
        /// </summary>
        /// <param name="username">The username name to pass.</param>
        /// <param name="targetServer">The target for which username is being parsed.</param>
        /// <param name="user">The user part of the username.</param>
        /// <param name="domain">The domain part of the username.</param>
        /// <returns>Returns true if it successfully parsed the username.</returns>
        private static bool ParseUsername(string username, string targetServer, out string user, out string domain)
        {
            user = string.Empty;
            domain = string.Empty;

            if (string.IsNullOrEmpty(username))
            {
                return false;
            }

            bool successfullyParsed;

            StringBuilder strUser = new StringBuilder(Convert.ToInt32(ProxyNativeMethods.CREDUI_MAX_USERNAME_LENGTH));
            StringBuilder strDomain = new StringBuilder(Convert.ToInt32(ProxyNativeMethods.CREDUI_MAX_DOMAIN_TARGET_LENGTH));
            // Call the OS API to do the parsing.
            ProxyNativeMethods.CredUIReturnCodes result = ProxyNativeMethods.CredUIParseUserName(username,
                                                    strUser,
                                                    ProxyNativeMethods.CREDUI_MAX_USERNAME_LENGTH,
                                                    strDomain,
                                                    ProxyNativeMethods.CREDUI_MAX_DOMAIN_TARGET_LENGTH);

            successfullyParsed = (result == ProxyNativeMethods.CredUIReturnCodes.NO_ERROR);

            if (successfullyParsed)
            {
                user = strUser.ToString();
                domain = strDomain.ToString();

                // Remove the domain part if domain is same as target. This is to workaround
                // the COMPLETE_USERNAME flag which add the target to the user name as the 
                // domain component.
                // Read comments in ShowOSCredentialDialog() for more details.
                if (!string.IsNullOrEmpty(domain) &&
                    string.Equals(domain, targetServer, StringComparison.OrdinalIgnoreCase))
                {
                    domain = string.Empty;
                }
            }

            return successfullyParsed;
        }

        /// <summary>
        /// Retrieves credentials from the OS store.
        /// </summary>
        /// <param name="target">The credential target.</param>
        /// <param name="username">The retrieved username.</param>
        /// <param name="password">The retrieved password.</param>
        /// <returns>Returns true if it successfully reads the OS credentials.</returns>
        private static bool ReadOSCredentials(string target, out string username, out string password)
        {
            username = string.Empty;
            password = string.Empty;

            if (string.IsNullOrEmpty(target))
            {
                return false;
            }

            IntPtr credPtr = IntPtr.Zero;
            IntPtr customCredPtr = IntPtr.Zero;

            try
            {
                bool queriedDomainPassword = false;
                bool readCredentials = true;

                // Query the OS credential store.
                if (!ProxyNativeMethods.CredRead(
                        target,
                        ProxyNativeMethods.CRED_TYPE_GENERIC,
                        0,
                        out credPtr))
                {
                    readCredentials = false;

                    // Query for the DOMAIN_PASSWORD credential type to retrieve the 
                    // credentials. CredUPromptForCredentials will store credentials
                    // as DOMAIN_PASSWORD credential type if it is not able to resolve
                    // the target using CredGetTargetInfo() function.
                    if (Marshal.GetLastWin32Error() == ProxyNativeMethods.ERROR_NOT_FOUND)
                    {
                        queriedDomainPassword = true;
                        // try queryiing for CRED_TYPE_DOMAIN_PASSWORD
                        if (ProxyNativeMethods.CredRead(
                            target,
                            ProxyNativeMethods.CRED_TYPE_DOMAIN_PASSWORD,
                            0,
                            out credPtr))
                        {
                            readCredentials = true;
                        }
                    }
                }

                if (readCredentials)
                {
                    // Get the native credentials if CredRead succeeds.
                    ProxyNativeMethods.NativeCredential nativeCredential = (ProxyNativeMethods.NativeCredential)Marshal.PtrToStructure(credPtr, typeof(ProxyNativeMethods.NativeCredential));
                    password = (nativeCredential.credentialBlob != IntPtr.Zero) ?
                                            Marshal.PtrToStringUni(nativeCredential.credentialBlob, (int)(nativeCredential.credentialBlobSize / Marshal.SystemDefaultCharSize))
                                            : string.Empty;

                    username = nativeCredential.userName;

                    // If we retrieved the username using the credentials type as DOMAIN_PASSWORD, and 
                    // we are not able to retrieve password, we query the GENERIC credentials to
                    // retrieve the password. Read comments in the ShowOSCredentialDialog() funtion
                    // for more details.
                    if (string.IsNullOrEmpty(password) && queriedDomainPassword)
                    {
                        // Read custom credentials.
                        if (ProxyNativeMethods.CredRead(
                                CreateCustomTarget(target),
                                ProxyNativeMethods.CRED_TYPE_GENERIC,
                                0,
                                out customCredPtr))
                        {
                            ProxyNativeMethods.NativeCredential customNativeCredential = (ProxyNativeMethods.NativeCredential)Marshal.PtrToStructure(customCredPtr, typeof(ProxyNativeMethods.NativeCredential));
                            if (string.Equals(username, customNativeCredential.userName, StringComparison.OrdinalIgnoreCase))
                            {
                                password = (customNativeCredential.credentialBlob != IntPtr.Zero) ?
                                                        Marshal.PtrToStringUni(customNativeCredential.credentialBlob, (int)(customNativeCredential.credentialBlobSize / Marshal.SystemDefaultCharSize))
                                                        : string.Empty;
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
                username = string.Empty;
                password = string.Empty;
            }
            finally
            {
                if (credPtr != IntPtr.Zero)
                {
                    ProxyNativeMethods.CredFree(credPtr);
                }

                if (customCredPtr != IntPtr.Zero)
                {
                    ProxyNativeMethods.CredFree(customCredPtr);
                }
            }

            bool successfullyReadCredentials = true;

            if (string.IsNullOrEmpty(username) ||
                string.IsNullOrEmpty(password))
            {
                username = string.Empty;
                password = string.Empty;
                successfullyReadCredentials = false;
            }

            return successfullyReadCredentials;
        }

        /// <summary>
        /// Generates the generic target name.
        /// </summary>
        /// <param name="target">The credetial target.</param>
        /// <returns>The generic target.</returns>
        private static string CreateCustomTarget(string target)
        {
            if (string.IsNullOrEmpty(target))
            {
                return string.Empty;
            }

            return "Credentials_" + target;
        }

    }

    #region NativeMethods

    static class ProxyNativeMethods
    {
        private const string advapi32Dll = "advapi32.dll";
        private const string credUIDll = "credui.dll";

        public const int
        ERROR_INVALID_FLAGS = 1004,  // Invalid flags.
        ERROR_NOT_FOUND = 1168,  // Element not found.
        ERROR_NO_SUCH_LOGON_SESSION = 1312,  // A specified logon session does not exist. It may already have been terminated.
        ERROR_LOGON_FAILURE = 1326;  // Logon failure: unknown user name or bad password.

        [Flags]
        public enum CREDUI_FLAGS : uint
        {
            INCORRECT_PASSWORD = 0x1,
            DO_NOT_PERSIST = 0x2,
            REQUEST_ADMINISTRATOR = 0x4,
            EXCLUDE_CERTIFICATES = 0x8,
            REQUIRE_CERTIFICATE = 0x10,
            SHOW_SAVE_CHECK_BOX = 0x40,
            ALWAYS_SHOW_UI = 0x80,
            REQUIRE_SMARTCARD = 0x100,
            PASSWORD_ONLY_OK = 0x200,
            VALIDATE_USERNAME = 0x400,
            COMPLETE_USERNAME = 0x800,
            PERSIST = 0x1000,
            SERVER_CREDENTIAL = 0x4000,
            EXPECT_CONFIRMATION = 0x20000,
            GENERIC_CREDENTIALS = 0x40000,
            USERNAME_TARGET_CREDENTIALS = 0x80000,
            KEEP_USERNAME = 0x100000,
        }

        [StructLayout(LayoutKind.Sequential)]
        public class CREDUI_INFO : IDisposable
        {
            public int cbSize;
            public IntPtr hwndParentCERParent;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pszMessageText;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pszCaptionText;
            public IntPtr hbmBannerCERHandle;

            public void Dispose()
            {
                Dispose(true);
                GC.SuppressFinalize(this);
            }

            protected virtual void Dispose(bool disposing)
            {

                // Free the unmanaged resource ...
                hwndParentCERParent = IntPtr.Zero;
                hbmBannerCERHandle = IntPtr.Zero;

            }

            ~CREDUI_INFO()
            {
                Dispose(false);
            }
        }

        public enum CredUIReturnCodes : uint
        {
            NO_ERROR = 0,
            ERROR_CANCELLED = 1223,
            ERROR_NO_SUCH_LOGON_SESSION = 1312,
            ERROR_NOT_FOUND = 1168,
            ERROR_INVALID_ACCOUNT_NAME = 1315,
            ERROR_INSUFFICIENT_BUFFER = 122,
            ERROR_INVALID_PARAMETER = 87,
            ERROR_INVALID_FLAGS = 1004,
        }

        // Copied from wincred.h
        public const uint
        // Values of the Credential Type field.
        CRED_TYPE_GENERIC = 1,
        CRED_TYPE_DOMAIN_PASSWORD = 2,
        CRED_TYPE_DOMAIN_CERTIFICATE = 3,
        CRED_TYPE_DOMAIN_VISIBLE_PASSWORD = 4,
        CRED_TYPE_MAXIMUM = 5,                           // Maximum supported cred type
        CRED_TYPE_MAXIMUM_EX = (CRED_TYPE_MAXIMUM + 1000),    // Allow new applications to run on old OSes

        // String limits
        CRED_MAX_CREDENTIAL_BLOB_SIZE = 512,         // Maximum size of the CredBlob field (in bytes)
        CRED_MAX_STRING_LENGTH = 256,         // Maximum length of the various credential string fields (in characters)
        CRED_MAX_USERNAME_LENGTH = (256 + 1 + 256), // Maximum length of the UserName field.  The worst case is <User>@<DnsDomain>
        CRED_MAX_GENERIC_TARGET_NAME_LENGTH = 32767,       // Maximum length of the TargetName field for CRED_TYPE_GENERIC (in characters)
        CRED_MAX_DOMAIN_TARGET_NAME_LENGTH = (256 + 1 + 80),  // Maximum length of the TargetName field for CRED_TYPE_DOMAIN_* (in characters). Largest one is <DfsRoot>\<DfsShare>
        CRED_MAX_VALUE_SIZE = 256,         // Maximum size of the Credential Attribute Value field (in bytes)
        CRED_MAX_ATTRIBUTES = 64,          // Maximum number of attributes per credential
        CREDUI_MAX_MESSAGE_LENGTH = 32767,
        CREDUI_MAX_CAPTION_LENGTH = 128,
        CREDUI_MAX_GENERIC_TARGET_LENGTH = CRED_MAX_GENERIC_TARGET_NAME_LENGTH,
        CREDUI_MAX_DOMAIN_TARGET_LENGTH = CRED_MAX_DOMAIN_TARGET_NAME_LENGTH,
        CREDUI_MAX_USERNAME_LENGTH = CRED_MAX_USERNAME_LENGTH,
        CREDUI_MAX_PASSWORD_LENGTH = (CRED_MAX_CREDENTIAL_BLOB_SIZE / 2);

        internal enum CRED_PERSIST : uint
        {
            NONE = 0,
            SESSION = 1,
            LOCAL_MACHINE = 2,
            ENTERPRISE = 3,
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct NativeCredential
        {
            public uint flags;
            public uint type;
            public string targetName;
            public string comment;
            public int lastWritten_lowDateTime;
            public int lastWritten_highDateTime;
            public uint credentialBlobSize;
            public IntPtr credentialBlob;
            public uint persist;
            public uint attributeCount;
            public IntPtr attributes;
            public string targetAlias;
            public string userName;
        };

        [DllImport(advapi32Dll, CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredReadW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool
        CredRead(
            [MarshalAs(UnmanagedType.LPWStr)]
            string targetName,
            [MarshalAs(UnmanagedType.U4)]
            uint type,
            [MarshalAs(UnmanagedType.U4)]
            uint flags,
            out IntPtr credential
            );

        [DllImport(advapi32Dll, CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredWriteW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool
        CredWrite(
            ref NativeCredential Credential,
            [MarshalAs(UnmanagedType.U4)]
            uint flags
            );

        [DllImport(advapi32Dll)]
        public static extern void
        CredFree(
            IntPtr buffer
            );

        [DllImport(credUIDll, EntryPoint = "CredUIPromptForCredentialsW", CharSet = CharSet.Unicode)]
        public static extern CredUIReturnCodes CredUIPromptForCredentials(
            CREDUI_INFO pUiInfo,  // Optional (one can pass null here)
            [MarshalAs(UnmanagedType.LPWStr)]
            string targetName,
            IntPtr Reserved,      // Must be 0 (IntPtr.Zero)
            int iError,
            [MarshalAs(UnmanagedType.LPWStr)]
            StringBuilder pszUserName,
            [MarshalAs(UnmanagedType.U4)]
            uint ulUserNameMaxChars,
            [MarshalAs(UnmanagedType.LPWStr)]
            StringBuilder pszPassword,
            [MarshalAs(UnmanagedType.U4)]
            uint ulPasswordMaxChars,
            ref int pfSave,
            CREDUI_FLAGS dwFlags);

        /// <returns>
        /// Win32 system errors:
        /// NO_ERROR
        /// ERROR_INVALID_ACCOUNT_NAME
        /// ERROR_INSUFFICIENT_BUFFER
        /// ERROR_INVALID_PARAMETER
        /// </returns>
        [DllImport(credUIDll, CharSet = CharSet.Auto, SetLastError = true, EntryPoint = "CredUIParseUserNameW")]
        public static extern CredUIReturnCodes CredUIParseUserName(
            [MarshalAs(UnmanagedType.LPWStr)]
            string strUserName,
            [MarshalAs(UnmanagedType.LPWStr)]
            StringBuilder strUser,
            [MarshalAs(UnmanagedType.U4)]
            uint iUserMaxChars,
            [MarshalAs(UnmanagedType.LPWStr)]
            StringBuilder strDomain,
            [MarshalAs(UnmanagedType.U4)]
            uint iDomainMaxChars
            );
    }

    #endregion
}
