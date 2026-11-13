using System;
using System.Reflection;
using UnityEngine;

namespace StutterFix
{
    // 리플레이 모드(Chronos)와 같이 쓰기.
    // Chronos 는 앞뒤로 건너뛸 때 게임의 DOTween 트윈·ScrubToTime·장식 초기화로 화면을 되감는다. 이 모드가 장식·타일 애니메이션을
    // 대신 돌리거나(DecoAnim, FloorAnim) 건너뛰고 미루는 기능은 그 되감기에 보이지 않아, 건너뛴 뒤 장식 위치가 어긋났다(seekcheck decos 26/498 differ).
    // 그래서 Chronos 가 리플레이를 열거나 재생하는 동안만 그런 기능을 끄고(안전 모드와 같은 방식), 끝나면 원래대로 켠다.
    // 녹화(일반 플레이)는 게임을 그대로 돌리므로 건드리지 않는다. Chronos 가 없으면 아무 일도 하지 않는다.
    internal static class ReplayCompat
    {
        // Chronos 되감기가 보지 못하는 상태를 가진 기능만: 게임 트윈 대신 자기 애니메이션(DecoAnim·FloorAnim·ZeroTween),
        // 장식 갱신 건너뛰기·미루기(DormantSkip·SkipInvisible·MoveFinish·MoveEpsilon), 렌더러를 가져가는 GPU 장식.
        // 효과 나눠 처리·빠른 이동 같은 렉 막는 기능은 둔다: 처음에 그것까지 껐더니 효과가 몰린 순간 97ms 끊김이 나서
        // 리플레이의 공이 어긋났다(궤도 경고 57 -> 166). 리플레이는 프레임이 고를수록 정확하다.
        private static readonly string[] Keys = {
            "DecoAnim", "FloorAnim", "LowFloorSplit", "ZeroTween", "DormantSkip", "SkipInvisible", "MoveFinish", "MoveEpsilon", "GpuDeco" };

        internal static bool Active;
        private static bool looked;
        private static PropertyInfo isReplaying;
        private static FieldInfo pending;
        private static int nextCheck;
        private static bool tightWas, lazyWas;

        internal static void Tick()
        {
            if (Time.frameCount < nextCheck) return;
            nextCheck = Time.frameCount + 10;
            bool want = Replaying();
            if (want == Active) return;
            Active = want;
            try
            {
                if (want)
                {
                    tightWas = TightLate.Enabled; lazyWas = LazySprite.Enabled;
                    TightLate.Enabled = false; TightLate.Forget(); LazySprite.Enabled = false;
                    Resilience.SetReplayOff(Keys);
                }
                else
                {
                    Resilience.SetReplayOff(null);
                    TightLate.Enabled = tightWas; LazySprite.Enabled = lazyWas;
                }
                Main.ApplyConfig();
                Main.Entry.Logger.Log(want ? "[리플레이 모드] 리플레이 재생 중: 애니메이션을 대신 돌리는 기능 " + Keys.Length + "가지를 잠시 끔"
                                           : "[리플레이 모드] 리플레이 끝: 원래 설정으로 되돌림");
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[리플레이 모드] 전환 실패: " + ex.Message); }
        }

        // 모드를 끄거나 다시 불러올 때: 끈 것을 되돌린다
        internal static void Reset()
        {
            if (!Active) return;
            Active = false;
            Resilience.SetReplayOff(null);
            TightLate.Enabled = tightWas; LazySprite.Enabled = lazyWas;
        }

        private static bool Replaying()
        {
            if (!looked)
            {
                looked = true;
                // 비교용: 모드 폴더에 replaycompat-off 파일이 있으면 리플레이 중에도 아무것도 끄지 않는다
                if (System.IO.File.Exists(System.IO.Path.Combine(Main.Entry.Path, "replaycompat-off"))) { Main.Entry.Logger.Log("[리플레이 모드] replaycompat-off 파일이 있어 리플레이 중에도 그대로 둠"); return false; }
                try
                {
                    Type t = null;
                    foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                        if (a.GetName().Name == "Chronos") { t = a.GetType("ReplayMod.ReplayManager", false); if (t != null) break; }
                    if (t == null) return false;
                    isReplaying = t.GetProperty("IsReplaying", BindingFlags.Public | BindingFlags.Static);
                    pending = t.GetField("PendingReplay", BindingFlags.Public | BindingFlags.Static);
                    Main.Entry.Logger.Log("[리플레이 모드] Chronos 찾음: 리플레이 재생 중에는 애니메이션 관련 기능을 잠시 끔");
                }
                catch { isReplaying = null; pending = null; }
            }
            try
            {
                return (isReplaying != null && (bool)isReplaying.GetValue(null, null)) || (pending != null && (bool)pending.GetValue(null));
            }
            catch { return false; }
        }
    }
}
