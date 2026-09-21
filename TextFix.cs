using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // 할당의 정체.
    //
    // scrTextDecoration.SetCollider 코루틴이 글자 크기를 재려고 매번 TextGenerator 를 새로 만든다.
    //
    //   var gen = new TextGenerator();                       <- 여기
    //   gen.GetPreferredWidth(text.text, settings);
    //   gen.GetPreferredHeight(text.text, settings);
    //
    // TextGenerator 는 글자 수만큼 내부 배열을 잡는 무거운 물건인데, 이 코루틴이 초당 3891번 돌았다.
    // 측정값: 96MB/s. 곡 전체 할당(110MB/s)의 대부분이 이 한 줄이었다.
    //
    // 하나를 만들어 두고 계속 다시 쓰면 내부 배열도 그대로 재활용되어 할당이 사라진다.
    // 글자 크기를 재는 계산 자체는 그대로라 결과는 달라지지 않는다.
    public static class TextFix
    {
        internal static int Replaced;
        internal static long Reused;

        private static TextGenerator shared;

        public static TextGenerator Shared()
        {
            Reused++;
            if (shared == null) shared = new TextGenerator();
            return shared;
        }

        // ── 같은 글자를 다시 넣으면 건너뛴다 ────────────────────────────
        // PACL2 모드가 scnGame.Update 에 끼어들어 매 프레임 글자 장식 34개를 같은 내용 그대로 다시 넣는다.
        // (호출 경로: PACL2.CustomFFX.Variables.VariableStateManager.UpdateTexts)
        // 학교 PC에서 TextGenerator 폭주가 없었던 이유가 이것이다.
        //
        // SetText 의 본문은 두 줄뿐이다.
        //   text.text = s;
        //   StartCoroutine(SetCollider());   <- 글자 크기를 다시 재는 코루틴
        // 내용이 같으면 결과도 똑같으므로 통째로 건너뛰어도 화면은 달라지지 않는다.
        internal static bool SkipSameText = true;
        internal static long SkippedSameText;

        private static System.Reflection.FieldInfo textField;
        private static System.Reflection.PropertyInfo textProp;

        public static bool SetTextPrefix(object __instance, string __0)
        {
            if (!SkipSameText) return true;
            try
            {
                var comp = textField.GetValue(__instance);
                if (comp == null) return true;
                if (textProp == null) textProp = AccessTools.Property(comp.GetType(), "text");
                var current = textProp.GetValue(comp, null) as string;
                if (!string.Equals(current, __0, StringComparison.Ordinal)) return true;
                SkippedSameText++;
                return false;
            }
            catch { return true; }
        }

        internal static void Install(Harmony harmony)
        {
            try
            {
                var owner = AccessTools.TypeByName("scrTextDecoration");
                if (owner == null) { Main.Entry.Logger.Error("scrTextDecoration 없음"); return; }

                textField = AccessTools.Field(owner, "text");
                var setText = AccessTools.Method(owner, "SetText", new[] { typeof(string) });
                if (textField != null && setText != null)
                {
                    harmony.Patch(setText, prefix: new HarmonyMethod(typeof(TextFix), nameof(SetTextPrefix)));
                    Main.Entry.Logger.Log("patched scrTextDecoration.SetText (같은 글자 건너뛰기)");
                }

                // 코루틴 본체는 컴파일러가 만든 <SetCollider>d__NN 클래스의 MoveNext 안에 있다.
                foreach (var nested in owner.GetNestedTypes(AccessTools.all))
                {
                    if (nested.Name.IndexOf("SetCollider", StringComparison.Ordinal) < 0) continue;
                    var move = AccessTools.DeclaredMethod(nested, "MoveNext");
                    if (move == null) continue;

                    harmony.Patch(move, transpiler: new HarmonyMethod(typeof(TextFix), nameof(Transpiler)));
                    Main.Entry.Logger.Log("patched " + nested.Name + ".MoveNext");
                }
            }
            catch (Exception ex)
            {
                Main.Entry.Logger.Error("text fix 실패: " + ex.Message);
            }
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var ctor = AccessTools.Constructor(typeof(TextGenerator), new Type[0]);
            var replacement = AccessTools.Method(typeof(TextFix), nameof(Shared));

            foreach (var ins in instructions)
            {
                // newobj 도 call 도 결과를 스택에 하나 올리므로 그대로 바꿔치기해도 균형이 맞는다.
                // 라벨을 잃지 않도록 명령어를 새로 만들지 않고 내용만 고친다.
                if (ins.opcode == OpCodes.Newobj && ReferenceEquals(ins.operand, ctor))
                {
                    ins.opcode = OpCodes.Call;
                    ins.operand = replacement;
                    Replaced++;
                }
                yield return ins;
            }
        }
    }
}
