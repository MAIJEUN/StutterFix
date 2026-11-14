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
    //    사람이 끈 것은 세지 않는다 (Alt+F4 뒤 저장 물음에서 강제로 끄거나, 작업 관리자로 끄는 사람이 많아 그때마다 안전 모드가 됐다):
    //      - 창 닫기 요청(Application.wantsToQuit, Alt+F4·X 버튼)이 온 뒤 끝났으면 "끄는 중" 표시가 남아 있다 -> 세지 않음.
    //      - 표시 없이 끝났어도, 끝나기 직전까지 게임이 돌고 있었고(멈춤 표시 없음) 지난 로그에 유니티 튕김 기록이 없으면 강제 종료로 본다 -> 세지 않음.
    //    세는 것: 지난 로그(Player-prev.log)에 튕김 기록이 있거나, 메인 스레드가 HangSeconds 넘게 멈춘 채로 끝난 경우(옆 스레드가 그때 한 번 표시).
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
            { "FastMove", new[] { "FastLoop" } }, { "Precheck", new[] { "Precheck" } }, { "DecoAnim", new[] { "DecoAnim" } }, { "FloorAnim", new[] { "FloorAnim" } },
            { "MoveApply", new[] { "MoveFinish" } }, { "Dormancy", new[] { "DormantSkip" } }, { "TextFix", new[] { "SkipSameText" } }, { "TypeCache", new[] { "FilterTypeCache" } }, { "MeshWarm", new[] { "MeshWarm" } }, { "SoundWarm", new[] { "SoundWarm" } }, { "JitWarm", new[] { "JitWarm" } },
            { "ImagePrefetch", new[] { "ImagePrefetch" } }, { "PngDecoder", new[] { "ImagePrefetch" } }, { "TexCompress", new[] { "ImagePrefetch" } }, { "DxtEncoder", new[] { "ImagePrefetch" } }, { "SfNative", new[] { "ImagePrefetch" } }, { "TurboJpeg", new[] { "ImagePrefetch" } },
            { "ShaderWarm", new[] { "ShaderWarm" } }, { "FastBlend", new[] { "FastBlend" } }, { "InvisibleSkip", new[] { "SkipInvisible" } },
            { "ParticleFix", new[] { "SkipIdleParticles", "LowPauseParticles" } }, { "LeakGuard", new[] { "LeakFix" } }, { "LoadFix", new[] { "LoadCache" } }, { "TransitionFix", new[] { "LoadCache" } },
            { "Fsr", new[] { "LowFsr" } }, { "HalfRender", new[] { "LowHalfRender" } }, { "LowEnd", new[] { "LowEnd" } },
        };
        // 안전 모드에서 끄는 것: 게임 동작에 깊이 끼어드는 기능 (메모리 정리 미루기·같은 글자 건너뛰기처럼 단순한 것은 둔다)
        // (이미지 미리 풀기는 끄지 않는다: 큰 이미지 줄이기가 이 길에만 있어, 끄면 이미지 원본이 아주 큰 맵이 메모리 부족으로 다시 꺼지는
        //  악순환이 된다(2026-10-04 ALPHA). 대신 안전 모드에서는 줄일 필요가 있는 맵에서만 쓴다 - ImagePrefetch.Begin)
        private static readonly string[] safeOff = { "LoadCache", "DecoAnim", "FloorAnim", "FastLoop", "InstantDirect", "ZeroTween", "Precheck", "DormantSkip",
            "SkipInvisible", "MoveFinish", "FastBlend", "SkipIdleParticles", "LowPauseParticles", "LeakFix", "LowFsr", "LowHalfRender", "LowEnd" };

#if DEV || AUTOTEST || GPUDECO
        internal static int TotalErrors { get { lock (sync) { int sum = 0; foreach (var v in errors.Values) sum += v; return sum; } } }
        internal static void RecordFeatureFailure(string feature, string reason)
        {
            int n; lock (sync) { errors.TryGetValue(feature, out n); errors[feature] = ++n; }
            Main.Entry.Logger.Log("[안정성] " + feature + " 실패 " + n + "회: " + reason);
        }
#endif
        internal static bool Off(string key) { lock (sync) return off.Contains(key); }

        internal static void Init()   // 여러 번 불려도 한 번만 (UMM 은 Load 다음 OnToggle(true) 도 부른다)
        {
            if (lockPath != null) return;   // 이미 준비됨: 다시 하면 방금 남긴 session.lock 을 "지난번 것" 으로 착각한다(2026-09-26)
            try
            {
                lockPath = Path.Combine(Main.Entry.Path, "session.lock");
                if (File.Exists(lockPath))
                {
                    LastCrashPhase = File.ReadAllText(lockPath).Trim();
                    bool hung = LastCrashPhase.StartsWith(HungMark, StringComparison.Ordinal);
                    bool closing = LastCrashPhase.StartsWith(ClosingMark, StringComparison.Ordinal);
                    bool crashed = PrevLogHasCrash();
                    if (crashed || hung)
                    {
                        Main.Config.CrashStreak++;
                        Main.Entry.Logger.Log("[안정성] 지난번 게임이 비정상 종료됨 (" + (crashed ? "튕김 기록 있음" : "멈춘 채로 끝남") + ", 그때 단계: " + LastCrashPhase + ", 연속 " + Main.Config.CrashStreak + "번)");
                    }
                    else
                    {
                        // 사람이 끈 것: 연속 횟수는 그대로 둔다(올리지도 지우지도 않음)
                        Main.Entry.Logger.Log("[안정성] 지난번 게임은 " + (closing ? "창 닫기 요청 뒤" : "돌던 중에 강제로") + " 꺼짐 - 사람이 끈 것으로 보고 세지 않음 (그때 단계: " + LastCrashPhase + ")");
                        LastCrashPhase = "";
                    }
                }
                else Main.Config.CrashStreak = 0;
                if (Main.Config.CrashStreak >= 2 && LastCrashPhase.Length > 0) EnterSafeMode("게임이 " + Main.Config.CrashStreak + "번 연속 비정상 종료됨 (마지막 단계: " + LastCrashPhase + ")");
                else if (Main.Config.CrashStreak == 1 && LastCrashPhase.Length > 0) lock (sync)
                    Notes.Add(SettingsWindow.T("지난번 게임이 비정상 종료됐습니다 (그때: " + LastCrashPhase + "). 한 번 더 그러면 자동으로 안전 모드로 켜집니다.",
                        "The game ended abnormally last time (during: " + LastCrashPhase + "). If it happens again, safe mode turns on automatically."));
                try { Main.Config.Save(Main.Entry); } catch { }
                Phase("시작");
                Application.quitting += OnQuit;
                Application.wantsToQuit += OnWantsToQuit;
                focused = Application.isFocused; Application.focusChanged += OnFocus;
                Application.logMessageReceivedThreaded += OnLog;
                lastTick = System.Diagnostics.Stopwatch.GetTimestamp();
                StartWatch();
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[안정성] 준비 실패: " + ex.Message); }
        }

        internal static void Shutdown()
        {
            Application.quitting -= OnQuit;
            Application.wantsToQuit -= OnWantsToQuit;
            Application.focusChanged -= OnFocus;
            Application.logMessageReceivedThreaded -= OnLog;
            Stop();   // 모드를 끄거나 다시 불러오는 것도 정상
        }

        private static void OnQuit() { try { if (lockPath != null && File.Exists(lockPath)) File.Delete(lockPath); } catch { } }
        // 모드를 끄면 단계 기록도 멈춘다 (다시 켜면 Init)
        private static void Stop() { watchGen++; OnQuit(); lockPath = null; phase = ""; closingAt = 0; hungMarked = false; }

        // ── 사람이 끈 것과 튕김·멈춤 가르기 ──
        private const string ClosingMark = "[끄는 중] ", HungMark = "[멈춤] ";
        internal static float HangSeconds = 20f;
        private static readonly object fileLock = new object();
        private static long lastTick, closingAt;
        private static volatile bool hungMarked;
        private static volatile int watchGen;
        private static volatile bool focused = true;   // 창이 뒤에 있으면 게임이 스스로 쉴 수 있어 멈춤으로 보지 않는다
        private static void OnFocus(bool f) { focused = f; System.Threading.Interlocked.Exchange(ref lastTick, System.Diagnostics.Stopwatch.GetTimestamp()); }

        // 창 닫기 요청 (Alt+F4, X 버튼, Application.Quit). 막지 않는다(true). 게임이 저장 물음으로 취소할 수 있어, 20초 뒤에도 돌고 있으면 표시를 지운다(Tick).
        private static bool OnWantsToQuit()
        {
            try { closingAt = System.Diagnostics.Stopwatch.GetTimestamp(); WriteLock(); } catch { }
            return true;
        }

        private static void WriteLock()
        {
            if (lockPath == null) return;
            string body = phase + " (" + DateTime.Now.ToString("HH:mm:ss") + ")";
            string text = hungMarked ? HungMark + body : closingAt != 0 ? ClosingMark + body : body;
            lock (fileLock) { try { File.WriteAllText(lockPath, text); } catch { } }
        }

        // 옆 스레드: 메인 스레드가 HangSeconds 넘게 안 돌면 한 번 "멈춤" 표시. 다시 돌면 Tick 이 지운다. 평소에는 파일을 쓰지 않는다.
        private static void StartWatch()
        {
            int gen = ++watchGen;
            var th = new System.Threading.Thread(() =>
            {
                while (gen == watchGen)
                {
                    System.Threading.Thread.Sleep(2000);
                    if (gen != watchGen || lockPath == null || hungMarked || !focused) continue;
                    double idle = (System.Diagnostics.Stopwatch.GetTimestamp() - System.Threading.Interlocked.Read(ref lastTick)) / (double)System.Diagnostics.Stopwatch.Frequency;
                    if (idle < HangSeconds) continue;
                    hungMarked = true;
                    try { WriteLock(); } catch { }
                }
            }) { IsBackground = true, Name = "StutterFix.HangWatch" };
            th.Start();
        }

        // 지난 실행의 게임 로그에 유니티 튕김 기록이 있는지 (끝 256KB 만 읽는다)
        private static bool PrevLogHasCrash()
        {
            try
            {
                string log = Application.consoleLogPath;
                if (string.IsNullOrEmpty(log)) return false;
                string prev = Path.Combine(Path.GetDirectoryName(log), "Player-prev.log");
                if (!File.Exists(prev)) return false;
                using (var fs = new FileStream(prev, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long n = Math.Min(fs.Length, 256 * 1024);
                    fs.Seek(-n, SeekOrigin.End);
                    var buf = new byte[n];
                    int got = 0; while (got < n) { int r = fs.Read(buf, got, (int)n - got); if (r <= 0) break; got += r; }
                    string tail = System.Text.Encoding.UTF8.GetString(buf, 0, got);
                    return tail.Contains("Crash!!!") || tail.Contains("OUTPUTTING STACK TRACE") || tail.Contains("A crash has been intercepted");
                }
            }
            catch { return false; }
        }

        // 지금 무엇을 하는 중인지 (비정상 종료 때 어디서 끝났는지 알려고). 단계가 바뀔 때만 부른다.
        private static string phase = "";
        internal static void Phase(string p)
        {
            if (p == phase || lockPath == null) return;
            phase = p;
            WriteLock();
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
            long nowT = System.Diagnostics.Stopwatch.GetTimestamp();
            System.Threading.Interlocked.Exchange(ref lastTick, nowT);
            if (hungMarked) { hungMarked = false; WriteLock(); }   // 다시 돌기 시작함 (긴 불러오기 등)
            if (closingAt != 0 && (nowT - closingAt) / (double)System.Diagnostics.Stopwatch.Frequency > 20.0) { closingAt = 0; WriteLock(); }   // 닫기를 취소하고 계속 함
            if (!pendingApply) return;
            pendingApply = false;
            try { Main.ApplyConfig(); } catch { }
        }

        // 설정 값에 이번 실행 동안의 끔을 겹친다
        internal static bool Eff(string key, bool value) { return value && !Off(key); }
    }
}
