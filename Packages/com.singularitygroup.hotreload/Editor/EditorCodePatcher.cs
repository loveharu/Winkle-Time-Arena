using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using SingularityGroup.HotReload.DTO;
using SingularityGroup.HotReload.Editor.Cli;
using SingularityGroup.HotReload.EditorDependencies;
using SingularityGroup.HotReload.RuntimeDependencies;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Task = System.Threading.Tasks.Task;
using System.Reflection;
using UnityEditor.Compilation;


namespace SingularityGroup.HotReload.Editor {
    [InitializeOnLoad]
    static class EditorCodePatcher {
        const string sessionFilePath = PackageConst.LibraryCachePath + "/sessionId.txt";
        const string patchesFilePath = PackageConst.LibraryCachePath + "/patches.json";
        
        internal static readonly ServerDownloader serverDownloader;
        internal static bool _compileError;
        
        static Timer timer; 
        static bool init;

        internal static UnityLicenseType licenseType { get; private set; }
        internal static bool LoginNotRequired => PackageConst.IsAssetStoreBuild && licenseType != UnityLicenseType.UnityPro;
        internal static IReadOnlyList<string> Failures { get; set; } = new List<string>();
        internal static bool compileError => _compileError;
        
        internal static PatchStatus patchStatus = PatchStatus.None;

        static bool quitting;
        static EditorCodePatcher() {
            if(init) {
                //Avoid infinite recursion in case the static constructor gets accessed via `InitPatchesBlocked` below
                return;
            }
            init = true;
            UnityHelper.Init();
            //Use synchonization context if possible because it's more reliable.
            ThreadUtility.InitEditor();
            if (!EditorWindowHelper.IsHumanControllingUs()) {
                return;
            }
            
            serverDownloader = new ServerDownloader();
            timer = new Timer(OnIntervalThreaded, (Action) OnIntervalMainThread, 500, 500);

            UpdateHost();
            licenseType = UnityLicenseHelper.GetLicenseType();
            var compileChecker = CompileChecker.Create();
            compileChecker.onCompilationFinished += OnCompilationFinished;
            EditorApplication.delayCall += InstallUtility.CheckForNewInstall;
            AddEditorFocusChangedHandler(OnEditorFocusChanged);
            // When domain reloads, this is a good time to ensure server has up-to-date project information
            if (ServerHealthCheck.I.IsServerHealthy) {
                EditorApplication.delayCall += TryPrepareBuildInfo;
            }
            // reset in case last session didn't shut down properly
            CheckEditorSettings();
            EditorApplication.quitting += ResetSettingsOnQuit;
            CompilationPipeline.compilationFinished += obj => {
                // reset in case package got removed
                // if it got removed, it will not be enabled again
                // if it wasn't removed, settings will get handled by OnIntervalMainThread
                AutoRefreshSettingChecker.Reset();
                ScriptCompilationSettingChecker.Reset();
            };
            DetectEditorStart();
            DetectVersionUpdate();
            //SingularityGroup.HotReload.Demo.Demo.I = new EditorDemo();
            RecordActiveDaysForRateApp();
            if(EditorApplication.isPlayingOrWillChangePlaymode) {
                CodePatcher.I.InitPatchesBlocked(patchesFilePath);
            }

#pragma warning disable CS0612 // Type or member is obsolete
            if (HotReloadPrefs.RateAppShownLegacy) {
                HotReloadPrefs.RateAppShown = true;
            }
            if (!File.Exists(HotReloadPrefs.showOnStartupPath)) {
                var showOnStartupLegacy = HotReloadPrefs.GetShowOnStartupEnum();
                HotReloadPrefs.ShowOnStartup = showOnStartupLegacy;
            }
#pragma warning restore CS0612 // Type or member is obsolete
        }

        public static void ResetSettingsOnQuit() {
            quitting = true;
            AutoRefreshSettingChecker.Reset();
            ScriptCompilationSettingChecker.Reset();
        }

        public static bool autoRecompileUnsupportedChangesSupported;
        static void AddEditorFocusChangedHandler(Action<bool> handler) {
            var eventInfo = typeof(EditorApplication).GetEvent("focusChanged", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            var addMethod = eventInfo?.GetAddMethod(true) ?? eventInfo?.GetAddMethod(false);
            if (addMethod != null) {
                addMethod.Invoke(null, new object[]{ handler });
            }
            autoRecompileUnsupportedChangesSupported = addMethod != null;
        }

        private static void OnEditorFocusChanged(bool hasFocus) {
            if (hasFocus && !HotReloadPrefs.AutoRecompileUnsupportedChangesImmediately) {
                TryRecompileUnsupportedChanges();
            }
        }

        private static void TryRecompileUnsupportedChanges() {
            if (!HotReloadPrefs.AutoRecompileUnsupportedChanges 
                || Failures.Count == 0 
                || _compileError 
                || EditorApplication.isPlaying && !HotReloadPrefs.AutoRecompileUnsupportedChangesInPlayMode
            ) {
                return;
            }
            if (EditorApplication.isPlaying) {
                EditorApplication.isPlaying = false;
            }
            if (EditorWindow.focusedWindow) {
                EditorWindow.focusedWindow.ShowNotification(new GUIContent("[Hot Reload] Unsupported Changes Detected! Recompiling..."));
            }
            AssetDatabase.Refresh();
        }

        private static DateTime lastPrepareBuildInfo = DateTime.UtcNow;

        /// Post state for player builds.
        /// Only check build target because user can change build settings whenever.
        internal static void TryPrepareBuildInfo() {
            // Note: we post files state even when build target is wrong
            // because you might connect with a build downloaded onto the device. 
            if ((DateTime.UtcNow - lastPrepareBuildInfo).TotalSeconds > 5) {
                lastPrepareBuildInfo = DateTime.UtcNow;
                HotReloadCli.PrepareBuildInfoAsync().Forget();
            }
        }

        internal static void RecordActiveDaysForRateApp() {
            var unixDay = (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 86400);
            var activeDays = GetActiveDaysForRateApp();
            if (activeDays.Count < Constants.DaysToRateApp && activeDays.Add(unixDay.ToString())) {
                HotReloadPrefs.ActiveDays = string.Join(",", activeDays);
            }
        }
        
        internal static HashSet<string> GetActiveDaysForRateApp() {
            if (string.IsNullOrEmpty(HotReloadPrefs.ActiveDays)) {
                return new HashSet<string>();
            }
            return new HashSet<string>(HotReloadPrefs.ActiveDays.Split(','));
        }

        // CheckEditorStart distinguishes between domain reload and first editor open
        // We have some separate logic on editor start (InstallUtility.HandleEditorStart)
        private static void DetectEditorStart() {
            var editorId = EditorAnalyticsSessionInfo.id;
            var currVersion = PackageConst.Version;
            Task.Run(() => {
                try {
                    var lines = File.Exists(sessionFilePath) ? File.ReadAllLines(sessionFilePath) : Array.Empty<string>();

                    long prevSessionId = -1;
                    string prevVersion = null;
                    if (lines.Length >= 2) {
                        long.TryParse(lines[1], out prevSessionId);
                    }
                    if (lines.Length >= 3) {
                        prevVersion = lines[2].Trim();
                    }
                    var updatedFromVersion = (prevSessionId != -1 && currVersion != prevVersion) ? prevVersion : null;

                    if (prevSessionId != editorId && prevSessionId != 0) {
                        // back to mainthread
                        ThreadUtility.RunOnMainThread(() => {
                            InstallUtility.HandleEditorStart(updatedFromVersion);

                            var newEditorId = EditorAnalyticsSessionInfo.id;
                            if (newEditorId != 0) {
                                Task.Run(() => {
                                    try {
                                        // editorId isn't available on first domain reload, must do it here
                                        File.WriteAllLines(sessionFilePath, new[] {
                                            "1", // serialization version
                                            newEditorId.ToString(),
                                            currVersion,
                                        });

                                    } catch (IOException) {
                                        // ignore
                                    }
                                });
                            }
                        });
                    }

                } catch (IOException) {
                    // ignore
                } catch (Exception e) {
                    ThreadUtility.LogException(e);
                }
            });
        }
        
        private static void DetectVersionUpdate() {
            if (serverDownloader.CheckIfDownloaded(HotReloadCli.controller)) {
                return;
            }
            ServerHealthCheck.instance.CheckHealth();
            if (!ServerHealthCheck.I.IsServerHealthy) {
                return;
            }
            var restartServer = EditorUtility.DisplayDialog("Hot Reload",
                $"When updating Hot Reload, the server must be restarted for the update to take effect." +
                "\nDo you want to restart it now?",
                "Restart server", "Don't restart");
            if (restartServer) {
                EditorCodePatcher.RestartCodePatcher().Forget();
            }
        }

        private static void UpdateHost() {
            string host;
            if (HotReloadPrefs.RemoteServer) {
                host = HotReloadPrefs.RemoteServerHost;
                RequestHelper.ChangeAssemblySearchPaths(Array.Empty<string>());
            } else {
                host = "127.0.0.1";
            }
            var rootPath = Path.GetFullPath(".");
            RequestHelper.SetServerInfo(new PatchServerInfo(host, null, rootPath, HotReloadPrefs.RemoteServer));
        }

        static void OnIntervalThreaded(object o) {
            ServerHealthCheck.instance.CheckHealth();
            ThreadUtility.RunOnMainThread((Action)o);
            if (serverDownloader.Progress >= 1f) {
                serverDownloader.CheckIfDownloaded(HotReloadCli.controller);
            }
        }
        
        internal static bool firstPatchAttempted;
        static void OnIntervalMainThread() {
            if(ServerHealthCheck.I.IsServerHealthy) {
                TryPrepareBuildInfo();
                RequestHelper.PollMethodPatches(resp => HandleResponseReceived(resp));
                RequestHelper.PollPatchStatus(resp => {
                    patchStatus = resp.patchStatus;
                    if (patchStatus == PatchStatus.Compiling) {
                        startWaitingForCompile = null;
                    }
                    if (patchStatus == PatchStatus.Patching) {
                        firstPatchAttempted = true;
                    }
                }, patchStatus);
                if (HotReloadPrefs.AllAssetChanges) {
                    RequestHelper.PollAssetChanges(HandleAssetChange);
                }
            }
            if (!ServerHealthCheck.I.IsServerHealthy) {
                stopping = false;
            }
            if (startupProgress?.Item1 == 1) {
                starting = false;
            }
            CheckEditorSettings();
        }

        static void CheckEditorSettings() {
            if (quitting) {
                return;
            }
            CheckAutoRefresh();
            CheckScriptCompilation();
        }

        static void CheckAutoRefresh() {
            if (HotReloadPrefs.AllowDisableUnityAutoRefresh && ServerHealthCheck.I.IsServerHealthy) {
                AutoRefreshSettingChecker.Apply();
                AutoRefreshSettingChecker.Check();
            } else {
                AutoRefreshSettingChecker.Reset();
            }
        }
        
        static void CheckScriptCompilation() {
            if (HotReloadPrefs.AllowDisableUnityAutoRefresh && ServerHealthCheck.I.IsServerHealthy) {
                ScriptCompilationSettingChecker.Apply();
                ScriptCompilationSettingChecker.Check();
            } else {
                ScriptCompilationSettingChecker.Reset();
            }
        }

        static string[] assetExtensionBlacklist = new[] {
            ".cs",
            // TODO add setting to allow scenes to get hot reloaded for users who collaborate (their scenes change externally)
            ".unity",
            // safer to ignore meta files completely until there's a use-case
            ".meta",
            // debug files
            ".mdb",
            ".pdb",
        };

        public static string[] compileFiles = new[] {
            ".asmdef",
            ".asmref",
            ".rsp",
        };

        public static string[] plugins = new[] {
            // native plugins
            ".dll",
            ".bundle",
            ".dylib",
            ".so",
            // plugin scripts
            ".cpp",
            ".h",
            ".aar",
            ".jar",
            ".a",
            ".java"
        };
        
        static void HandleAssetChange(string assetPath) {
            // ignore directories
            if (Directory.Exists(assetPath)) {
                return;
            }
            foreach (var compileFile in compileFiles) {
                if (assetPath.EndsWith(compileFile, StringComparison.Ordinal)) {
                    Failures = new List<string>(Failures) { $"errors: AssemblyFileEdit: Editing assembly files requires recompiling in Unity. in {assetPath}" };
                    if (HotReloadPrefs.AutoRecompileUnsupportedChangesImmediately || UnityEditorInternal.InternalEditorUtility.isApplicationActive) {
                        TryRecompileUnsupportedChanges();
                    }
                    return;
                }
            }
            // Add plugin changes to unsupported changes list
            foreach (var plugin in plugins) {
                if (assetPath.EndsWith(plugin, StringComparison.Ordinal)) {
                    Failures = new List<string>(Failures) { $"errors: NativePluginEdit: Editing native plugins requires recompiling in Unity. in {assetPath}" };
                    if (HotReloadPrefs.AutoRecompileUnsupportedChangesImmediately || UnityEditorInternal.InternalEditorUtility.isApplicationActive) {
                        TryRecompileUnsupportedChanges();
                    }
                    return;
                }
            }
            // ignore file extensiosn that trigger domain reload
            foreach (var blacklisted in assetExtensionBlacklist) {
                if (assetPath.EndsWith(blacklisted, StringComparison.Ordinal)) {
                    return;
                }
            }
            var relativePath = GetRelativePath(assetPath, Path.GetFullPath("Assets"));
            var relativePathPackages = GetRelativePath(assetPath, Path.GetFullPath("Packages"));
            // ignore files outside assets and packages folders
            if (relativePath.StartsWith("..", StringComparison.Ordinal) 
                && relativePathPackages.StartsWith("..", StringComparison.Ordinal)
            ) {
                return;
            }
            try {
                if (!File.Exists(assetPath)) {
                    AssetDatabase.DeleteAsset(relativePath);
                } else {
                    AssetDatabase.ImportAsset(relativePath, ImportAssetOptions.ForceUpdate);
                }
            } catch (Exception e){
                Log.Warning($"Refreshing asset at path: {assetPath} failed due to exception: {e}");
            }
        }

        public static string GetRelativePath(string filespec, string folder) {
            Uri pathUri = new Uri(filespec);
            Uri folderUri = new Uri(folder);
            return Uri.UnescapeDataString(folderUri.MakeRelativeUri(pathUri).ToString().Replace('/', Path.DirectorySeparatorChar));
        }
        
        static void HandleResponseReceived(MethodPatchResponse response) {
            if (response.patches.Length > 0) {
                LogBurstHint(response);
                var errors = CodePatcher.I.RegisterPatches(response, persist: true);
                if (errors?.Count > 0) {
                    var newFailures = new List<string>(Failures);
                    newFailures.AddRange(errors);
                    Failures = newFailures;
                }
                CodePatcher.I.SaveAppliedPatches(patchesFilePath).Forget();
                var window = HotReloadWindow.Current;
                if(window) {
                    window.Repaint();
                }
            }
            if (response.failures.Length > 0) {
                _compileError = response.failures.Any(failure=> failure.Contains("error CS"));
                var newFailures = new List<string>(Failures);
                foreach (var failure in response.failures) {
                    if (!failure.Contains("error CS")) {
                        // move failure to the top of the list if already in list
                        if (newFailures.Contains(failure)) {
                            newFailures.Remove(failure);
                        }
                        newFailures.Add(failure);
                    }
                }
                Failures = newFailures;
                
                if (HotReloadPrefs.AutoRecompileUnsupportedChangesImmediately || UnityEditorInternal.InternalEditorUtility.isApplicationActive) {
                    TryRecompileUnsupportedChanges();
                }
            } else {
                _compileError = false;
            }
            HandleRemovedUnityMethods(response.removedMethod);
        }

        static void HandleRemovedUnityMethods(SMethod[] removedMethod) {
            foreach(var sMethod in removedMethod) {
                try {
                    var candidates = CodePatcher.I.SymbolResolver.Resolve(sMethod.assemblyName.Replace(".dll", ""));
                    var asm = candidates[0];
                    var module = asm.GetLoadedModules()[0];
                    var oldMethod = module.ResolveMethod(sMethod.metadataToken);
                    UnityEventHelper.RemoveUnityEventMethod(oldMethod);
                } catch(Exception ex) {
                    Log.Warning("Encountered exception in RemoveUnityEventMethod: {0} {1}", ex.GetType().Name, ex.Message);
                }
            }
        }
        
        [Conditional("UNITY_2022_2_OR_NEWER")]
        static void LogBurstHint(MethodPatchResponse response) {
            if(HotReloadPrefs.LoggedBurstHint) {
                return;
            }
            foreach (var patch in response.patches) {
                if(patch.unityJobs.Length > 0) {
                    Debug.LogWarning("A unity job was hot reloaded. " +
                                     "This will cause a harmless warning that can be ignored. " +
                                     $"More info about this can be found here: {Constants.TroubleshootingURL}");
                    HotReloadPrefs.LoggedBurstHint = true;
                    break;
                }
            }
        }

        private static DateTime? startWaitingForCompile;
        static void OnCompilationFinished() {
            ServerHealthCheck.instance.CheckHealth();
            if (Failures.Count > 0) {
                Failures = new List<string>();
            }
            if(ServerHealthCheck.I.IsServerHealthy) {
                startWaitingForCompile = DateTime.UtcNow;
                firstPatchAttempted = false;
                RequestCompile().Forget();
            }
            Task.Run(() => File.Delete(patchesFilePath));
        }
        
        static async Task RequestCompile() {
            await RequestHelper.RequestClearPatches();
            await ProjectGeneration.ProjectGeneration.GenerateSlnAndCsprojFiles(Application.dataPath);
            await RequestHelper.RequestCompile();
        }
        
        private static bool stopping;
        private static bool starting;
        private static DateTime? startupCompletedAt;
        private static Tuple<float, string> startupProgress;
        
        internal static bool Started => ServerHealthCheck.I.IsServerHealthy && DownloadProgress == 1 && StartupProgress?.Item1 == 1;
        internal static bool Starting => (StartedServerRecently() || ServerHealthCheck.I.IsServerHealthy) && !Started && starting;
        internal static bool Stopping => stopping && Running;
        internal static bool Compiling => DateTime.UtcNow - startWaitingForCompile < TimeSpan.FromSeconds(5) || patchStatus == PatchStatus.Compiling;
        internal static Tuple<float, string> StartupProgress => startupProgress;
        
        
        /// <summary>
        /// We have a button to stop the Hot Reload server.<br/>
        /// Store task to ensure only one stop attempt at a time. 
        /// </summary>
        private static DateTime? serverStartedAt;
        private static DateTime? serverStoppedAt;
        private static DateTime? serverRestartedAt;
        private static bool StartedServerRecently() {
            return DateTime.UtcNow - serverStartedAt < ServerHealthCheck.HeartBeatTimeout;
        }
        
        internal static bool StoppedServerRecently() {
            return DateTime.UtcNow - serverStoppedAt < ServerHealthCheck.HeartBeatTimeout || (!StartedServerRecently() && (startupProgress?.Item1 ?? 0) == 0);
        }
        
        internal static bool RestartedServerRecently() {
            return DateTime.UtcNow - serverRestartedAt < ServerHealthCheck.HeartBeatTimeout;
        }

        private static bool requestingStart;
        private static async Task StartCodePatcher(LoginData loginData = null) {
            if (requestingStart || StartedServerRecently())  {
                return;
            }
            stopping = false;
            starting = true;
            var exposeToNetwork = HotReloadPrefs.ExposeServerToLocalNetwork;
            var allAssetChanges = HotReloadPrefs.AllAssetChanges;
            var disableConsoleWindow = HotReloadPrefs.DisableConsoleWindow;
            CodePatcher.I.ClearPatchedMethods();
            try {
                requestingStart = true;
                startupProgress = Tuple.Create(0f, "Starting Hot Reload");
                serverStartedAt = DateTime.UtcNow;
                await HotReloadCli.StartAsync(exposeToNetwork, allAssetChanges, disableConsoleWindow, loginData).ConfigureAwait(false);
                firstPatchAttempted = false;
            }
            catch (Exception ex) {
                ThreadUtility.LogException(ex);
            }
            finally {
                requestingStart = false;
            }
        }
        
        private static bool requestingStop;
        internal static async Task StopCodePatcher() {
            stopping = true;
            starting = false;
            if (requestingStop) {
                return;
            }
            CodePatcher.I.ClearPatchedMethods();
            try {
                requestingStop = true;
                await HotReloadCli.StopAsync().ConfigureAwait(false);
                serverStoppedAt = DateTime.UtcNow;
                await ThreadUtility.SwitchToMainThread();
                startupProgress = null;
            }
            catch (Exception ex) {
                ThreadUtility.LogException(ex);
            }
            finally {
                requestingStop = false;
            }
        }
        
        private static bool requestingRestart;
        internal static async Task RestartCodePatcher() {
            if (requestingRestart) {
                return;
            }
            try {
                requestingRestart = true;
                await StopCodePatcher();
                await DownloadAndRun();
                serverRestartedAt = DateTime.UtcNow;
            }
            finally {
                requestingRestart = false;
            }
        }
        
        
        private static bool requestingDownloadAndRun;
        internal static float DownloadProgress => serverDownloader.Progress;
        internal static bool DownloadRequired => DownloadProgress < 1f;
        internal static bool DownloadStarted => serverDownloader.Started;
        internal static bool RequestingDownloadAndRun => requestingDownloadAndRun;
        internal static async Task<bool> DownloadAndRun(LoginData loginData = null) {
            if (requestingDownloadAndRun) {
                return false;
            }
            stopping = false;
            requestingDownloadAndRun = true;
            try {
                if (DownloadRequired) {
                    var ok = await serverDownloader.PromptForDownload();
                    if (!ok) {
                        return false;
                    }
                }
                await StartCodePatcher(loginData);
                return true;
            } finally {
                requestingDownloadAndRun = false;
            }
        }
        
        private const int SERVER_POLL_FREQUENCY_ON_STARTUP_MS = 500;
        private const int SERVER_POLL_FREQUENCY_AFTER_STARTUP_MS = 2000;
        private static int GetPollFrequency() {
            return (startupProgress != null && startupProgress.Item1 < 1) || StartedServerRecently()
                ? SERVER_POLL_FREQUENCY_ON_STARTUP_MS
                : SERVER_POLL_FREQUENCY_AFTER_STARTUP_MS;
        }
        
        internal static bool RequestingLoginInfo { get; set; }
        
        [CanBeNull] internal static LoginStatusResponse Status { get; private set; }
        internal static void HandleStatus(LoginStatusResponse resp) {
            Attribution.RegisterLogin(resp);
            
            bool consumptionsChanged = Status?.freeSessionRunning != resp.freeSessionRunning || Status?.freeSessionEndTime != resp.freeSessionEndTime;
            bool expiresAtChanged = Status?.licenseExpiresAt != resp.licenseExpiresAt;
            if (resp.consumptionsUnavailableReason == ConsumptionsUnavailableReason.UnrecoverableError
                && Status?.consumptionsUnavailableReason != ConsumptionsUnavailableReason.UnrecoverableError
            ) {
                Log.Error("Free charges unavailabe. Please contact support if the issue persists.");
            }
            if (!RequestingLoginInfo && resp.requestError == null) {
                Status = resp;
            }
            if (resp.lastLicenseError == null) {
                // If we got success, we should always show an error next time it comes up
                HotReloadPrefs.ErrorHidden = false;
            }

            var oldStartupProgress = startupProgress;
            var newStartupProgress = Tuple.Create(
                resp.startupProgress,
                string.IsNullOrEmpty(resp.startupStatus) ? "Starting Hot Reload" : resp.startupStatus);

            startupProgress = newStartupProgress;
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            if (startupCompletedAt == null && newStartupProgress.Item1 == 1f) {
                startupCompletedAt = DateTime.UtcNow;
            }
            
            if (oldStartupProgress == null
                || Math.Abs(oldStartupProgress.Item1 - newStartupProgress.Item1) > 0
                || oldStartupProgress.Item2 != newStartupProgress.Item2
                || consumptionsChanged
                || expiresAtChanged
            ) {
                // Send project files state now that server can receive requests (only needed for player builds)
                TryPrepareBuildInfo();
            }
        }
        
        internal static  async Task RequestLogin(string email, string password) {
            EditorCodePatcher.RequestingLoginInfo = true;
            try {
                int i = 0;
                while (!Running && i < 100) {
                    await Task.Delay(100);
                    i++;
                }

                Status = await RequestHelper.RequestLogin(email, password, 10);

                // set to false so new error is shown
                HotReloadPrefs.ErrorHidden = false;
                if (Status?.isLicensed == true) {
                    HotReloadPrefs.LicenseEmail = email;
                    HotReloadPrefs.LicensePassword = Status.initialPassword ?? password;
                }
            } finally {
                RequestingLoginInfo = false;
            }
        }
        private static bool requestingServerInfo;
        private static long lastServerPoll;
        private static bool running;
        internal static bool Running => ServerHealthCheck.I.IsServerHealthy;
        
        internal static void RequestServerInfo() {
            if (requestingServerInfo) {
                return;
            }
            RequestServerInfoAsync().Forget();
        }
        
        private static async Task RequestServerInfoAsync() {
            requestingServerInfo = true;
            try {
                await RequestServerInfoCore();
            } finally {
                requestingServerInfo = false;
            }
        }

        private static async Task RequestServerInfoCore() {
            var pollFrequency = GetPollFrequency();
            // Delay until we've hit the poll request frequency
            var waitMs = (int)Mathf.Clamp(pollFrequency - ((DateTime.Now.Ticks / (float)TimeSpan.TicksPerMillisecond) - lastServerPoll), 0, pollFrequency);
            await Task.Delay(waitMs);

            var oldRunning = running;

            var newRunning = ServerHealthCheck.I.IsServerHealthy;
            running = newRunning;

            if (running) {
                var resp = await RequestHelper.GetLoginStatus(30);
                HandleStatus(resp);
            } else {
                startupCompletedAt = null;
            }

            if (!running && !StartedServerRecently()) {
                // Reset startup progress
                startupProgress = null;
            }

            // Repaint if the running Status has changed since the layout changes quite a bit
            if (oldRunning != newRunning && HotReloadWindow.Current) {
                HotReloadWindow.Current.RunTab.RepaintInstant();
            }

            lastServerPoll = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;
        }
    }
}
