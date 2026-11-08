using System;
using System.Collections.Generic;
using System.IO;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace AuroraCn
{
    internal static class SwitchAudit
    {
        internal static int Check(string originalPath, string patchedPath)
        {
            var patched = ModuleDefMD.Load(patchedPath);
            var original = ModuleDefMD.Load(originalPath);
            var failures = new List<string>();
            foreach (TypeDef type in patched.GetTypes())
            {
                if (type.Namespace == "AuroraLoc")
                    continue;
                foreach (MethodDef method in type.Methods)
                {
                    if (!method.HasBody)
                        continue;
                    CheckLengthSwitch(method, failures);
                    CheckComparedValue(method, failures);
                }
            }
            string locDir = Path.Combine(Path.GetDirectoryName(originalPath), "localization");
            CheckKeptCases(original, patched, TranslationTables.Load(locDir), failures);
            string report = Path.Combine(Path.GetDirectoryName(originalPath), "localization", "scratch", "switch-audit.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(report));
            File.WriteAllLines(report, failures.ToArray());
            Console.WriteLine("switch failures=" + failures.Count);
            Console.WriteLine("report " + report);
            int shown = Math.Min(failures.Count, 40);
            for (int i = 0; i < shown; i++)
                Console.WriteLine(failures[i]);
            return failures.Count == 0 ? 0 : 1;
        }

        private static void CheckLengthSwitch(MethodDef method, List<string> failures)
        {
            var list = method.Body.Instructions;
            for (int i = 0; i < list.Count; i++)
            {
                if (!IsLength(list[i]) || !HasLengthBranch(list, i))
                    continue;
                int end;
                var cases = CaseStrings(list, i, out end);
                if (cases.Count == 0)
                    continue;
                var accepted = AcceptedLengths(list, i, end);
                if (accepted.Count == 0)
                    continue;
                bool ascii = HasAsciiCharTest(list, i);
                foreach (string text in cases)
                {
                    if (!accepted.Contains(text.Length))
                        failures.Add("fail\tlength\t" + Name(method) + "\t" + text);
                    else if (ascii && HasNonAscii(text))
                        failures.Add("fail\tascii\t" + Name(method) + "\t" + text);
                }
            }
        }

        private static void CheckComparedValue(MethodDef method, List<string> failures)
        {
            var list = method.Body.Instructions;
            for (int i = 1; i < list.Count - 1; i++)
            {
                if (list[i].OpCode.Code != Code.Ldstr || !IsEquality(list[i + 1]) || !IsLoad(list[i - 1]))
                    continue;
                TypeSig type = ValueType(method, list[i - 1]);
                if (type != null && !IsString(type))
                    failures.Add("fail\tvalue\t" + Name(method) + "\t" + list[i].Operand);
            }
            for (int i = 1; i < list.Count - 4; i++)
            {
                if (!IsBrfalse(list[i]) || !IsLoad(list[i - 1]) || !IsLoad(list[i + 1]))
                    continue;
                if (list[i + 2].OpCode.Code != Code.Ldstr || !IsEquality(list[i + 3]))
                    continue;
                if (CountChain(list, i + 1) < 2)
                    continue;
                TypeSig gate = ValueType(method, list[i - 1]);
                if (gate != null && !IsString(gate))
                    failures.Add("fail\tgate\t" + Name(method) + "\t" + list[i + 2].Operand);
            }
        }

        private static void CheckKeptCases(ModuleDefMD original, ModuleDefMD patched, TranslationTables tables, List<string> failures)
        {
            var patchedTypes = new Dictionary<string, TypeDef>();
            foreach (TypeDef type in patched.GetTypes())
            {
                if (type.Namespace != "AuroraLoc")
                    patchedTypes[type.FullName] = type;
            }
            foreach (TypeDef type in original.GetTypes())
            {
                TypeDef other;
                if (!patchedTypes.TryGetValue(type.FullName, out other))
                    continue;
                int count = Math.Min(type.Methods.Count, other.Methods.Count);
                for (int m = 0; m < count; m++)
                {
                    MethodDef left = type.Methods[m];
                    MethodDef right = other.Methods[m];
                    if (!left.HasBody || !right.HasBody)
                        continue;
                    var wanted = new HashSet<string>();
                    var list = left.Body.Instructions;
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (!IsLength(list[i]) || !HasLengthBranch(list, i) || !HasAsciiCharTest(list, i))
                            continue;
                        int end;
                        foreach (string text in CaseStrings(list, i, out end))
                            wanted.Add(text);
                    }
                    if (wanted.Count == 0)
                        continue;
                    var found = new HashSet<string>();
                    foreach (Instruction ins in right.Body.Instructions)
                    {
                        if (ins.OpCode.Code == Code.Ldstr && ins.Operand is string)
                            found.Add((string)ins.Operand);
                    }
                    foreach (string text in wanted)
                    {
                        string expected = text;
                        string chinese;
                        if (tables.Display.TryGetValue(text, out chinese) || tables.Ui.TryGetValue(text, out chinese))
                            expected = chinese;
                        if (!found.Contains(expected))
                            failures.Add("fail\tdropped\t" + Name(left) + "\t" + text + "\t=>\t" + expected);
                    }
                }
            }
        }

        private static int CountChain(IList<Instruction> list, int start)
        {
            int count = 0;
            for (int i = start; i + 3 < list.Count; i += 4)
            {
                if (!IsLoad(list[i]) || list[i + 1].OpCode.Code != Code.Ldstr || !IsEquality(list[i + 2]))
                    break;
                if (list[i + 3].OpCode.Code != Code.Brtrue && list[i + 3].OpCode.Code != Code.Brtrue_S)
                    break;
                if (count > 0 && !SameLoad(list[start], list[i]))
                    break;
                count++;
            }
            return count;
        }

        private static List<string> CaseStrings(IList<Instruction> list, int lengthCall, out int end)
        {
            int start = Math.Max(0, lengthCall - 3);
            end = list.Count;
            var cases = new List<string>();
            for (int i = start; i < list.Count - 2 && i < end; i++)
            {
                if (list[i].OpCode.Code != Code.Ldstr || !(list[i].Operand is string))
                    continue;
                if (!IsEquality(list[i + 1]))
                    continue;
                if (list[i + 2].OpCode.Code != Code.Brtrue && list[i + 2].OpCode.Code != Code.Brtrue_S)
                    continue;
                var target = list[i + 2].Operand as Instruction;
                if (target == null)
                    continue;
                int targetAt = list.IndexOf(target);
                if (targetAt <= i)
                    continue;
                if (targetAt < end)
                    end = targetAt;
                if (i < end)
                    cases.Add((string)list[i].Operand);
            }
            return cases;
        }

        private static HashSet<int> AcceptedLengths(IList<Instruction> list, int lengthCall, int end)
        {
            var accepted = new HashSet<int>();
            if (lengthCall + 1 >= list.Count)
                return accepted;
            int lengthLocal = LocalIndex(list[lengthCall + 1]);
            if (lengthLocal < 0)
                return accepted;
            int limit = Math.Min(end, lengthCall + 45);
            for (int i = lengthCall + 1; i < limit - 2; i++)
            {
                if (LocalIndex(list[i]) != lengthLocal)
                    continue;
                int constant = LoadConstant(list[i + 1]);
                if (constant == int.MinValue)
                    continue;
                var code = list[i + 2].OpCode.Code;
                if (code == Code.Beq || code == Code.Beq_S)
                    accepted.Add(constant);
                if (code == Code.Sub && i + 3 < list.Count && list[i + 3].OpCode.Code == Code.Switch)
                {
                    var targets = list[i + 3].Operand as Instruction[];
                    if (targets == null)
                        continue;
                    for (int n = 0; n < targets.Length; n++)
                        accepted.Add(constant + n);
                }
            }
            return accepted;
        }

        private static TypeSig ValueType(MethodDef method, Instruction ins)
        {
            int local = LocalIndex(ins);
            if (local >= 0)
            {
                if (local >= method.Body.Variables.Count)
                    return null;
                return method.Body.Variables[local].Type;
            }
            int arg = ArgIndex(ins);
            if (arg < 0 || arg >= method.Parameters.Count)
                return null;
            return method.Parameters[arg].Type;
        }

        private static bool IsString(TypeSig type)
        {
            return type.FullName == "System.String";
        }

        private static bool HasNonAscii(string text)
        {
            foreach (char c in text)
            {
                if (c > 127)
                    return true;
            }
            return false;
        }

        private static bool IsLength(Instruction ins)
        {
            return ins.Operand != null && ins.Operand.ToString().IndexOf("String::get_Length", StringComparison.Ordinal) >= 0;
        }

        private static bool IsEquality(Instruction ins)
        {
            return ins.Operand != null && ins.Operand.ToString().IndexOf("op_Equality", StringComparison.Ordinal) >= 0;
        }

        private static bool HasLengthBranch(IList<Instruction> list, int index)
        {
            int end = Math.Min(list.Count, index + 10);
            for (int i = index; i < end; i++)
            {
                var code = list[i].OpCode.Code;
                if (code == Code.Beq || code == Code.Beq_S || code == Code.Switch)
                    return true;
            }
            return false;
        }

        private static bool HasAsciiCharTest(IList<Instruction> list, int index)
        {
            int end = Math.Min(list.Count, index + 80);
            for (int i = index; i < end; i++)
            {
                if (list[i].Operand == null || list[i].Operand.ToString().IndexOf("String::get_Chars", StringComparison.Ordinal) < 0)
                    continue;
                for (int k = i + 1; k < Math.Min(list.Count, i + 6); k++)
                {
                    int value = LoadConstant(list[k]);
                    if (value >= 32 && value < 128)
                        return true;
                }
            }
            return false;
        }

        private static bool IsBrfalse(Instruction ins)
        {
            return ins.OpCode.Code == Code.Brfalse || ins.OpCode.Code == Code.Brfalse_S;
        }

        private static bool IsLoad(Instruction ins)
        {
            switch (ins.OpCode.Code)
            {
                case Code.Ldarg_0:
                case Code.Ldarg_1:
                case Code.Ldarg_2:
                case Code.Ldarg_3:
                case Code.Ldarg:
                case Code.Ldarg_S:
                case Code.Ldloc_0:
                case Code.Ldloc_1:
                case Code.Ldloc_2:
                case Code.Ldloc_3:
                case Code.Ldloc:
                case Code.Ldloc_S:
                    return true;
                default:
                    return false;
            }
        }

        private static bool SameLoad(Instruction left, Instruction right)
        {
            if (left.OpCode.Code != right.OpCode.Code)
                return false;
            if (left.Operand == null || right.Operand == null)
                return left.Operand == null && right.Operand == null;
            return left.Operand.Equals(right.Operand);
        }

        private static int LocalIndex(Instruction ins)
        {
            switch (ins.OpCode.Code)
            {
                case Code.Ldloc_0:
                case Code.Stloc_0:
                    return 0;
                case Code.Ldloc_1:
                case Code.Stloc_1:
                    return 1;
                case Code.Ldloc_2:
                case Code.Stloc_2:
                    return 2;
                case Code.Ldloc_3:
                case Code.Stloc_3:
                    return 3;
                case Code.Ldloc:
                case Code.Ldloc_S:
                case Code.Stloc:
                case Code.Stloc_S:
                    return ((Local)ins.Operand).Index;
                default:
                    return -1;
            }
        }

        private static int ArgIndex(Instruction ins)
        {
            switch (ins.OpCode.Code)
            {
                case Code.Ldarg_0:
                    return 0;
                case Code.Ldarg_1:
                    return 1;
                case Code.Ldarg_2:
                    return 2;
                case Code.Ldarg_3:
                    return 3;
                case Code.Ldarg:
                case Code.Ldarg_S:
                    return ((Parameter)ins.Operand).Index;
                default:
                    return -1;
            }
        }

        private static int LoadConstant(Instruction ins)
        {
            switch (ins.OpCode.Code)
            {
                case Code.Ldc_I4_0: return 0;
                case Code.Ldc_I4_1: return 1;
                case Code.Ldc_I4_2: return 2;
                case Code.Ldc_I4_3: return 3;
                case Code.Ldc_I4_4: return 4;
                case Code.Ldc_I4_5: return 5;
                case Code.Ldc_I4_6: return 6;
                case Code.Ldc_I4_7: return 7;
                case Code.Ldc_I4_8: return 8;
                case Code.Ldc_I4_S:
                case Code.Ldc_I4:
                    return Convert.ToInt32(ins.Operand);
                default:
                    return int.MinValue;
            }
        }

        private static string Name(MethodDef method)
        {
            return method.DeclaringType.Name + "::" + method.Name;
        }
    }
}
