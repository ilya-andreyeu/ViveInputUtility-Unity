﻿//========= Copyright 2016-2017, HTC Corporation. All rights reserved. ===========

using HTC.UnityPlugin.Utility;
using HTC.UnityPlugin.VRModuleManagement;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering;

namespace HTC.UnityPlugin.Vive
{
    [InitializeOnLoad]
    public class VIUVersionCheck : EditorWindow
    {
        [Serializable]
        private struct RepoInfo
        {
            public string tag_name;
            public string body;
        }

        private interface IPropSetting
        {
            bool SkipCheck();
            void UpdateCurrentValue();
            bool IsIgnored();
            bool IsUsingRecommendedValue();
            void DoDrawRecommend();
            void AcceptRecommendValue();
            void DoIgnore();
            void DeleteIgnore();
        }

        public class RecommendedSetting<T> : IPropSetting
        {
            private const string fmtTitle = "{0} (current = {1})";
            private const string fmtRecommendBtn = "Use recommended ({0})";
            private const string fmtRecommendBtnWithPosefix = "Use recommended ({0}) - {1}";

            private string m_settingTitle;
            private string m_settingTrimedTitle;
            private string ignoreKey { get { return m_settingTrimedTitle; } }

            public string settingTitle { get { return m_settingTitle; } set { m_settingTitle = value; m_settingTrimedTitle = value.Replace(" ", ""); } }
            public string recommendBtnPostfix = string.Empty;
            public string toolTip = string.Empty;
            public Func<bool> skipCheckFunc = null;
            public Func<T> recommendedValueFunc = null;
            public Func<T> currentValueFunc = null;
            public Action<T> setValueFunc = null;
            public T currentValue = default(T);
            public T recommendedValue = default(T);

            public T GetRecommended() { return recommendedValueFunc == null ? recommendedValue : recommendedValueFunc(); }

            public bool SkipCheck() { return skipCheckFunc == null ? false : skipCheckFunc(); }

            public bool IsIgnored() { return VIUProjectSettings.HasIgnoreKey(ignoreKey); }

            public bool IsUsingRecommendedValue() { return EqualityComparer<T>.Default.Equals(currentValue, GetRecommended()); }

            public void UpdateCurrentValue() { currentValue = currentValueFunc(); }

            public void DoDrawRecommend()
            {
                GUILayout.Label(new GUIContent(string.Format(fmtTitle, settingTitle, currentValue), toolTip));

                GUILayout.BeginHorizontal();

                bool recommendBtnClicked;
                if (string.IsNullOrEmpty(recommendBtnPostfix))
                {
                    recommendBtnClicked = GUILayout.Button(new GUIContent(string.Format(fmtRecommendBtn, GetRecommended()), toolTip));
                }
                else
                {
                    recommendBtnClicked = GUILayout.Button(new GUIContent(string.Format(fmtRecommendBtnWithPosefix, GetRecommended(), recommendBtnPostfix), toolTip));
                }

                if (recommendBtnClicked)
                {
                    AcceptRecommendValue();
                }

                GUILayout.FlexibleSpace();

                if (GUILayout.Button(new GUIContent("Ignore", toolTip)))
                {
                    DoIgnore();
                }

                GUILayout.EndHorizontal();
            }

            public void AcceptRecommendValue()
            {
                setValueFunc(GetRecommended());
            }

            public void DoIgnore()
            {
                VIUProjectSettings.AddIgnoreKey(ignoreKey);
            }

            public void DeleteIgnore()
            {
                VIUProjectSettings.RemoveIgnoreKey(ignoreKey);
            }
        }

        public const string lastestVersionUrl = "https://api.github.com/repos/ViveSoftware/ViveInputUtility-Unity/releases/latest";
        public const string pluginUrl = "https://github.com/ViveSoftware/ViveInputUtility-Unity/releases";
        public const double versionCheckIntervalMinutes = 30.0;

        private const string nextVersionCheckTimeKey = "ViveInputUtility.LastVersionCheckTime";
        private const string fmtIgnoreUpdateKey = "DoNotShowUpdate.v{0}";
        private static string ignoreThisVersionKey;

        private static bool completeCheckVersionFlow = false;
        private static WWW www;
        private static RepoInfo latestRepoInfo;
        private static Version latestVersion;
        private static Vector2 releaseNoteScrollPosition;
        private static Vector2 settingScrollPosition;
        private static bool showNewVersion;

        private static bool toggleSkipThisVersion = false;

        private static List<IPropSetting> s_settings;
        private Texture2D viuLogo;

        static VIUVersionCheck()
        {
            EditorApplication.update += CheckVersionAndSettings;
        }

        public static void AddRecommendedSetting<T>(RecommendedSetting<T> setting)
        {
            InitializeSettins();
            s_settings.Add(setting);
        }

        private static void InitializeSettins()
        {
            if (s_settings != null) { return; }

            s_settings = new List<IPropSetting>();

            s_settings.Add(new RecommendedSetting<bool>()
            {
                settingTitle = "Binding Interface Switch",
                skipCheckFunc = () => !VIUSettingsEditor.supportOpenVR,
                toolTip = VIUSettings.BIND_UI_SWITCH_TOOLTIP + " You can change this option later in Edit -> Preferences... -> VIU Settings.",
                currentValueFunc = () => VIUSettings.enableBindingInterfaceSwitch,
                setValueFunc = v => { VIUSettings.enableBindingInterfaceSwitch = v; },
                recommendedValue = true,
            });

            s_settings.Add(new RecommendedSetting<bool>()
            {
                settingTitle = "External Camera Switch",
                skipCheckFunc = () => !VRModule.isSteamVRPluginDetected || !VIUSettingsEditor.supportOpenVR,
                toolTip = VIUSettings.EX_CAM_UI_SWITCH_TOOLTIP + " You can change this option later in Edit -> Preferences... -> VIU Settings.",
                currentValueFunc = () => VIUSettings.enableExternalCameraSwitch,
                setValueFunc = v => { VIUSettings.enableExternalCameraSwitch = v; },
                recommendedValue = true,
            });

#if UNITY_5_3
            s_settings.Add(new RecommendedSetting<bool>()
            {
                settingTitle = "Stereoscopic Rendering",
                skipCheckFunc = () => VRModule.isSteamVRPluginDetected || !VIUSettingsEditor.supportAnyVR,
                currentValueFunc = () => PlayerSettings.stereoscopic3D,
                setValueFunc = v => PlayerSettings.stereoscopic3D = v,
                recommendedValue = false,
            });
#endif

#if UNITY_5_3 || UNITY_5_4
            s_settings.Add(new RecommendedSetting<RenderingPath>()
            {
                settingTitle = "Rendering Path",
                skipCheckFunc = () => VRModule.isSteamVRPluginDetected || !VIUSettingsEditor.supportAnyVR,
                recommendBtnPostfix = "required for MSAA",
                currentValueFunc = () => PlayerSettings.renderingPath,
                setValueFunc = v => PlayerSettings.renderingPath = v,
                recommendedValue = RenderingPath.Forward,
            });

            s_settings.Add(new RecommendedSetting<bool>()
            {
                settingTitle = "Show Unity Splash Screen",
                skipCheckFunc = () => VRModule.isSteamVRPluginDetected || !InternalEditorUtility.HasPro(),
                currentValueFunc = () => PlayerSettings.showUnitySplashScreen,
                setValueFunc = v => PlayerSettings.showUnitySplashScreen = v,
                recommendedValue = false,
            });
#endif

            s_settings.Add(new RecommendedSetting<bool>()
            {
                settingTitle = "GPU Skinning",
                skipCheckFunc = () => VRModule.isSteamVRPluginDetected || !VIUSettingsEditor.supportAnyVR,
                currentValueFunc = () => PlayerSettings.gpuSkinning,
                setValueFunc = v => PlayerSettings.gpuSkinning = v,
                recommendedValue = true,
            });

            s_settings.Add(new RecommendedSetting<bool>()
            {
                settingTitle = "Default Is Fullscreen",
                skipCheckFunc = () => VRModule.isSteamVRPluginDetected || !VIUSettingsEditor.supportAnyStandaloneVR,
                currentValueFunc = () => PlayerSettings.defaultIsFullScreen,
                setValueFunc = v => PlayerSettings.defaultIsFullScreen = v,
                recommendedValue = false,
            });

            s_settings.Add(new RecommendedSetting<Vector2>()
            {
                settingTitle = "Default Screen Size",
                skipCheckFunc = () => VRModule.isSteamVRPluginDetected || !VIUSettingsEditor.supportAnyStandaloneVR,
                currentValueFunc = () => new Vector2(PlayerSettings.defaultScreenWidth, PlayerSettings.defaultScreenHeight),
                setValueFunc = v => { PlayerSettings.defaultScreenWidth = (int)v.x; PlayerSettings.defaultScreenHeight = (int)v.y; },
                recommendedValue = new Vector2(1024f, 768f),
            });

            s_settings.Add(new RecommendedSetting<bool>()
            {
                settingTitle = "Run In Background",
                skipCheckFunc = () => VRModule.isSteamVRPluginDetected || !VIUSettingsEditor.supportAnyStandaloneVR,
                currentValueFunc = () => PlayerSettings.runInBackground,
                setValueFunc = v => PlayerSettings.runInBackground = v,
                recommendedValue = true,
            });

            s_settings.Add(new RecommendedSetting<ResolutionDialogSetting>()
            {
                settingTitle = "Display Resolution Dialog",
                skipCheckFunc = () => VRModule.isSteamVRPluginDetected || !VIUSettingsEditor.supportAnyStandaloneVR,
                currentValueFunc = () => PlayerSettings.displayResolutionDialog,
                setValueFunc = v => PlayerSettings.displayResolutionDialog = v,
                recommendedValue = ResolutionDialogSetting.HiddenByDefault,
            });

            s_settings.Add(new RecommendedSetting<bool>()
            {
                settingTitle = "Resizable Window",
                skipCheckFunc = () => VRModule.isSteamVRPluginDetected || !VIUSettingsEditor.supportAnyStandaloneVR,
                currentValueFunc = () => PlayerSettings.resizableWindow,
                setValueFunc = v => PlayerSettings.resizableWindow = v,
                recommendedValue = true,
            });

            s_settings.Add(new RecommendedSetting<D3D11FullscreenMode>()
            {
                settingTitle = "D3D11 Fullscreen Mode",
                skipCheckFunc = () => VRModule.isSteamVRPluginDetected || !VIUSettingsEditor.supportAnyStandaloneVR,
                currentValueFunc = () => PlayerSettings.d3d11FullscreenMode,
                setValueFunc = v => PlayerSettings.d3d11FullscreenMode = v,
                recommendedValue = D3D11FullscreenMode.FullscreenWindow,
            });

            s_settings.Add(new RecommendedSetting<bool>()
            {
                settingTitle = "Visible In Background",
                skipCheckFunc = () => VRModule.isSteamVRPluginDetected || !VIUSettingsEditor.supportAnyStandaloneVR,
                currentValueFunc = () => PlayerSettings.visibleInBackground,
                setValueFunc = v => PlayerSettings.visibleInBackground = v,
                recommendedValue = true,
            });

            s_settings.Add(new RecommendedSetting<ColorSpace>()
            {
                settingTitle = "Color Space",
                skipCheckFunc = () => !VIUSettingsEditor.supportAnyVR,
                recommendBtnPostfix = "requires reloading scene",
                currentValueFunc = () => PlayerSettings.colorSpace,
                setValueFunc = v => PlayerSettings.colorSpace = v,
                recommendedValue = ColorSpace.Linear,
            });

            s_settings.Add(new RecommendedSetting<bool>()
            {
                settingTitle = "ColorSpace.Linear supported Graphics API",
                skipCheckFunc = () => !VIUSettingsEditor.supportAnyAndroidVR || PlayerSettings.colorSpace == ColorSpace.Gamma,
                currentValueFunc = () =>
                {
                    if (PlayerSettings.colorSpace != ColorSpace.Linear) { return false; }
                    if (PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.Android) == true) { return false; }

                    var apiList = ListPool<GraphicsDeviceType>.Get();
                    apiList.AddRange(PlayerSettings.GetGraphicsAPIs(BuildTarget.Android));
                    var result = !apiList.Contains(GraphicsDeviceType.OpenGLES2) && apiList.Contains(GraphicsDeviceType.OpenGLES3)
#if UNITY_5_5_OR_NEWER
                        && !apiList.Contains(GraphicsDeviceType.Vulkan)
#endif
                        ;
                    ListPool<GraphicsDeviceType>.Release(apiList);
                    return result;
                },
                setValueFunc = v =>
                {
                    PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
                    var listChanged = false;
                    var apiList = ListPool<GraphicsDeviceType>.Get();
                    apiList.AddRange(PlayerSettings.GetGraphicsAPIs(BuildTarget.Android));
                    if (!apiList.Contains(GraphicsDeviceType.OpenGLES3)) { apiList.Add(GraphicsDeviceType.OpenGLES3); listChanged = true; }
#if UNITY_5_5_OR_NEWER
                    // FIXME: Daydream SDK currently not support Vulkan API
                    if (apiList.Remove(GraphicsDeviceType.Vulkan)) { listChanged = true; }
#endif
                    if (apiList.Remove(GraphicsDeviceType.OpenGLES2)) { listChanged = true; }
                    if (listChanged) { PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, apiList.ToArray()); }
                    ListPool<GraphicsDeviceType>.Release(apiList);
                },
                recommendedValue = true,
            });

            s_settings.Add(new RecommendedSetting<bool>()
            {
                settingTitle = "Vive Support",
                skipCheckFunc = () => VRModule.isSteamVRPluginDetected || !VIUSettingsEditor.canSupportOpenVR,
                currentValueFunc = () => VIUSettingsEditor.supportOpenVR,
                setValueFunc = v => VIUSettingsEditor.supportOpenVR = v,
                recommendedValue = true,
            });

            s_settings.Add(new RecommendedSetting<bool>()
            {
                settingTitle = "Oculus Support",
                skipCheckFunc = () => !VIUSettingsEditor.canSupportOculus,
                currentValueFunc = () => VIUSettingsEditor.supportOculus,
                setValueFunc = v => VIUSettingsEditor.supportOculus = v,
                recommendedValue = true,
            });

            s_settings.Add(new RecommendedSetting<bool>()
            {
                settingTitle = "Daydream Support",
                skipCheckFunc = () => !VIUSettingsEditor.canSupportDaydream,
                currentValueFunc = () => VIUSettingsEditor.supportDaydream,
                setValueFunc = v => VIUSettingsEditor.supportDaydream = v,
                recommendedValue = true,
            });
        }

        // check vive input utility version on github
        private static void CheckVersionAndSettings()
        {
            if (Application.isPlaying)
            {
                EditorApplication.update -= CheckVersionAndSettings;
                return;
            }

            InitializeSettins();

            // fetch new version info from github release site
            if (!completeCheckVersionFlow)
            {
                if (www == null) // web request not running
                {
                    if (EditorPrefs.HasKey(nextVersionCheckTimeKey) && DateTime.UtcNow < UtcDateTimeFromStr(EditorPrefs.GetString(nextVersionCheckTimeKey)))
                    {
                        completeCheckVersionFlow = true;
                        return;
                    }

                    www = new WWW(lastestVersionUrl);
                }

                if (!www.isDone)
                {
                    return;
                }

                if (UrlSuccess(www))
                {
                    // On Windows, PlaterSetting is stored at \HKEY_CURRENT_USER\Software\Unity Technologies\Unity Editor 5.x
                    EditorPrefs.SetString(nextVersionCheckTimeKey, UtcDateTimeToStr(DateTime.UtcNow.AddMinutes(versionCheckIntervalMinutes)));

                    latestRepoInfo = JsonUtility.FromJson<RepoInfo>(www.text);
                }

                // parse latestVersion and ignoreThisVersionKey
                if (!string.IsNullOrEmpty(latestRepoInfo.tag_name))
                {
                    try
                    {
                        latestVersion = new Version(Regex.Replace(latestRepoInfo.tag_name, "[^0-9\\.]", string.Empty));
                        ignoreThisVersionKey = string.Format(fmtIgnoreUpdateKey, latestVersion.ToString());
                    }
                    catch
                    {
                        latestVersion = default(Version);
                        ignoreThisVersionKey = string.Empty;
                    }
                }

                www.Dispose();
                www = null;

                completeCheckVersionFlow = true;
            }

            showNewVersion = !string.IsNullOrEmpty(ignoreThisVersionKey) && !VIUProjectSettings.HasIgnoreKey(ignoreThisVersionKey) && latestVersion > VIUVersion.current;

            // check if their is setting that not using recommended value and not ignored
            var recommendCount = 0; // not ignored and not using recommended value
            foreach (var setting in s_settings)
            {
                if (setting.SkipCheck()) { continue; }

                setting.UpdateCurrentValue();

                if (!setting.IsIgnored() && !setting.IsUsingRecommendedValue())
                {
                    ++recommendCount;
                }
            }

            if (showNewVersion || recommendCount > 0)
            {
                var window = GetWindow<VIUVersionCheck>(true, "Vive Input Utility");
                window.minSize = new Vector2(240f, 550f);

                var rect = window.position;
                window.position = new Rect(Mathf.Max(rect.x, 50f), Mathf.Max(rect.y, 50f), rect.width, 200f + (showNewVersion ? 700f : 400f));
            }

            EditorApplication.update -= CheckVersionAndSettings;
        }

        private static DateTime UtcDateTimeFromStr(string str)
        {
            var utcTicks = default(long);
            if (string.IsNullOrEmpty(str) || !long.TryParse(str, out utcTicks)) { return DateTime.MinValue; }
            return new DateTime(utcTicks, DateTimeKind.Utc);
        }

        private static string UtcDateTimeToStr(DateTime utcDateTime)
        {
            return utcDateTime.Ticks.ToString();
        }

        private static bool UrlSuccess(WWW www)
        {
            if (!string.IsNullOrEmpty(www.error))
            {
                // API rate limit exceeded, see https://developer.github.com/v3/#rate-limiting
                Debug.Log("url:" + www.url);
                Debug.Log("error:" + www.error);
                Debug.Log(www.text);

                string responseHeader;
                if (www.responseHeaders.TryGetValue("X-RateLimit-Limit", out responseHeader))
                {
                    Debug.Log("X-RateLimit-Limit:" + responseHeader);
                }
                if (www.responseHeaders.TryGetValue("X-RateLimit-Remaining", out responseHeader))
                {
                    Debug.Log("X-RateLimit-Remaining:" + responseHeader);
                }
                if (www.responseHeaders.TryGetValue("X-RateLimit-Reset", out responseHeader))
                {
                    Debug.Log("X-RateLimit-Reset:" + TimeZone.CurrentTimeZone.ToLocalTime(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(double.Parse(responseHeader))).ToString());
                }

                return false;
            }

            if (Regex.IsMatch(www.text, "404 not found", RegexOptions.IgnoreCase))
            {
                Debug.Log("url:" + www.url);
                Debug.Log("error:" + www.error);
                Debug.Log(www.text);
                return false;
            }

            return true;
        }

        private string GetResourcePath()
        {
            var ms = MonoScript.FromScriptableObject(this);
            var path = AssetDatabase.GetAssetPath(ms);
            path = Path.GetDirectoryName(path);
            return path.Substring(0, path.Length - "Scripts/Editor".Length) + "Textures/";
        }

        public void OnGUI()
        {
            if (viuLogo == null)
            {
                var currentDir = Path.GetDirectoryName(AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(this)));
                var texturePath = currentDir.Substring(0, currentDir.Length - "Scripts/Editor".Length) + "Textures/VIU_logo.png";
                viuLogo = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
            }

            if (viuLogo != null)
            {
                GUI.DrawTexture(GUILayoutUtility.GetRect(position.width, 124, GUI.skin.box), viuLogo, ScaleMode.ScaleToFit);
            }

            if (showNewVersion)
            {
                EditorGUILayout.HelpBox("New version available:", MessageType.Warning);

                GUILayout.Label("Current version: " + VIUVersion.current);
                GUILayout.Label("New version: " + latestVersion);

                if (!string.IsNullOrEmpty(latestRepoInfo.body))
                {
                    GUILayout.Label("Release notes:");
                    releaseNoteScrollPosition = GUILayout.BeginScrollView(releaseNoteScrollPosition, GUILayout.Height(250f));
                    EditorGUILayout.HelpBox(latestRepoInfo.body, MessageType.None);
                    GUILayout.EndScrollView();
                }

                GUILayout.BeginHorizontal();
                {
                    if (GUILayout.Button(new GUIContent("Get Latest Version", "Goto " + pluginUrl)))
                    {
                        Application.OpenURL(pluginUrl);
                    }

                    GUILayout.FlexibleSpace();

                    toggleSkipThisVersion = GUILayout.Toggle(toggleSkipThisVersion, "Do not prompt for this version again.");
                }
                GUILayout.EndHorizontal();
            }

            var notRecommendedCount = 0;
            var ignoredCount = 0; // ignored and not using recommended value
            var drawCount = 0; // not ignored and not using recommended value

            InitializeSettins();
            foreach (var setting in s_settings)
            {
                if (setting.SkipCheck()) { continue; }

                setting.UpdateCurrentValue();

                var isIgnored = setting.IsIgnored();
                if (isIgnored) { ++ignoredCount; }

                if (setting.IsUsingRecommendedValue()) { continue; }
                else { ++notRecommendedCount; }

                if (!isIgnored)
                {
                    if (drawCount == 0)
                    {
                        EditorGUILayout.HelpBox("Recommended project settings:", MessageType.Warning);

                        settingScrollPosition = GUILayout.BeginScrollView(settingScrollPosition, GUILayout.ExpandHeight(true));
                    }

                    ++drawCount;
                    setting.DoDrawRecommend();
                }
            }

            if (drawCount > 0)
            {
                GUILayout.EndScrollView();

                if (ignoredCount > 0)
                {
                    if (GUILayout.Button("Clear All Ignores(" + ignoredCount + ")"))
                    {
                        foreach (var setting in s_settings) { setting.DeleteIgnore(); }
                    }
                }

                GUILayout.BeginHorizontal();
                {
                    if (GUILayout.Button("Accept All(" + drawCount + ")"))
                    {
                        foreach (var setting in s_settings) { if (!setting.SkipCheck() && !setting.IsIgnored()) { setting.AcceptRecommendValue(); } }
                    }

                    if (GUILayout.Button("Ignore All(" + drawCount + ")"))
                    {
                        foreach (var setting in s_settings) { if (!setting.SkipCheck() && !setting.IsIgnored() && !setting.IsUsingRecommendedValue()) { setting.DoIgnore(); } }
                    }
                }
                GUILayout.EndHorizontal();
            }
            else if (notRecommendedCount > 0)
            {
                EditorGUILayout.HelpBox("Some recommended settings ignored.", MessageType.Warning);

                GUILayout.FlexibleSpace();

                if (GUILayout.Button("Clear All Ignores(" + ignoredCount + ")"))
                {
                    foreach (var setting in s_settings) { setting.DeleteIgnore(); }
                }
            }
            else
            {
                EditorGUILayout.HelpBox("All recommended settings applied.", MessageType.Info);

                GUILayout.FlexibleSpace();
            }

            VIUSettingsEditor.EnabledDevices.ApplyChanges();

            if (VIUProjectSettings.hasChanged)
            {
                // save ignore keys
                VIUProjectSettings.Save();
            }

            if (GUILayout.Button("Close"))
            {
                Close();
            }
        }

        private void OnDestroy()
        {
            if (viuLogo != null)
            {
                viuLogo = null;
            }

            if (showNewVersion && toggleSkipThisVersion && !string.IsNullOrEmpty(ignoreThisVersionKey))
            {
                VIUProjectSettings.AddIgnoreKey(ignoreThisVersionKey);
            }
        }
    }
}