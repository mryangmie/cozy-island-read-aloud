using System;
using System.Collections;
using System.IO;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Networking;

namespace CozyIslandReadAloud
{
    internal sealed class ReadAloudRunner : MonoBehaviour
    {
        private static string _audioDir;
        private static ManualLogSource _log;

        private AudioSource _audioSource;
        private float _tickTimer;
        private bool _loggedFirstGui;
        private bool _isLoading;

        public static void Configure(string audioDir, ManualLogSource log)
        {
            _audioDir = audioDir;
            _log = log;
        }

        public void Awake()
        {
            DontDestroyOnLoad(gameObject);
            _audioSource = gameObject.AddComponent<AudioSource>();
            _audioSource.playOnAwake = false;
            _audioSource.spatialBlend = 0f;
            _log?.LogInfo("Runner Awake. Audio dir: " + _audioDir);
        }

        public void OnEnable()
        {
            _log?.LogInfo("Runner OnEnable");
        }

        public void Start()
        {
            _log?.LogInfo("Runner Start. Screen=" + Screen.width + "x" + Screen.height);
        }

        public void Update()
        {
            _tickTimer += Time.unscaledDeltaTime;
            if (_tickTimer >= 2f)
            {
                _tickTimer = 0f;
                _log?.LogInfo("Runner tick. Screen=" + Screen.width + "x" + Screen.height);
            }
        }

        public void OnGUI()
        {
            if (!_loggedFirstGui)
            {
                _loggedFirstGui = true;
                _log?.LogInfo("Runner first OnGUI. Screen=" + Screen.width + "x" + Screen.height);
            }

            const float width = 96f;
            const float height = 28f;
            var rect = new Rect((Screen.width - width) * 0.5f, 48f, width, height);

            var oldBackground = GUI.backgroundColor;
            var oldColor = GUI.color;
            GUI.depth = -32000;
            GUI.backgroundColor = new Color(0.02f, 0.05f, 0.08f, 0.34f);
            GUI.color = new Color(1f, 1f, 1f, 0.76f);

            if (GUI.Button(rect, "G/H/J/K"))
            {
                _log?.LogInfo("Runner read hint clicked.");
                PlayTest();
            }

            GUI.backgroundColor = oldBackground;
            GUI.color = oldColor;
        }

        private void PlayTest()
        {
            if (_isLoading)
            {
                return;
            }

            var path = Path.Combine(_audioDir ?? string.Empty, "test-dialog.wav");
            if (!File.Exists(path))
            {
                _log?.LogWarning("Test audio not found: " + path);
                return;
            }

            StartCoroutine(PlayWav(path));
        }

        private IEnumerator PlayWav(string audioPath)
        {
            _isLoading = true;

            var uri = new Uri(audioPath).AbsoluteUri;
            using (var request = UnityWebRequestMultimedia.GetAudioClip(uri, AudioType.WAV))
            {
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    _log?.LogWarning("Runner failed to load audio: " + request.error + " path=" + audioPath);
                    _isLoading = false;
                    yield break;
                }

                var clip = DownloadHandlerAudioClip.GetContent(request);
                _audioSource.Stop();
                _audioSource.clip = clip;
                _audioSource.Play();
                _log?.LogInfo("Runner played test audio.");
            }

            _isLoading = false;
        }
    }
}
