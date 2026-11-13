#if AUTOTEST
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // Measurement build only. No persistent setting and no hooks while off.
    internal static class ScriptPath
    {
        const string SkipId = "StutterFix.scriptpath.skip";
        const string ExactId = "StutterFix.scriptpath.exact";
        const string GameHash = "EC0D7C3889696C5605D6DB4F443FD387F8668A90BA7B4DD2A1A2DA64B5ADA5BC";
        static readonly string[] Names = { "floor", "conductor", "canvas" };
        static readonly MethodInfo[] Targets = {
            AccessTools.DeclaredMethod(typeof(scrFloor), "Update"),
            AccessTools.DeclaredMethod(typeof(scrConductor), "Update"),
            AccessTools.DeclaredMethod(typeof(Canvas), "SendWillRenderCanvases") };
        static readonly bool[] skip = new bool[3];
        static readonly long[] skipped = new long[3];
        static Harmony skipHarmony, exactHarmony;
        static bool exact, verify, applied;
        static long calls, eligible, fast, checkedCalls, diffs;
        static AccessTools.FieldRef<scrFloor, bool> started;
        static readonly List<FieldInfo> fields = new List<FieldInfo>();
        static scrFloor pending;
        static object[] before;

        internal static bool Step(string arg)
        {
            var a = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (a.Length == 1 && a[0] == "state") { State(); return true; }
            if (a.Length == 1 && a[0] == "off") { Stop(); State(); return true; }
            if (a.Length == 2 && a[0] == "assert")
            {
                int k = Array.IndexOf(Names, a[1]);
                if (k < 0 || !skip[k] || skipped[k] == 0) throw new Exception("scriptskip INVALID: no active intercepted calls: " + a[1]);
                State(); Main.Entry.Logger.Log("[scriptskip accepted] ceiling " + a[1]); return true;
            }
            if (a.Length == 2 && a[0] == "exact")
            {
                if (a[1] == "assert")
                {
                    State();
                    if (!applied || diffs != 0 || eligible == 0 || (verify ? checkedCalls == 0 || checkedCalls != eligible : fast == 0))
                        throw new Exception("scriptskip exact INVALID: no admitted work or comparison mismatch");
                    Main.Entry.Logger.Log("[scriptskip accepted] exact " + (verify ? "verify" : "on"));
                    return true;
                }
                if (a[1] == "off") { StopExact(); State(); return true; }
                if (a[1] != "on" && a[1] != "verify") throw new Exception("scriptskip exact on|off|verify|assert");
                if (skip.Any(x => x)) throw new Exception("Turn all ceiling skips off before exact verification");
                StartExact(a[1] == "verify"); State(); return true;
            }
            if (a.Length != 2 || (a[1] != "on" && a[1] != "off"))
                throw new Exception("scriptskip floor|conductor|canvas on|off; state; assert <name>; exact on|off|verify|assert; off");
            int index = Array.IndexOf(Names, a[0]);
            if (index < 0) throw new Exception("Unknown scriptskip target: " + a[0]);
            if (a[1] == "off")
            {
                skip[index] = false;
                skipHarmony?.Unpatch(Targets[index], HarmonyPatchType.All, SkipId);
            }
            else if (!skip[index])
            {
                if (exact) throw new Exception("Turn exact off before ceiling skips");
                if (Targets[index] == null) throw new Exception("Missing scriptskip target");
                if (skipHarmony == null) skipHarmony = new Harmony(SkipId);
                skipped[index] = 0;
                skipHarmony.Patch(Targets[index], prefix: new HarmonyMethod(typeof(ScriptPath), nameof(Skip)) { priority = Priority.First });
                skip[index] = true;
            }
            State(); return true;
        }

        public static bool Skip(MethodBase __originalMethod)
        {
            for (int i = 0; i < Targets.Length; i++)
                if (__originalMethod == Targets[i] && skip[i]) { skipped[i]++; return false; }
            return true;
        }

        static void State()
        {
            Main.Entry.Logger.Log("[scriptskip] CEILING changes gameplay/rendering: " +
                string.Join(" ", Names.Select((x, i) => x + "=" + skip[i] + "/" + skipped[i])));
            Main.Entry.Logger.Log($"[scriptskip exact] on={exact} verify={verify} applied={applied} calls={calls} eligible={eligible} fast={fast} compared={checkedCalls} mismatches={diffs}");
        }

        static void StartExact(bool compare)
        {
            StopExact();
            using (var sha = SHA256.Create())
            using (var file = File.OpenRead(typeof(scrFloor).Assembly.Location))
                if (BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "") != GameHash)
                    throw new Exception("scriptskip exact refused: game DLL differs from audited version");
            started = AccessTools.FieldRefAccess<scrFloor, bool>("didRunStart");
            fields.Clear();
            for (var t = typeof(scrFloor); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                fields.AddRange(t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
            calls = eligible = fast = checkedCalls = diffs = 0;
            verify = compare; exact = true;
            exactHarmony = new Harmony(ExactId);
            try
            {
                exactHarmony.Patch(Targets[0], transpiler: new HarmonyMethod(typeof(ScriptPath), nameof(FloorBody)) { priority = Priority.Last });
                if (!applied) throw new Exception("scriptskip exact refused: unknown transpiler or unsupported body");
            }
            catch { StopExact(); throw; }
        }

        // This is inside the original body: TileCull.ScriptPrefix and every other
        // Harmony prefix/postfix still execute once, in their original order.
        public static IEnumerable<CodeInstruction> FloorBody(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var code = instructions.ToList();
            applied = false;
            var info = Harmony.GetPatchInfo(Targets[0]);
            if (info != null && info.Transpilers.Any(p => p.PatchMethod.DeclaringType != typeof(AllocFix) && p.PatchMethod.DeclaringType != typeof(ScriptPath)))
                return code; // unknown body mutation: retain original behavior
            if (code.Count == 0 || code.Any(c => c.blocks.Count != 0)) return code;
            // Inject the comparison just before each ORIGINAL return. Branches to
            // those returns must enter the comparison, not jump over it.
            var check = AccessTools.Method(typeof(ScriptPath), nameof(VerifyExit));
            for (int i = verify ? code.Count - 1 : -1; i >= 0; i--)
            {
                if (code[i].opcode != OpCodes.Ret) continue;
                // Reuse the return instruction object, retaining its labels.
                code[i].opcode = OpCodes.Ldarg_0;
                code.InsertRange(i + 1, new[] { new CodeInstruction(OpCodes.Call, check), new CodeInstruction(OpCodes.Ret) });
            }
            var original = generator.DefineLabel();
            // UMM's old Harmony exposes List<mscorlib.Label>; the project targets
            // netstandard. Access through IList to avoid facade type mismatch.
            ((System.Collections.IList)AccessTools.Field(typeof(CodeInstruction), "labels").GetValue(code[0])).Add(original);
            code.InsertRange(0, new[] {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ScriptPath), nameof(Guard))),
                new CodeInstruction(OpCodes.Brfalse, original),
                new CodeInstruction(OpCodes.Ret) });
            applied = true;
            return code;
        }

        public static bool Guard(scrFloor floor)
        {
            if (!exact) return false;
            calls++;
            if (!started(floor) || !Application.isPlaying ||
                (floor.specialColorType != TrackColorType.Single && floor.specialColorType != TrackColorType.Stripes) ||
                Animated(floor.floorIcon)) return false;
            eligible++;
            if (!verify) { fast++; return true; }
            // Candidate is the identity operation. Compare its unchanged state
            // with one real execution of the original, at the same frame/time.
            pending = floor;
            before = fields.Select(f => f.GetValue(floor)).ToArray();
            return false;
        }

        internal static bool Animated(FloorIcon icon) => icon == FloorIcon.AnimatedRabbit || icon == FloorIcon.AnimatedSnail ||
            icon == FloorIcon.AnimatedDoubleRabbit || icon == FloorIcon.AnimatedDoubleSnail;

        public static void VerifyExit(scrFloor floor)
        {
            if (!ReferenceEquals(pending, floor)) return;
            pending = null;
            checkedCalls++;
            for (int i = 0; i < fields.Count; i++)
            {
                if (BitEqual(before[i], fields[i].GetValue(floor))) continue;
                diffs++;
                if (diffs <= 5) Main.Entry.Logger.Log("[scriptskip exact] mismatch: " + fields[i].Name);
            }
            before = null;
            if (diffs != 0) exact = false; // automatically fall back; assert still fails
        }

        internal static bool BitEqual(object a, object b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.GetType() != b.GetType()) return false;
            if (a is float af) return BitConverter.SingleToInt32Bits(af) == BitConverter.SingleToInt32Bits((float)b);
            if (a is double ad) return BitConverter.DoubleToInt64Bits(ad) == BitConverter.DoubleToInt64Bits((double)b);
            var t = a.GetType();
            if (!t.IsValueType) return false; // references compared by identity
            if (t.IsPrimitive || t.IsEnum) return a.Equals(b);
            return t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .All(f => BitEqual(f.GetValue(a), f.GetValue(b)));
        }

        static void StopExact()
        {
            exact = verify = applied = false;
            pending = null; before = null;
            exactHarmony?.UnpatchOwner(ExactId); exactHarmony = null;
            fields.Clear();
        }

        internal static void Stop()
        {
            StopExact();
            Array.Clear(skip, 0, skip.Length);
            skipHarmony?.UnpatchOwner(SkipId); skipHarmony = null;
        }
    }
}
#endif
