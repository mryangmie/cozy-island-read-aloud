using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using TMPro;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.UI;

namespace CozyIslandReadAloud
{
    internal static class ReadAloudPlayerLoop
    {
        private static ManualLogSource _log;
        private static string _audioDir;
        private static string _workspaceRoot;
        private static string _windowsTtsCacheDir;
        private static string _mimoTtsCacheDir;
        private static string _windowsTtsScriptPath;
        private static bool _installed;
        private static bool _firstTickLogged;
        private static bool _overlayCreated;
        private static float _nextLogAt;
        private static AudioSource _audioSource;
        private static AudioClip _testClip;
        private static Process _ttsProcess;
        private static string _pendingAudioPath;
        private static string _pendingText;
        private static float _pendingStartedAt;
        private static readonly Queue<string> _queuedAudioPaths = new Queue<string>();
        private static float _nextQueuedAudioAt;
        private static int _lastHudExcludedCount;

        private static readonly Regex RichTextTagPattern = new Regex("<[^>]+>", RegexOptions.Compiled);
        private static readonly Regex WhitespacePattern = new Regex("\\s+", RegexOptions.Compiled);
        private static readonly Regex MaterialCountPattern = new Regex("^(.+?)[（(]\\s*(\\d+)\\s*/\\s*(\\d+)\\s*[）)]$", RegexOptions.Compiled);

        private const uint SndAsync = 0x0001;
        private const uint SndFilename = 0x00020000;
        private const int MaxSpokenCharacters = 420;

        private enum ReadTarget
        {
            Main,
            TopLeftTaskList,
            BottomLeftAchievement,
            BottomRightControlHint
        }

        [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool PlaySound(string pszSound, IntPtr hmod, uint fdwSound);

        public static void Install(string audioDir, ManualLogSource log)
        {
            _audioDir = audioDir;
            _log = log;
            ConfigureExternalPaths();

            if (_installed)
            {
                _log?.LogInfo("PlayerLoop bridge already installed.");
                return;
            }

            var loop = PlayerLoop.GetCurrentPlayerLoop();
            if (InsertIntoUpdate(ref loop))
            {
                PlayerLoop.SetPlayerLoop(loop);
                _installed = true;
                _log?.LogInfo("PlayerLoop bridge installed.");
            }
            else
            {
                _log?.LogWarning("PlayerLoop bridge install failed: Update loop not found.");
            }
        }

        private static bool InsertIntoUpdate(ref PlayerLoopSystem root)
        {
            if (root.subSystemList == null)
            {
                return false;
            }

            for (var i = 0; i < root.subSystemList.Length; i++)
            {
                if (root.subSystemList[i].type == typeof(UnityEngine.PlayerLoop.Update))
                {
                    var update = root.subSystemList[i];
                    var oldList = update.subSystemList ?? Array.Empty<PlayerLoopSystem>();
                    var newList = new PlayerLoopSystem[oldList.Length + 1];
                    Array.Copy(oldList, newList, oldList.Length);
                    newList[oldList.Length] = new PlayerLoopSystem
                    {
                        type = typeof(ReadAloudPlayerLoop),
                        updateDelegate = Tick
                    };
                    update.subSystemList = newList;
                    root.subSystemList[i] = update;
                    return true;
                }
            }

            return false;
        }

        private static void Tick()
        {
            try
            {
                if (!_firstTickLogged)
                {
                    _firstTickLogged = true;
                    _log?.LogInfo("PlayerLoop first tick. Screen=" + Screen.width + "x" + Screen.height);
                }

                if (!_overlayCreated && Screen.width > 0 && Screen.height > 0 && Time.realtimeSinceStartup > 0.5f)
                {
                    CreateOverlay();
                }

                if (Input.GetKeyDown(KeyCode.G))
                {
                    _log?.LogInfo("G key pressed, reading current screen text.");
                    ReadScreenText(ReadTarget.Main);
                }

                if (Input.GetKeyDown(KeyCode.H))
                {
                    _log?.LogInfo("H key pressed, reading top-left task list.");
                    ReadScreenText(ReadTarget.TopLeftTaskList);
                }

                if (Input.GetKeyDown(KeyCode.J))
                {
                    _log?.LogInfo("J key pressed, reading bottom-left achievement area.");
                    ReadScreenText(ReadTarget.BottomLeftAchievement);
                }

                if (Input.GetKeyDown(KeyCode.K))
                {
                    _log?.LogInfo("K key pressed, reading bottom-right control hints.");
                    ReadScreenText(ReadTarget.BottomRightControlHint);
                }

                PollWindowsTts();
                PollQueuedAudio();

                if (Time.realtimeSinceStartup >= _nextLogAt)
                {
                    _nextLogAt = Time.realtimeSinceStartup + 2f;
                    _log?.LogInfo("PlayerLoop tick. overlay=" + _overlayCreated + ", screen=" + Screen.width + "x" + Screen.height);
                }
            }
            catch (Exception ex)
            {
                _log?.LogError("PlayerLoop tick failed: " + ex);
            }
        }

        private static void CreateOverlay()
        {
            if (_overlayCreated)
            {
                return;
            }

            var canvasObject = new GameObject("CozyIslandReadAloudPlayerLoopCanvas");
            UnityEngine.Object.DontDestroyOnLoad(canvasObject);

            var canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32767;

            var scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            canvasObject.AddComponent<GraphicRaycaster>();

            var audioObject = new GameObject("CozyIslandReadAloudPlayerLoopAudio");
            UnityEngine.Object.DontDestroyOnLoad(audioObject);
            _audioSource = audioObject.AddComponent<AudioSource>();
            _audioSource.playOnAwake = false;
            _audioSource.spatialBlend = 0f;

            var buttonObject = new GameObject("ReadAloudPlayerLoopButton");
            buttonObject.transform.SetParent(canvasObject.transform, false);

            var image = buttonObject.AddComponent<Image>();
            image.color = new Color(0.02f, 0.05f, 0.08f, 0.34f);

            var button = buttonObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(() => ReadScreenText(ReadTarget.Main));

            var rect = buttonObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.sizeDelta = new Vector2(132f, 34f);
            rect.anchoredPosition = new Vector2(0f, -52f);

            var textObject = new GameObject("Text");
            textObject.transform.SetParent(buttonObject.transform, false);
            var text = textObject.AddComponent<Text>();
            text.text = "G/H/J/K";
            text.alignment = TextAnchor.MiddleCenter;
            text.fontSize = 18;
            text.color = new Color(1f, 1f, 1f, 0.76f);
            text.raycastTarget = false;
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");

            var textRect = textObject.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            _overlayCreated = true;
            _log?.LogInfo("PlayerLoop overlay created.");
        }

        private static void ReadScreenText(ReadTarget target)
        {
            try
            {
                var text = BuildCurrentReadableText(target);
                var targetName = GetTargetName(target);
                if (string.IsNullOrWhiteSpace(text))
                {
                    var fallback = targetName + "没有文字可以查看，请在有文字的时候按下播放按钮。";
                    _log?.LogWarning("No readable text found in " + targetName + ". Speaking fallback prompt.");
                    PlayTextWithWindowsTts(fallback);
                    return;
                }

                _log?.LogInfo("Readable screen text [" + targetName + "]: " + text);
                PlayTextWithWindowsTts(text);
            }
            catch (Exception ex)
            {
                _log?.LogError("ReadScreenText failed: " + ex);
            }
        }

        private static void PlayAudioFile(string path)
        {
            if (!File.Exists(path))
            {
                _log?.LogWarning("WAV not found: " + path);
                return;
            }

            PlayNativeWav(path);

            if (_audioSource == null)
            {
                return;
            }

            try
            {
                if (_testClip == null || !string.Equals(_testClip.name, Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase))
                {
                    _testClip = LoadWav(path);
                }

                if (_testClip != null)
                {
                    _audioSource.Stop();
                    _audioSource.clip = _testClip;
                    _audioSource.Play();
                }
            }
            catch (Exception ex)
            {
                _log?.LogWarning("Unity AudioSource fallback failed: " + ex.Message);
            }
        }

        private static void PlayTextWithWindowsTts(string text)
        {
            EnsureWindowsTtsCacheDir();

            if (_ttsProcess != null && !_ttsProcess.HasExited)
            {
                _log?.LogInfo("Windows TTS is already generating. Please wait. text=" + _pendingText);
                return;
            }

            var hash = ComputeTextHash(text);
            var mimoAudioPath = Path.Combine(_mimoTtsCacheDir, hash + ".wav");
            if (File.Exists(mimoAudioPath) && new FileInfo(mimoAudioPath).Length > 44)
            {
                _log?.LogInfo("MiMo TTS cache hit: " + mimoAudioPath);
                PlayAudioFile(mimoAudioPath);
                return;
            }

            if (TryPlayMimoSegments(text))
            {
                return;
            }

            var textPath = Path.Combine(_windowsTtsCacheDir, hash + ".txt");
            var audioPath = Path.Combine(_windowsTtsCacheDir, hash + ".wav");

            if (File.Exists(audioPath) && new FileInfo(audioPath).Length > 44)
            {
                _log?.LogInfo("Windows TTS cache hit: " + audioPath);
                PlayAudioFile(audioPath);
                return;
            }

            File.WriteAllText(textPath, text, Encoding.UTF8);
            StartWindowsTts(textPath, audioPath, text);
        }

        private static bool TryPlayMimoSegments(string text)
        {
            var paths = new List<string>();
            var segments = SplitTextSegments(text);
            if (segments.Count <= 1)
            {
                return false;
            }

            for (var i = 0; i < segments.Count; i++)
            {
                var segment = segments[i];
                var path = Path.Combine(_mimoTtsCacheDir, ComputeTextHash(segment) + ".wav");
                if (File.Exists(path) && new FileInfo(path).Length > 44)
                {
                    paths.Add(path);
                }
                else
                {
                    _log?.LogInfo("MiMo segment miss: " + segment);
                    return false;
                }
            }

            _queuedAudioPaths.Clear();
            for (var i = 0; i < paths.Count; i++)
            {
                _queuedAudioPaths.Enqueue(paths[i]);
            }

            _nextQueuedAudioAt = 0f;
            _log?.LogInfo("MiMo segment cache hit: " + paths.Count + " segments for text: " + text);
            PollQueuedAudio();
            return true;
        }

        private static List<string> SplitTextSegments(string text)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(text))
            {
                return result;
            }

            var parts = Regex.Split(text, "[。！？!?；;、/]+");
            for (var i = 0; i < parts.Length; i++)
            {
                var part = NormalizeText(parts[i]);
                if (!ShouldIgnore(part) && result.IndexOf(part) < 0)
                {
                    result.Add(part);
                }
            }

            return result;
        }

        private static void PollQueuedAudio()
        {
            if (_queuedAudioPaths.Count == 0 || Time.realtimeSinceStartup < _nextQueuedAudioAt)
            {
                return;
            }

            var path = _queuedAudioPaths.Dequeue();
            PlayAudioFile(path);
            _nextQueuedAudioAt = Time.realtimeSinceStartup + GetWavDurationSeconds(path) + 0.12f;
        }

        private static float GetWavDurationSeconds(string path)
        {
            try
            {
                var data = File.ReadAllBytes(path);
                if (data.Length < 44)
                {
                    return 1f;
                }

                var channels = BitConverter.ToInt16(data, 22);
                var sampleRate = BitConverter.ToInt32(data, 24);
                var bitsPerSample = BitConverter.ToInt16(data, 34);
                var dataOffset = FindChunk(data, "data");
                if (channels <= 0 || sampleRate <= 0 || bitsPerSample <= 0 || dataOffset < 0)
                {
                    return 1f;
                }

                var byteCount = BitConverter.ToInt32(data, dataOffset + 4);
                var bytesPerSecond = sampleRate * channels * bitsPerSample / 8f;
                return Mathf.Clamp(byteCount / bytesPerSecond, 0.4f, 30f);
            }
            catch
            {
                return 1f;
            }
        }

        private static void StartWindowsTts(string textPath, string audioPath, string text)
        {
            if (string.IsNullOrWhiteSpace(_windowsTtsScriptPath) || !File.Exists(_windowsTtsScriptPath))
            {
                _log?.LogWarning("Windows TTS script not found: " + _windowsTtsScriptPath);
                return;
            }

            try
            {
                var process = new Process();
                process.StartInfo.FileName = "powershell.exe";
                process.StartInfo.Arguments = "-NoProfile -ExecutionPolicy Bypass -File " +
                                              QuoteArgument(_windowsTtsScriptPath) +
                                              " -TextFile " + QuoteArgument(textPath) +
                                              " -OutputFile " + QuoteArgument(audioPath);
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;

                if (!process.Start())
                {
                    _log?.LogWarning("Windows TTS process did not start.");
                    process.Dispose();
                    return;
                }

                _ttsProcess = process;
                _pendingAudioPath = audioPath;
                _pendingText = text;
                _pendingStartedAt = Time.realtimeSinceStartup;
                _log?.LogInfo("Windows TTS started: " + audioPath);
            }
            catch (Exception ex)
            {
                _log?.LogWarning("Windows TTS start failed: " + ex);
            }
        }

        private static void PollWindowsTts()
        {
            if (_ttsProcess == null)
            {
                return;
            }

            try
            {
                if (!_ttsProcess.HasExited)
                {
                    if (Time.realtimeSinceStartup - _pendingStartedAt > 45f)
                    {
                        _log?.LogWarning("Windows TTS timed out, killing process. text=" + _pendingText);
                        _ttsProcess.Kill();
                    }

                    return;
                }

                var exitCode = _ttsProcess.ExitCode;
                var output = _ttsProcess.StandardOutput.ReadToEnd();
                var error = _ttsProcess.StandardError.ReadToEnd();
                var audioPath = _pendingAudioPath;

                _ttsProcess.Dispose();
                _ttsProcess = null;
                _pendingAudioPath = null;

                if (exitCode == 0 && File.Exists(audioPath) && new FileInfo(audioPath).Length > 44)
                {
                    _log?.LogInfo("Windows TTS finished: " + audioPath);
                    PlayAudioFile(audioPath);
                    return;
                }

                _log?.LogWarning("Windows TTS failed. exit=" + exitCode +
                                 ", output=" + TrimForLog(output) +
                                 ", error=" + TrimForLog(error));
            }
            catch (Exception ex)
            {
                _log?.LogWarning("Windows TTS polling failed: " + ex);
                try
                {
                    _ttsProcess?.Dispose();
                }
                catch
                {
                    // Ignore cleanup failures.
                }

                _ttsProcess = null;
            }
        }

        private static void PlayNativeWav(string path)
        {
            try
            {
                var ok = PlaySound(path, IntPtr.Zero, SndAsync | SndFilename);
                if (ok)
                {
                    _log?.LogInfo("Native PlaySound started: " + path);
                }
                else
                {
                    _log?.LogWarning("Native PlaySound failed. error=" + Marshal.GetLastWin32Error() + " path=" + path);
                }
            }
            catch (Exception ex)
            {
                _log?.LogWarning("Native PlaySound threw: " + ex);
            }
        }

        private static string BuildCurrentReadableText(ReadTarget target)
        {
            var candidates = new List<TextCandidate>();
            _lastHudExcludedCount = 0;

            foreach (var text in Resources.FindObjectsOfTypeAll<TMP_Text>())
            {
                if (!IsUsableText(text))
                {
                    continue;
                }

                AddCandidate(candidates, NormalizeText(text.text), text.rectTransform, text.fontSize, target);
            }

            foreach (var text in Resources.FindObjectsOfTypeAll<Text>())
            {
                if (!IsUsableLegacyText(text))
                {
                    continue;
                }

                AddCandidate(candidates, NormalizeText(text.text), text.rectTransform, text.fontSize, target);
            }

            if (candidates.Count == 0)
            {
                _log?.LogInfo("Text scan found no readable candidates for " + GetTargetName(target) +
                              ". hudExcluded=" + _lastHudExcludedCount);
                return string.Empty;
            }

            if (target == ReadTarget.Main && TryBuildWorkbenchReadableText(candidates, out var workbenchText))
            {
                _log?.LogInfo("Workbench readable text: " + workbenchText);
                return workbenchText;
            }

            candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
            var best = candidates[0];
            var selected = new List<TextCandidate>();

            if (target == ReadTarget.Main)
            {
                for (var i = 0; i < candidates.Count; i++)
                {
                    var candidate = candidates[i];
                    if (selected.Count >= 18)
                    {
                        break;
                    }

                    if (candidate.Score >= best.Score - 120f || IsNear(candidate, best))
                    {
                        selected.Add(candidate);
                    }
                }
            }
            else
            {
                for (var i = 0; i < candidates.Count && selected.Count < 24; i++)
                {
                    selected.Add(candidates[i]);
                }
            }

            selected.Sort(CompareReadingOrder);

            var result = new StringBuilder();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < selected.Count; i++)
            {
                var text = selected[i].Text;
                if (!seen.Add(text) || IsDuplicateOfExisting(seen, text))
                {
                    continue;
                }

                if (result.Length > 0 && !EndsWithSentencePunctuation(result))
                {
                    result.Append("。");
                }

                result.Append(text);
                if (result.Length >= MaxSpokenCharacters)
                {
                    break;
                }
            }

            var spoken = result.ToString();
            if (spoken.Length > MaxSpokenCharacters)
            {
                spoken = spoken.Substring(0, MaxSpokenCharacters);
            }

            _log?.LogInfo("Text scan target=" + GetTargetName(target) +
                          ", candidates=" + candidates.Count +
                          ", selected=" + selected.Count +
                          ", hudExcluded=" + _lastHudExcludedCount +
                          ", best=\"" + best.Text + "\"");
            return spoken.Trim();
        }

        private static bool TryBuildWorkbenchReadableText(List<TextCandidate> candidates, out string spoken)
        {
            spoken = string.Empty;

            if (!ContainsExactText(candidates, "材料列表"))
            {
                return false;
            }

            var categoryCount = CountExactTexts(candidates, new[] { "全部", "物体", "建筑", "装扮", "载具" });
            var hasCraftControls = ContainsExactText(candidates, "制作") || ContainsTextPart(candidates, "退出工作台");
            if (!hasCraftControls && categoryCount < 3)
            {
                return false;
            }

            var detailTexts = new List<TextCandidate>();
            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                if (IsWorkbenchDetailRegion(candidate.ScreenPoint) && !IsWorkbenchChromeText(candidate.Text))
                {
                    detailTexts.Add(candidate);
                }
            }

            if (detailTexts.Count == 0)
            {
                return false;
            }

            detailTexts.Sort(CompareReadingOrder);

            var uniqueTexts = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < detailTexts.Count; i++)
            {
                var text = detailTexts[i].Text;
                if (seen.Add(text) && !IsDuplicateOfExisting(seen, text))
                {
                    uniqueTexts.Add(text);
                }
            }

            var title = string.Empty;
            var description = string.Empty;
            var status = string.Empty;
            var materials = new List<string>();
            var inMaterials = false;

            for (var i = 0; i < uniqueTexts.Count; i++)
            {
                var text = uniqueTexts[i];
                if (text == "材料列表")
                {
                    inMaterials = true;
                    continue;
                }

                if (IsWorkbenchStatusText(text))
                {
                    status = CleanParenthesizedText(text);
                    continue;
                }

                if (inMaterials || MaterialCountPattern.IsMatch(text))
                {
                    materials.Add(FormatMaterialRequirement(text));
                    continue;
                }

                if (string.IsNullOrEmpty(title))
                {
                    title = text;
                    continue;
                }

                if (string.IsNullOrEmpty(description) && !title.Contains(text) && !text.Contains(title))
                {
                    description = text;
                }
            }

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(title))
            {
                parts.Add(title);
            }

            if (!string.IsNullOrWhiteSpace(description))
            {
                parts.Add(description);
            }

            if (materials.Count > 0)
            {
                parts.Add("材料列表");
                for (var i = 0; i < materials.Count; i++)
                {
                    parts.Add(materials[i]);
                }
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                parts.Add(status);
            }

            spoken = JoinSpokenParts(parts);
            return !string.IsNullOrWhiteSpace(title) && parts.Count >= 2;
        }

        private static bool IsWorkbenchDetailRegion(Vector2 screenPoint)
        {
            return screenPoint.x > Screen.width * 0.46f &&
                   screenPoint.x < Screen.width * 0.80f &&
                   screenPoint.y > Screen.height * 0.18f &&
                   screenPoint.y < Screen.height * 0.78f;
        }

        private static bool IsWorkbenchChromeText(string text)
        {
            return text == "全部" ||
                   text == "物体" ||
                   text == "建筑" ||
                   text == "装扮" ||
                   text == "载具" ||
                   text == "制作" ||
                   text == "退出工作台";
        }

        private static bool IsWorkbenchStatusText(string text)
        {
            return text.Contains("不足") ||
                   text.Contains("无法制作") ||
                   text.Contains("可制作");
        }

        private static string FormatMaterialRequirement(string text)
        {
            var compact = text.Replace(" ", string.Empty);
            var match = MaterialCountPattern.Match(compact);
            if (!match.Success)
            {
                return CleanParenthesizedText(text);
            }

            return match.Groups[1].Value + "，已有" + match.Groups[2].Value + "个，需要" + match.Groups[3].Value + "个";
        }

        private static string CleanParenthesizedText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            return text.Trim().Trim('(', ')', '（', '）').Trim();
        }

        private static string JoinSpokenParts(List<string> parts)
        {
            var builder = new StringBuilder();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < parts.Count; i++)
            {
                var part = CleanParenthesizedText(parts[i]);
                if (string.IsNullOrWhiteSpace(part) || !seen.Add(part))
                {
                    continue;
                }

                if (builder.Length > 0 && !EndsWithSentencePunctuation(builder))
                {
                    builder.Append("。");
                }

                builder.Append(part);
            }

            if (builder.Length > 0 && !EndsWithSentencePunctuation(builder))
            {
                builder.Append("。");
            }

            return builder.ToString();
        }

        private static bool ContainsExactText(List<TextCandidate> candidates, string text)
        {
            for (var i = 0; i < candidates.Count; i++)
            {
                if (string.Equals(candidates[i].Text, text, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsTextPart(List<TextCandidate> candidates, string text)
        {
            for (var i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].Text.IndexOf(text, StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static int CountExactTexts(List<TextCandidate> candidates, string[] texts)
        {
            var count = 0;
            for (var i = 0; i < texts.Length; i++)
            {
                if (ContainsExactText(candidates, texts[i]))
                {
                    count++;
                }
            }

            return count;
        }

        private static void AddCandidate(List<TextCandidate> candidates, string clean, RectTransform rectTransform, float fontSize, ReadTarget target)
        {
            if (ShouldIgnore(clean) || rectTransform == null)
            {
                return;
            }

            if (!TryGetScreenPoint(rectTransform, out var screenPoint))
            {
                return;
            }

            if (screenPoint.x < -80f || screenPoint.x > Screen.width + 80f ||
                screenPoint.y < -80f || screenPoint.y > Screen.height + 80f)
            {
                return;
            }

            if (!IsPointInTargetRegion(screenPoint, target))
            {
                if (target == ReadTarget.Main && IsExcludedHudRegion(screenPoint))
                {
                    _lastHudExcludedCount++;
                }

                return;
            }

            var chineseCount = CountChineseCharacters(clean);
            candidates.Add(new TextCandidate
            {
                Text = clean,
                ScreenPoint = screenPoint,
                FontSize = fontSize,
                ChineseCount = chineseCount,
                Score = ScoreText(clean, screenPoint, fontSize, chineseCount)
            });
        }

        private static bool IsUsableText(TMP_Text text)
        {
            return text != null &&
                   text.gameObject != null &&
                   text.gameObject.scene.IsValid() &&
                   text.isActiveAndEnabled &&
                   text.gameObject.activeInHierarchy &&
                   text.rectTransform != null &&
                   !IsReadAloudObject(text.transform);
        }

        private static bool IsUsableLegacyText(Text text)
        {
            return text != null &&
                   text.gameObject != null &&
                   text.gameObject.scene.IsValid() &&
                   text.isActiveAndEnabled &&
                   text.gameObject.activeInHierarchy &&
                   text.rectTransform != null &&
                   !IsReadAloudObject(text.transform);
        }

        private static bool IsReadAloudObject(Transform transform)
        {
            while (transform != null)
            {
                var name = transform.name;
                if (!string.IsNullOrEmpty(name) && name.IndexOf("ReadAloud", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                transform = transform.parent;
            }

            return false;
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
            if (lower.Contains("fps") || lower.Contains("steamid") || lower.Contains("read") || lower.Contains("g read"))
            {
                return true;
            }

            if (text == "0" || text == "对话" || text == "点击继续" || text == "朗读" || text == "测试朗读")
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

        private static float ScoreText(string text, Vector2 screenPoint, float fontSize, int chineseCount)
        {
            var distanceFromCenter = Vector2.Distance(screenPoint, new Vector2(Screen.width * 0.5f, Screen.height * 0.5f));
            var score = chineseCount * 12f + Math.Min(text.Length, 40) * 2f + Mathf.Min(fontSize, 52f);
            score -= distanceFromCenter * 0.025f;

            if (text.Length >= 8)
            {
                score += 70f;
            }

            if (text.Contains("制作") || text.Contains("配方") || text.Contains("任务") ||
                text.Contains("需要") || text.Contains("材料") || text.Contains("对话") ||
                text.Contains("告示") || text.Contains("工作台"))
            {
                score += 45f;
            }

            if (screenPoint.x > Screen.width * 0.18f && screenPoint.x < Screen.width * 0.82f &&
                screenPoint.y > Screen.height * 0.12f && screenPoint.y < Screen.height * 0.88f)
            {
                score += 30f;
            }

            if (text.Contains("点击") || text.Contains("继续"))
            {
                score -= 35f;
            }

            return score;
        }

        private static bool IsExcludedHudRegion(Vector2 screenPoint)
        {
            var leftTopTaskList = screenPoint.x < Screen.width * 0.30f &&
                                  screenPoint.y > Screen.height * 0.68f;
            var leftBottomAchievement = screenPoint.x < Screen.width * 0.36f &&
                                        screenPoint.y < Screen.height * 0.28f;
            var rightBottomControlHint = screenPoint.x > Screen.width * 0.62f &&
                                         screenPoint.y < Screen.height * 0.32f;

            return leftTopTaskList || leftBottomAchievement || rightBottomControlHint;
        }

        private static bool IsPointInTargetRegion(Vector2 screenPoint, ReadTarget target)
        {
            switch (target)
            {
                case ReadTarget.Main:
                    return !IsExcludedHudRegion(screenPoint);
                case ReadTarget.TopLeftTaskList:
                    return IsTopLeftTaskListRegion(screenPoint);
                case ReadTarget.BottomLeftAchievement:
                    return IsBottomLeftAchievementRegion(screenPoint);
                case ReadTarget.BottomRightControlHint:
                    return IsBottomRightControlHintRegion(screenPoint);
                default:
                    return false;
            }
        }

        private static bool IsTopLeftTaskListRegion(Vector2 screenPoint)
        {
            return screenPoint.x < Screen.width * 0.34f &&
                   screenPoint.y > Screen.height * 0.50f;
        }

        private static bool IsBottomLeftAchievementRegion(Vector2 screenPoint)
        {
            return screenPoint.x < Screen.width * 0.42f &&
                   screenPoint.y < Screen.height * 0.34f;
        }

        private static bool IsBottomRightControlHintRegion(Vector2 screenPoint)
        {
            return screenPoint.x > Screen.width * 0.55f &&
                   screenPoint.y < Screen.height * 0.36f;
        }

        private static string GetTargetName(ReadTarget target)
        {
            switch (target)
            {
                case ReadTarget.Main:
                    return "中间主要内容";
                case ReadTarget.TopLeftTaskList:
                    return "左上角任务清单";
                case ReadTarget.BottomLeftAchievement:
                    return "左下角成就系统";
                case ReadTarget.BottomRightControlHint:
                    return "右下角操作提示";
                default:
                    return "当前区域";
            }
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

        private static bool IsNear(TextCandidate a, TextCandidate b)
        {
            return Mathf.Abs(a.ScreenPoint.x - b.ScreenPoint.x) <= Screen.width * 0.42f &&
                   Mathf.Abs(a.ScreenPoint.y - b.ScreenPoint.y) <= Screen.height * 0.42f;
        }

        private static int CompareReadingOrder(TextCandidate a, TextCandidate b)
        {
            var y = b.ScreenPoint.y.CompareTo(a.ScreenPoint.y);
            if (Mathf.Abs(a.ScreenPoint.y - b.ScreenPoint.y) > 24f)
            {
                return y;
            }

            return a.ScreenPoint.x.CompareTo(b.ScreenPoint.x);
        }

        private static bool IsDuplicateOfExisting(HashSet<string> existing, string text)
        {
            foreach (var item in existing)
            {
                if (item == text)
                {
                    continue;
                }

                if (item.Contains(text) || text.Contains(item))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool EndsWithSentencePunctuation(StringBuilder builder)
        {
            if (builder.Length == 0)
            {
                return false;
            }

            var ch = builder[builder.Length - 1];
            return ch == '。' || ch == '！' || ch == '？' || ch == '.' || ch == '!' || ch == '?';
        }

        private static void ConfigureExternalPaths()
        {
            var pluginDir = GetPluginDir();
            var workspaceMarkerPath = Path.Combine(pluginDir, "workspace-root.txt");
            if (File.Exists(workspaceMarkerPath))
            {
                _workspaceRoot = File.ReadAllText(workspaceMarkerPath, Encoding.UTF8).Trim();
            }

            if (string.IsNullOrWhiteSpace(_workspaceRoot) || !Directory.Exists(_workspaceRoot))
            {
                _workspaceRoot = pluginDir;
            }

            _windowsTtsCacheDir = Path.Combine(_workspaceRoot, "audio_cache", "windows", "zh-CN");
            _mimoTtsCacheDir = Path.Combine(_workspaceRoot, "audio_cache", "mimo", "zh-CN");
            _windowsTtsScriptPath = Path.Combine(_workspaceRoot, "tools", "generate-windows-tts.ps1");
            EnsureTtsCacheDirs();

            _log?.LogInfo("External workspace root: " + _workspaceRoot);
            _log?.LogInfo("Windows TTS cache dir: " + _windowsTtsCacheDir);
            _log?.LogInfo("MiMo TTS cache dir: " + _mimoTtsCacheDir);
            _log?.LogInfo("Windows TTS script: " + _windowsTtsScriptPath);
        }

        private static string GetPluginDir()
        {
            try
            {
                var zhDir = new DirectoryInfo(_audioDir ?? string.Empty);
                var audioDir = zhDir.Parent;
                var pluginDir = audioDir?.Parent;
                if (pluginDir != null)
                {
                    return pluginDir.FullName;
                }
            }
            catch
            {
                // Fall through to the current directory.
            }

            return Directory.GetCurrentDirectory();
        }

        private static void EnsureWindowsTtsCacheDir()
        {
            if (!string.IsNullOrWhiteSpace(_windowsTtsCacheDir))
            {
                Directory.CreateDirectory(_windowsTtsCacheDir);
            }
        }

        private static void EnsureTtsCacheDirs()
        {
            EnsureWindowsTtsCacheDir();
            if (!string.IsNullOrWhiteSpace(_mimoTtsCacheDir))
            {
                Directory.CreateDirectory(_mimoTtsCacheDir);
            }
        }

        private static string ComputeTextHash(string text)
        {
            using (var sha1 = SHA1.Create())
            {
                var bytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(text));
                var builder = new StringBuilder(bytes.Length * 2);
                for (var i = 0; i < bytes.Length; i++)
                {
                    builder.Append(bytes[i].ToString("x2"));
                }

                return builder.ToString();
            }
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static string TrimForLog(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            value = value.Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= 500 ? value : value.Substring(0, 500);
        }

        private static AudioClip LoadWav(string path)
        {
            if (!File.Exists(path))
            {
                _log?.LogWarning("WAV not found: " + path);
                return null;
            }

            var data = File.ReadAllBytes(path);
            if (data.Length < 44)
            {
                _log?.LogWarning("WAV too short: " + path);
                return null;
            }

            var channels = BitConverter.ToInt16(data, 22);
            var sampleRate = BitConverter.ToInt32(data, 24);
            var bitsPerSample = BitConverter.ToInt16(data, 34);
            if (bitsPerSample != 16 || channels <= 0)
            {
                _log?.LogWarning("Unsupported WAV format. channels=" + channels + ", bits=" + bitsPerSample);
                return null;
            }

            var dataOffset = FindChunk(data, "data");
            if (dataOffset < 0)
            {
                _log?.LogWarning("WAV data chunk not found: " + path);
                return null;
            }

            var byteCount = BitConverter.ToInt32(data, dataOffset + 4);
            var pcmOffset = dataOffset + 8;
            var sampleCount = byteCount / 2 / channels;
            var samples = new float[sampleCount * channels];

            for (var i = 0; i < samples.Length; i++)
            {
                var value = BitConverter.ToInt16(data, pcmOffset + i * 2);
                samples[i] = value / 32768f;
            }

            var clip = AudioClip.Create(Path.GetFileNameWithoutExtension(path), sampleCount, channels, sampleRate, false);
            clip.SetData(samples, 0);
            _log?.LogInfo("Loaded WAV: " + path + " samples=" + sampleCount + " channels=" + channels + " rate=" + sampleRate);
            return clip;
        }

        private static int FindChunk(byte[] data, string chunkName)
        {
            for (var i = 12; i < data.Length - 8; i++)
            {
                if (data[i] == chunkName[0] &&
                    data[i + 1] == chunkName[1] &&
                    data[i + 2] == chunkName[2] &&
                    data[i + 3] == chunkName[3])
                {
                    return i;
                }
            }

            return -1;
        }

        private sealed class TextCandidate
        {
            public string Text;
            public Vector2 ScreenPoint;
            public float FontSize;
            public int ChineseCount;
            public float Score;
        }
    }
}
