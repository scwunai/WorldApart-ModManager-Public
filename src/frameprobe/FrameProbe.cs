using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace FrameProbe
{
    /// <summary>
    /// Measurement-only plugin (P1.0). Keeps a per-frame time series and appends a
    /// summary line every WINDOW seconds, so scenarios "inject only" (B) and
    /// "inject + mod manager" (C/D) can be compared with the same instrument.
    ///
    /// Per frame it does one array store and one float add; everything else
    /// (sort, formatting, file write) happens once per WINDOW seconds.
    ///
    /// env MOD_FRAME_LOG  target log file (default <game>\KIMI\v4_frames.log)
    /// env MOD_FRAME_TAG  scenario tag written into every line
    /// </summary>
    [BepInPlugin(Guid, "Frame Probe", "1.0.0")]
    public class FrameProbePlugin : BasePlugin
    {
        public const string Guid = "local.frameprobe";

        internal static ManualLogSource Log2;

        public override void Load()
        {
            Log2 = Log;
            ClassInjector.RegisterTypeInIl2Cpp<FrameSampler>();
            AddComponent<FrameSampler>();
            Log.LogInfo("FrameProbe loaded; realtimeSinceStartup=" +
                        Time.realtimeSinceStartup.ToString("F3", CultureInfo.InvariantCulture));
        }
    }

    public class FrameSampler : MonoBehaviour
    {
        private const float Window = 30f;
        private const float MaxValidFrame = 0.5f;   // above this it is a focus gap, not a slow frame

        private static string LogPath;
        private static string Tag;

        private readonly List<float> _window = new List<float>(8192);
        private float _acc;
        private int _frames;
        private float _winStart = -1f;
        private bool _firstFrameLogged;
        private int _windows;
        private int _longFrames;
        private int _dropped;
        private bool _wasFocused = true;
        private bool _runInBackground;

        public FrameSampler(IntPtr ptr) : base(ptr) { }

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;
            bool focused = Application.isFocused;

            if (!_firstFrameLogged)
            {
                _firstFrameLogged = true;
                _runInBackground = Application.runInBackground;
                Line("startup first-frame t=" + Time.realtimeSinceStartup.ToString("F3", CultureInfo.InvariantCulture)
                     + " dt=" + dt.ToString("F5", CultureInfo.InvariantCulture)
                     + " runInBackground=" + _runInBackground);
            }

            if (focused != _wasFocused)
            {
                _wasFocused = focused;
                Line("FOCUS t=" + Time.realtimeSinceStartup.ToString("F3", CultureInfo.InvariantCulture)
                     + " focused=" + focused + " dt=" + (dt * 1000f).ToString("F1", CultureInfo.InvariantCulture) + "ms");
            }

            // An unfocused Unity player stops rendering, so the first delta after
            // losing focus is pure wall-clock and must not pollute the statistics.
            if (dt <= MaxValidFrame)
            {
                _window.Add(dt);
                _acc += dt;
                _frames++;
            }
            else
            {
                _dropped++;
            }
            if (_winStart < 0f) _winStart = Time.realtimeSinceStartup;

            // capture the individual long frames so the stall period is visible
            if (dt > 0.1f && _longFrames < 400)
            {
                _longFrames++;
                Line("LONGFRAME t=" + Time.realtimeSinceStartup.ToString("F3", CultureInfo.InvariantCulture)
                     + " dt=" + (dt * 1000f).ToString("F1", CultureInfo.InvariantCulture) + "ms focused=" + focused);
            }

            if (_acc >= Window) Flush();
        }

        private void Flush()
        {
            int n = _window.Count;
            if (n == 0) return;
            float sum = 0f;
            for (int i = 0; i < n; i++) sum += _window[i];
            float avg = sum / n;

            var sorted = new List<float>(_window);
            sorted.Sort();
            float p50 = sorted[(int)(n * 0.50f)];
            float p95 = sorted[Math.Min(n - 1, (int)(n * 0.95f))];
            float p99 = sorted[Math.Min(n - 1, (int)(n * 0.99f))];
            float max = sorted[n - 1];

            NumberFormatInfo ci = CultureInfo.InvariantCulture.NumberFormat;
            Line(string.Format(ci,
                "win={0} frames={1} dropped={2} dur={3:F2}s avg={4:F3}ms p50={5:F3}ms p95={6:F3}ms p99={7:F3}ms max={8:F3}ms fps={9:F1}",
                _windows, n, _dropped, _acc, avg * 1000f, p50 * 1000f, p95 * 1000f, p99 * 1000f, max * 1000f,
                n / _acc));

            _windows++;
            _window.Clear();
            _acc = 0f;
            _frames = 0;
            _dropped = 0;
            _winStart = Time.realtimeSinceStartup;
        }

        private static void Line(string s)
        {
            if (LogPath == null)
            {
                LogPath = Environment.GetEnvironmentVariable("MOD_FRAME_LOG");
                if (string.IsNullOrEmpty(LogPath))
                    LogPath = @"<G>\KIMI\v4_frames.log";
                Tag = Environment.GetEnvironmentVariable("MOD_FRAME_TAG");
                if (Tag == null) Tag = "?";
            }
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + " [" + Tag + "] " + s;
            try { File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8); }
            catch (Exception e) { FrameProbePlugin.Log2.LogWarning("frame log write failed: " + e.Message); }
            FrameProbePlugin.Log2.LogInfo("FRAME " + line);
        }
    }
}
