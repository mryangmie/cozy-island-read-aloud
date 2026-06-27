using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using BepInEx;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace CozyIslandReadAloud
{
    [BepInPlugin("com.yangmie.cozyisland.readaloud", "CozyIsland Read Aloud", "0.1.1")]
    public sealed class Plugin : BaseUnityPlugin
    {
        private static readonly Regex RichTextTagPattern = new Regex("<[^>]+>", RegexOptions.Compiled);
        private static readonly Regex WhitespacePattern = new Regex("\\s+", RegexOptions.Compiled);

        private readonly Dictionary<string, AudioClip> _clipCache = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
        private readonly Vector3[] _corners = new Vector3[4];

        private AudioSource _audioSource;
        private Canvas _canvas;
        private RectTransform _canvasRect;
        private RectTransform _buttonRect;
        private TextMeshProUGUI _buttonText;
        private string _audioDir;
        private string _currentText;
        private RectTransform _currentTextRect;
        private Vector2 _guiButtonPoint;
        private float _scanTimer;
        private float _diagnosticTimer;
        private int _lastSeenTextCount;
        private int _lastCandidateTextCount;
        private bool _isLoadingClip;
        private bool _loggedFirstUpdate;
        private bool _loggedFirstGui;

        public void Awake()
        {
            DontDestroyOnLoad(gameObject);
            _audioSource = gameObject.AddComponent<AudioSource>();
            _audioSource.playOnAwake = false;
            _audioSource.spatialBlend = 0f;

            var pluginDir = Path.GetDirectoryName(Info.Location);
            _audioDir = Path.Combine(pluginDir ?? string.Empty, "audio", "zh-CN");

            ReadAloudRunner.Configure(_audioDir, Logger);
            var runnerObject = new GameObject("CozyIslandReadAloudRunner");
            DontDestroyOnLoad(runnerObject);
            runnerObject.AddComponent<ReadAloudRunner>();
            Logger.LogInfo("ReadAloud runner object created.");

            ReadAloudPlayerLoop.Install(_audioDir, Logger);

            CreateOverlay();
            SceneManager.sceneLoaded += OnSceneLoaded;
            InvokeRepeating(nameof(PeriodicTick), 1f, 2f);
            Logger.LogInfo("CozyIsland Read Aloud loaded. Audio dir: " + _audioDir);
        }

        public void OnEnable()
        {
            Logger.LogInfo("ReadAloud OnEnable");
        }

        public void Start()
        {
            Logger.LogInfo("ReadAloud Start");
            EnsureOverlay();
        }

        public void Update()
        {
            if (!_loggedFirstUpdate)
            {
                _loggedFirstUpdate = true;
                Logger.LogInfo("ReadAloud first Update. Screen=" + Screen.width + "x" + Screen.height);
            }

            _scanTimer += Time.unscaledDeltaTime;
            if (_scanTimer >= 0.35f)
            {
                _scanTimer = 0f;
                PickCurrentText();
            }

            _diagnosticTimer += Time.unscaledDeltaTime;
            if (_diagnosticTimer >= 4f)
            {
                _diagnosticTimer = 0f;
                Logger.LogInfo("ReadAloud scan: seen=" + _lastSeenTextCount +
                               ", candidates=" + _lastCandidateTextCount +
                               ", best=\"" + (_currentText ?? string.Empty) + "\"");
            }
        }

        public void OnGUI()
        {
            if (!_loggedFirstGui)
            {
                _loggedFirstGui = true;
                Logger.LogInfo("ReadAloud first OnGUI. Screen=" + Screen.width + "x" + Screen.height);
            }

            GUI.depth = -32000;

            const float width = 96f;
            const float height = 28f;
            var x = (Screen.width - width) * 0.5f;
            var y = 48f;

            x = Mathf.Clamp(x, 20f, Screen.width - width - 20f);
            y = Mathf.Clamp(y, 20f, Screen.height - height - 20f);

            var previousColor = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.02f, 0.05f, 0.08f, 0.34f);
            var label = "G/H/J/K";
            if (GUI.Button(new Rect(x, y, width, height), label))
            {
                OnReadClicked();
            }
            GUI.backgroundColor = previousColor;
        }

        public void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Logger.LogInfo("ReadAloud scene loaded: " + scene.name);
            EnsureOverlay();
        }

        private void PeriodicTick()
        {
            Logger.LogInfo("ReadAloud periodic tick. Screen=" + Screen.width + "x" + Screen.height +
                           ", canvas=" + (_canvas != null) +
                           ", button=" + (_buttonRect != null) +
                           ", active=" + (_buttonRect != null && _buttonRect.gameObject.activeInHierarchy));
            EnsureOverlay();
        }

        private void EnsureOverlay()
        {
            if (_canvas == null || _buttonRect == null)
            {
                CreateOverlay();
            }

            SetButtonVisible(true);
            if (_buttonRect != null)
            {
                _buttonRect.sizeDelta = new Vector2(132f, 34f);
                _buttonRect.anchorMin = new Vector2(0.5f, 1f);
                _buttonRect.anchorMax = new Vector2(0.5f, 1f);
                _buttonRect.pivot = new Vector2(0.5f, 1f);
                _buttonRect.anchoredPosition = new Vector2(0f, -52f);
            }
        }

        private void CreateOverlay()
        {
            if (_canvas != null)
            {
                return;
            }

            var canvasObject = new GameObject("CozyIslandReadAloudCanvas");
            DontDestroyOnLoad(canvasObject);

            _canvas = canvasObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 32000;
            _canvasRect = canvasObject.GetComponent<RectTransform>();

            var scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            canvasObject.AddComponent<GraphicRaycaster>();

            var buttonObject = new GameObject("ReadAloudButton");
            buttonObject.transform.SetParent(canvasObject.transform, false);

            var image = buttonObject.AddComponent<Image>();
            image.color = new Color(0.02f, 0.05f, 0.08f, 0.28f);

            var button = buttonObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(OnReadClicked);

            _buttonRect = buttonObject.GetComponent<RectTransform>();
            _buttonRect.sizeDelta = new Vector2(132f, 34f);
            _buttonRect.anchorMin = new Vector2(0.5f, 1f);
            _buttonRect.anchorMax = new Vector2(0.5f, 1f);
            _buttonRect.pivot = new Vector2(0.5f, 1f);

            var labelObject = new GameObject("Label");
            labelObject.transform.SetParent(buttonObject.transform, false);
            _buttonText = labelObject.AddComponent<TextMeshProUGUI>();
            _buttonText.text = "G/H/J/K";
            _buttonText.alignment = TextAlignmentOptions.Center;
            _buttonText.fontSize = 18f;
            _buttonText.color = new Color(1f, 1f, 1f, 0.72f);
            _buttonText.raycastTarget = false;

            var labelRect = labelObject.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;

            _buttonRect.sizeDelta = new Vector2(132f, 34f);
            _buttonRect.anchoredPosition = new Vector2(0f, -52f);
            SetButtonVisible(true);
        }

        private void PickCurrentText()
        {
            TMP_Text bestTmpText = null;
            Text bestLegacyText = null;
            RectTransform bestRect = null;
            string bestCleanText = null;
            float bestScore = float.MinValue;
            var seen = 0;
            var candidates = 0;

            foreach (var text in Resources.FindObjectsOfTypeAll<TMP_Text>())
            {
                seen++;
                if (!IsUsableText(text))
                {
                    continue;
                }

                var clean = NormalizeText(text.text);
                if (ShouldIgnore(clean))
                {
                    continue;
                }

                if (!TryGetScreenPoint(text.rectTransform, out var screenPoint))
                {
                    continue;
                }

                if (screenPoint.x < -80f || screenPoint.x > Screen.width + 80f ||
                    screenPoint.y < -80f || screenPoint.y > Screen.height + 80f)
                {
                    continue;
                }

                var score = ScoreText(clean, screenPoint, text.fontSize);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestTmpText = text;
                    bestLegacyText = null;
                    bestRect = text.rectTransform;
                    bestCleanText = clean;
                }

                candidates++;
            }

            foreach (var text in Resources.FindObjectsOfTypeAll<Text>())
            {
                seen++;
                if (!IsUsableLegacyText(text))
                {
                    continue;
                }

                var clean = NormalizeText(text.text);
                if (ShouldIgnore(clean))
                {
                    continue;
                }

                var rectTransform = text.rectTransform;
                if (!TryGetScreenPoint(rectTransform, out var screenPoint))
                {
                    continue;
                }

                if (screenPoint.x < -80f || screenPoint.x > Screen.width + 80f ||
                    screenPoint.y < -80f || screenPoint.y > Screen.height + 80f)
                {
                    continue;
                }

                var score = ScoreText(clean, screenPoint, text.fontSize);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestTmpText = null;
                    bestLegacyText = text;
                    bestRect = rectTransform;
                    bestCleanText = clean;
                }

                candidates++;
            }

            _lastSeenTextCount = seen;
            _lastCandidateTextCount = candidates;
            _currentTextRect = bestRect;
            _currentText = bestCleanText;
            SetButtonVisible(bestRect != null);

            if (bestRect != null)
            {
                if (bestTmpText != null)
                {
                    BorrowFontFrom(bestTmpText);
                }
                else if (bestLegacyText != null && bestLegacyText.font != null && _buttonText != null)
                {
                    // Keep the TMP default if the legacy font cannot be assigned.
                }

                PositionButtonAtTopCenter();
            }
            else
            {
                PositionButtonAtTopCenter();
            }
        }

        private bool IsUsableText(TMP_Text text)
        {
            return text != null &&
                   text.gameObject != null &&
                   text.gameObject.scene.IsValid() &&
                   text.isActiveAndEnabled &&
                   text.gameObject.activeInHierarchy &&
                   text.rectTransform != null &&
                   (_canvas == null || !text.transform.IsChildOf(_canvas.transform));
        }

        private bool IsUsableLegacyText(Text text)
        {
            return text != null &&
                   text.gameObject != null &&
                   text.gameObject.scene.IsValid() &&
                   text.isActiveAndEnabled &&
                   text.gameObject.activeInHierarchy &&
                   text.rectTransform != null &&
                   (_canvas == null || !text.transform.IsChildOf(_canvas.transform));
        }

        private static string NormalizeText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            text = RichTextTagPattern.Replace(text, string.Empty);
            text = text.Replace("\\n", " ");
            text = text.Replace("\r", " ").Replace("\n", " ");
            text = WhitespacePattern.Replace(text, " ");
            return text.Trim();
        }

        private static bool ShouldIgnore(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return true;
            }

            if (text.Length <= 1)
            {
                return true;
            }

            var lower = text.ToLowerInvariant();
            if (lower.Contains("fps") || lower.Contains("steamid"))
            {
                return true;
            }

            if (text == "0" || text == "对话" || text == "点击继续" || text == "朗读" || text == "测试朗读" || text == "READ")
            {
                return true;
            }

            return CountChineseCharacters(text) == 0;
        }

        private static int CountChineseCharacters(string text)
        {
            var count = 0;
            foreach (var ch in text)
            {
                if (ch >= 0x4e00 && ch <= 0x9fff)
                {
                    count++;
                }
            }

            return count;
        }

        private static float ScoreText(string text, Vector2 screenPoint, float fontSize)
        {
            var chineseCount = CountChineseCharacters(text);
            var distanceFromCenter = Vector2.Distance(screenPoint, new Vector2(Screen.width * 0.5f, Screen.height * 0.5f));

            var score = chineseCount * 12f + text.Length * 2f + Mathf.Min(fontSize, 48f);
            score -= distanceFromCenter * 0.035f;

            if (chineseCount >= 8)
            {
                score += 120f;
            }

            if (text.Contains("点击") || text.Contains("对话") || text.Contains("任务") || text.Contains("配方"))
            {
                score += 20f;
            }

            return score;
        }

        private static bool TryGetScreenPoint(RectTransform rectTransform, out Vector2 screenPoint)
        {
            var canvas = rectTransform.GetComponentInParent<Canvas>();
            Camera camera = null;
            if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
            {
                camera = canvas.worldCamera != null ? canvas.worldCamera : Camera.main;
            }

            screenPoint = RectTransformUtility.WorldToScreenPoint(camera, rectTransform.position);
            return !float.IsNaN(screenPoint.x) && !float.IsNaN(screenPoint.y);
        }

        private void PositionButtonNear(RectTransform targetRect)
        {
            var canvas = targetRect.GetComponentInParent<Canvas>();
            Camera camera = null;
            if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
            {
                camera = canvas.worldCamera != null ? canvas.worldCamera : Camera.main;
            }

            targetRect.GetWorldCorners(_corners);

            var maxX = float.MinValue;
            var maxY = float.MinValue;
            for (var i = 0; i < _corners.Length; i++)
            {
                var screenPoint = RectTransformUtility.WorldToScreenPoint(camera, _corners[i]);
                maxX = Mathf.Max(maxX, screenPoint.x);
                maxY = Mathf.Max(maxY, screenPoint.y);
            }

            var desired = new Vector2(maxX + 54f, maxY - 24f);
            desired.x = Mathf.Clamp(desired.x, 72f, Screen.width - 72f);
            desired.y = Mathf.Clamp(desired.y, 72f, Screen.height - 72f);

            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRect, desired, null, out var localPoint))
            {
                _buttonRect.anchoredPosition = localPoint;
            }
        }

        private void UpdateGuiButtonPoint(RectTransform targetRect)
        {
            var canvas = targetRect.GetComponentInParent<Canvas>();
            Camera camera = null;
            if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
            {
                camera = canvas.worldCamera != null ? canvas.worldCamera : Camera.main;
            }

            targetRect.GetWorldCorners(_corners);

            var maxX = float.MinValue;
            var maxY = float.MinValue;
            for (var i = 0; i < _corners.Length; i++)
            {
                var screenPoint = RectTransformUtility.WorldToScreenPoint(camera, _corners[i]);
                maxX = Mathf.Max(maxX, screenPoint.x);
                maxY = Mathf.Max(maxY, screenPoint.y);
            }

            _guiButtonPoint = new Vector2(maxX + 12f, Screen.height - maxY + 2f);
        }

        private void PositionButtonAtTopCenter()
        {
            _guiButtonPoint = new Vector2((Screen.width - 96f) * 0.5f, 48f);
            if (_buttonRect != null)
            {
                _buttonRect.anchorMin = new Vector2(0.5f, 1f);
                _buttonRect.anchorMax = new Vector2(0.5f, 1f);
                _buttonRect.pivot = new Vector2(0.5f, 1f);
                _buttonRect.anchoredPosition = new Vector2(0f, -52f);
            }
        }

        private void BorrowFontFrom(TMP_Text source)
        {
            if (source != null && source.font != null && _buttonText != null && _buttonText.font != source.font)
            {
                _buttonText.font = source.font;
            }
        }

        private void SetButtonVisible(bool visible)
        {
            if (_buttonRect != null && _buttonRect.gameObject.activeSelf != visible)
            {
                _buttonRect.gameObject.SetActive(visible);
            }
        }

        private void OnReadClicked()
        {
            var text = string.IsNullOrWhiteSpace(_currentText) ? "告示牌" : _currentText;
            if (string.IsNullOrWhiteSpace(text))
            {
                Logger.LogInfo("No readable text selected.");
                return;
            }

            var audioPath = ResolveAudioPath(text);
            if (string.IsNullOrEmpty(audioPath) || !File.Exists(audioPath))
            {
                Logger.LogWarning("No audio file found for text: " + text);
                return;
            }

            if (!_isLoadingClip)
            {
                StartCoroutine(PlayWav(audioPath));
            }
        }

        private string ResolveAudioPath(string text)
        {
            if (text.Contains("告示牌"))
            {
                return Path.Combine(_audioDir, "test-dialog.wav");
            }

            foreach (var name in new[] { "基础工作台", "金甜菜", "甜菜", "木棍" })
            {
                if (text.Contains(name))
                {
                    return Path.Combine(_audioDir, name + ".wav");
                }
            }

            var exactPath = Path.Combine(_audioDir, SanitizeFileName(text) + ".wav");
            return File.Exists(exactPath) ? exactPath : string.Empty;
        }

        private static string SanitizeFileName(string text)
        {
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                text = text.Replace(invalid.ToString(), string.Empty);
            }

            return text.Trim();
        }

        private IEnumerator PlayWav(string audioPath)
        {
            _isLoadingClip = true;

            if (!_clipCache.TryGetValue(audioPath, out var clip) || clip == null)
            {
                var uri = new Uri(audioPath).AbsoluteUri;
                using (var request = UnityWebRequestMultimedia.GetAudioClip(uri, AudioType.WAV))
                {
                    yield return request.SendWebRequest();

                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        Logger.LogWarning("Failed to load audio: " + request.error + " path=" + audioPath);
                        _isLoadingClip = false;
                        yield break;
                    }

                    clip = DownloadHandlerAudioClip.GetContent(request);
                    _clipCache[audioPath] = clip;
                }
            }

            _audioSource.Stop();
            _audioSource.clip = clip;
            _audioSource.Play();
            Logger.LogInfo("Read aloud: " + _currentText);

            _isLoadingClip = false;
        }
    }
}
