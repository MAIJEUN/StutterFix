using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace StutterFix
{
    // 모르는 모드와 부딪혔을 때 스스로 대처하기.
    //
    // 1) 오류 자동 차단: 게임 로그에 올라온 예외 중 호출 경로에 이 모드 코드(StutterFix.클래스)가 있는 것을 기능별로 센다.
    //    한 기능에서 이번 실행 동안 Threshold 번 나면 그 기능만 이번 실행 동안 끈다(설정은 그대로, 다음 실행에서 다시 켜짐).
    //    어떤 모드가 같은 게임 함수를 고치는지 함께 로그와 설정 창에 남긴다.
    // 2) 비정상 종료 감지: 게임이 켜지면 모드 폴더에 session.lock 을 남기고 정상 종료 때 지운다. 다음 실행에 남아 있으면 비정상 종료
    //    (튕김·멈춰서 강제 종료). 두 번 연속이면 안전 모드(게임 동작에 깊이 관여하는 기능을 끔)로 켜고 설정 창에 알린다.
    //    어느 단계(맵 불러오는 중, 플레이 중 등)에서 끝났는지도 남긴다.
    internal static class Resilience
    {
        internal const int Threshold = 5;
        private static readonly Dictionary<string, int> errors = new Dictionary<string, int>();
        private static readonly HashSet<string> off = new HashSet<string>();          // 이번 실행 동안 끈 설정 이름
        internal static readonly List<string> Notes = new List<string>();              // 설정 창에 보일 안내 (sync 로 잠금)
        internal static List<string> NotesCopy() { lock (sync) return new List<string>(Notes); }
        internal static int OffCount { get { lock (sync) return off.Count; } }
        internal static bool SafeMode;
        internal static string LastCrashPhase = "";
        private static string lockPath;
        private static readonly object sync = new object();
        private static volatile bool pendingApply;

        // 클래스 이름 -> 끌 설정 이름들
        private static readonly Dictionary<string, string[]> map = new Dictionary<string, string[]>
        {
            { "GcControl", new[] { "GcPause" } }, { "EffectBudget", new[] { "EffectSplit" } }, { "RecolorSplit", new[] { "RecolorSplit" } },
            { "TweenFix", new[] { "TweenGuard" } }, { "ZeroTween", new[] { "ZeroTween" } }, { "InstantMove", new[] { "InstantDirect" } },
            { "FastMove", new[] { "FastLoop" } }, { "Precheck", new[] { "Precheck" } }, { "DecoAnim", new[] { "DecoAnim" } },
            { "MoveApply", new[] { "MoveFinish" } }, { "Dormancy", new[] { "DormantSkip" } }, { "TextFix", new[] { "SkipSameText" } },
            { "ImagePrefetch", new[] { "ImagePrefetch" } }, { "PngDecoder", new[] { "ImagePrefetch" } }, { "TexCompress", new[] { "ImagePrefetch" } }, { "DxtEncoder", new[] { "ImagePrefetch" } },
            { "ShaderWarm", new[] { "ShaderWarm" } }, { "FastBlend", new[] { "FastBlend" } }, { "InvisibleSkip", new[] { "SkipInvisible" } },
            { "ParticleFix", new[] { "SkipIdleParticles", "LowPauseParticles" } }, { "LeakGuard", new[] { "LeakFix" } }, { "LoadFix", new[] { "LoadCache" } },
            { "Fsr", new[] { "LowFsr" } }, { "HalfRender", new[] { "LowHalfRender" } }, { "LowEnd", new[] { "LowEnd" } },
        };
        // 안전 모드에서 끄는 것: 게임 동작에 깊이 끼어드는 기능 (메모리 정리 미루기·같은 글자 건너뛰기처럼 단순한 것은 둔다)
        private static readonly string[] safeOff = { "ImagePrefetch", "LoadCache", "DecoAnim", "FastLoop", "InstantDirect", "ZeroTween", "Precheck", "DormantSkip",
            "SkipInvisible", "MoveFinish", "FastBlend", "SkipIdleParticles", "LowPauseParticles", "LeakFix", "LowFsr", "LowHalfRender", "LowEnd" };

        internal static bool Off(string key) { lock (sync) return off.Contains(key); }

        internal static void Init()
        {
            try
            {
                lockPath = Path.Combine(Main.Entry.Path, "session.lock");
                if (File.Exists(lockPath))
                {
                    LastCrashPhase = File.ReadAllText(lockPath).Trim();
                    Main.Config.CrashStreak++;
                    Main.Entry.Logger.Log("[안정성] 지난번 게임이 비정상 종료됨 (그때 단계: " + LastCrashPhase + ", 연속 " + Main.Config.CrashStreak + "번)");
                }
                else Main.Config.CrashStreak = 0;
                if (Main.Config.CrashStreak >= 2) EnterSafeMode("게임이 " + Main.Config.CrashStreak + "번 연속 비정상 종료됨 (마지막 단계: " + LastCrashPhase + ")");
                else if (Main.Config.CrashStreak == 1) lock (sync)
                    Notes.Add(SettingsWindow.T("지난번 게임이 비정상 종료됐습니다 (그때: " + LastCrashPhase + "). 한 번 더 그러면 자동으로 안전 모드로 켜집니다.",
                        "The game ended abnormally last time (during: " + LastCrashPhase + "). If it happens again, safe mode turns on automatically."));
                try { Main.Config.Save(Main.Entry); } catch { }
                Phase("시작");
                Application.quitting += OnQuit;
                Application.logMessageReceivedThreaded += OnLog;
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[안정성] 준비 실패: " + ex.Message); }
        }

        internal static void Shutdown()
        {
            Application.quitting -= OnQuit;
            Application.logMessageReceivedThreaded -= OnLog;
            Stop();   // 모드를 끄거나 다시 불러오는 것도 정상
        }

        private static void OnQuit() { try { if (lockPath != null && File.Exists(lockPath)) File.Delete(lockPath); } catch { } }
        // 모드를 끄면 단계 기록도 멈춘다 (다시 켜면 Init)
        private static void Stop() { OnQuit(); lockPath = null; phase = ""; }

        // 지금 무엇을 하는 중인지 (비정상 종료 때 어디서 끝났는지 알려고). 단계가 바뀔 때만 부른다.
        private static string phase = "";
        internal static void Phase(string p)
        {
            if (p == phase || lockPath == null) return;
            phase = p;
            try { File.WriteAllText(lockPath, p + " (" + DateTime.Now.ToString("HH:mm:ss") + ")"); } catch { }
        }

        private static void EnterSafeMode(string why)
        {
            SafeMode = true;
            lock (sync)
            {
                foreach (var k in safeOff) off.Add(k);
                Notes.Add(SettingsWindow.T("안전 모드: " + why + ". 게임 동작에 깊이 관여하는 기능을 이번 실행 동안 껐습니다. 정상적으로 끝내면 다음 실행부터 원래대로 켜집니다.",
                    "Safe mode: " + why + ". Features that hook deep into the game are off for this run. After a normal exit they come back next launch."));
            }
            Main.Entry.Logger.Log("[안정성] 안전 모드로 켬: " + why);
        }

        internal static void EnterSafeModeManual()
        {
            EnterSafeMode(SettingsWindow.T("직접 켬", "turned on manually"));
            Main.ApplyConfig();
        }

        internal static void LeaveSafeMode()
        {
            SafeMode = false; Main.Config.CrashStreak = 0;
            lock (sync)
            {
                foreach (var k in safeOff) off.Remove(k);
                Notes.RemoveAll(n => n.StartsWith("안전 모드") || n.StartsWith("Safe mode"));
            }
            try { Main.Config.Save(Main.Entry); } catch { }
            Main.ApplyConfig();
        }

        internal static void ReenableAll()
        {
            lock (sync) { off.Clear(); errors.Clear(); Notes.Clear(); }
            SafeMode = false;
            Main.ApplyConfig();
        }

        // 다른 스레드에서도 불린다. 이 모드 코드가 호출 경로에 있는 예외만 센다.
        private static void OnLog(string condition, string stack, LogType type)
        {
            if (type != LogType.Exception || string.IsNullOrEmpty(stack)) return;
            try
            {
                string cls = null;
                int at = stack.IndexOf("StutterFix.", StringComparison.Ordinal);
                if (at < 0) return;
                int s = at + "StutterFix.".Length, e = s;
                while (e < stack.Length && (char.IsLetterOrDigit(stack[e]) || stack[e] == '_')) e++;
                cls = stack.Substring(s, e - s);
                string[] keys;
                if (!map.TryGetValue(cls, out keys)) return;
                int n;
                lock (sync)
                {
                    errors.TryGetValue(cls, out n); errors[cls] = ++n;
                    if (n != Threshold) return;
                    foreach (var k in keys) off.Add(k);
                }
                string first = condition.Length > 160 ? condition.Substring(0, 160) : condition;
                string msg = string.Format("{0} 기능에서 오류가 {1}번 나서 이번 실행 동안 껐습니다 (첫 오류: {2}). 다른 모드와 부딪혔을 수 있습니다{3}",
                    string.Join(", ", keys), Threshold, first, SharedHint());
                lock (sync) Notes.Add(msg);
                Main.Entry.Logger.Log("[안정성] " + msg + "\n" + stack);
                pendingApply = true;   // 설정 반영은 메인 스레드에서 (Tick)
            }
            catch { }
        }

        private static string SharedHint()
        {
            var s = Compat.SharedSummary;
            return string.IsNullOrEmpty(s) ? "" : " (같은 게임 함수를 고치는 모드: " + s + ")";
        }

        // 메인 스레드에서 매 프레임
        internal static void Tick()
        {
            if (!pendingApply) return;
            pendingApply = false;
            try { Main.ApplyConfig(); } catch { }
        }

        // 설정 값에 이번 실행 동안의 끔을 겹친다
        internal static bool Eff(string key, bool value) { return value && !Off(key); }
    }
}
