using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // 파티클 장식(scrParticleDecoration).
    // 1) 변화 없는 갱신 건너뛰기 (기본 켜짐, 화면 동일)
    //    Update 가 매 프레임 파티클 모양 크기(shape.scale)와 시뮬레이션 속도(main.simulationSpeed = 속도 x 곡 피치)를 유니티에 넣는다(IL 확인).
    //    둘 다 엔진 호출이라, 넣을 값이 지난번에 넣은 값과 비트까지 같으면 건너뛴다. 이 두 값을 쓰는 곳은 Update 말고 ResetParticle, SetScale
    //    뿐이라(IL 전체 검색) 그 둘이 불리면 기억을 지운다. 파티클 시스템이 바뀌거나 에디터 편집 중이면 원래대로 넣는다.
    // 2) (저사양) 화면 밖 파티클 멈추기 (기본 꺼짐)
    //    파티클 시스템의 컬링 방식을 "화면 밖이면 멈춤" 으로 바꿔 화면 밖 파티클의 CPU 시뮬레이션을 멈춘다. 다시 화면에 들어오면 멈춘 곳부터
    //    이어가므로 원래와 모양·시점이 달라질 수 있다. 끄면 원래 방식으로 되돌린다.
    // Quartz 의 같은 기능이 켜져 있으면 이쪽은 쉰다(Compat).
    internal static class ParticleFix
    {
        internal static bool SkipIdle = true;
        internal static bool PauseOffscreen;
        internal static long Skipped, Written, Paused;

        private sealed class State
        {
            public ParticleSystem Ps;
            public bool HasScale, HasSpeed, CullSet;
            public Vector3 Scale; public float Speed;
            public ParticleSystemCullingMode CullBefore;
        }
        private static readonly ConditionalWeakTable<scrParticleDecoration, State> states = new ConditionalWeakTable<scrParticleDecoration, State>();
        private static readonly AccessTools.FieldRef<scrParticleDecoration, ParticleSystem> psRef = AccessTools.FieldRefAccess<scrParticleDecoration, ParticleSystem>("particleSystem");
        private static State cur;
        private static readonly List<WeakReference> culled = new List<WeakReference>();

        internal static void Install(Harmony h)
        {
            try
            {
                var t = typeof(scrParticleDecoration);
                var upd = AccessTools.Method(t, "Update");
                if (upd == null) return;
                h.Patch(upd, prefix: new HarmonyMethod(typeof(ParticleFix), nameof(UpdatePrefix)), transpiler: new HarmonyMethod(typeof(ParticleFix), nameof(Transpiler)),
                    postfix: new HarmonyMethod(typeof(ParticleFix), nameof(UpdatePostfix)));
                foreach (var name in new[] { "ResetParticle", "SetScale", "SetSprite" })
                {
                    var m = AccessTools.Method(t, name);
                    if (m != null) h.Patch(m, postfix: new HarmonyMethod(typeof(ParticleFix), nameof(Forget)));
                }
                Main.Entry.Logger.Log("[파티클] 설치 (바꾼 호출 " + replaced + "곳)");
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[파티클] 설치 실패: " + ex.Message); }
        }

        private static int replaced;
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> ins)
        {
            var setScale = AccessTools.PropertySetter(typeof(ParticleSystem.ShapeModule), "scale");
            var setSpeed = AccessTools.PropertySetter(typeof(ParticleSystem.MainModule), "simulationSpeed");
            foreach (var i in ins)
            {
                if (i.opcode == OpCodes.Call && ReferenceEquals(i.operand, setScale)) { i.operand = AccessTools.Method(typeof(ParticleFix), nameof(ShapeScale)); replaced++; }
                else if (i.opcode == OpCodes.Call && ReferenceEquals(i.operand, setSpeed)) { i.operand = AccessTools.Method(typeof(ParticleFix), nameof(SimSpeed)); replaced++; }
                yield return i;
            }
        }

        private static bool Active { get { return SkipIdle && !Compat.QSkipIdleParticles && Hitch.Playing; } }

        public static void UpdatePrefix(scrParticleDecoration __instance)
        {
            cur = null;
            try
            {
                var ps = psRef(__instance);
                if ((object)ps == null) return;
                var s = states.GetOrCreateValue(__instance);
                if (!ReferenceEquals(s.Ps, ps)) { s.Ps = ps; s.HasScale = s.HasSpeed = false; s.CullSet = false; }
                cur = s;
                // 저사양: 화면 밖이면 멈춤
                bool wantPause = PauseOffscreen && !Compat.QPauseOffscreenParticles && Hitch.Playing;
                if (wantPause != s.CullSet)
                {
                    var main = ps.main;
                    if (wantPause) { s.CullBefore = main.cullingMode; main.cullingMode = ParticleSystemCullingMode.Pause; Paused++; lock (culled) culled.Add(new WeakReference(__instance)); }
                    else main.cullingMode = s.CullBefore;
                    s.CullSet = wantPause;
                }
            }
            catch { cur = null; }
        }
        public static void UpdatePostfix() { cur = null; }

        public static void Forget(scrParticleDecoration __instance)
        {
            State s;
            if (states.TryGetValue(__instance, out s)) { s.HasScale = s.HasSpeed = false; }
        }

        public static void ShapeScale(ref ParticleSystem.ShapeModule m, Vector3 v)
        {
            var s = cur;
            if (s != null && Active && s.HasScale && s.Scale.x == v.x && s.Scale.y == v.y && s.Scale.z == v.z) { Skipped++; return; }
            m.scale = v; Written++;
            if (s != null) { s.Scale = v; s.HasScale = true; }
        }
        public static void SimSpeed(ref ParticleSystem.MainModule m, float v)
        {
            var s = cur;
            if (s != null && Active && s.HasSpeed && s.Speed == v) { Skipped++; return; }
            m.simulationSpeed = v; Written++;
            if (s != null) { s.Speed = v; s.HasSpeed = true; }
        }

        // 모드를 끌 때: 멈춤으로 바꾼 컬링 방식을 되돌린다
        internal static void Shutdown()
        {
            lock (culled)
            {
                foreach (var w in culled)
                {
                    var d = w.Target as scrParticleDecoration; State s;
                    if (d == null || !states.TryGetValue(d, out s) || !s.CullSet || s.Ps == null) continue;
                    try { var main = s.Ps.main; main.cullingMode = s.CullBefore; s.CullSet = false; } catch { }
                }
                culled.Clear();
            }
        }

        internal static string Summary()
        {
            if (Skipped + Written == 0) return "";
            return string.Format(" | 파티클 장식: 같은 값 넣기 {0}번 중 {1}번 건너뜀{2}", Skipped + Written, Skipped, Paused > 0 ? ", 화면 밖 멈춤 " + Paused + "개" : "");
        }
    }
}
