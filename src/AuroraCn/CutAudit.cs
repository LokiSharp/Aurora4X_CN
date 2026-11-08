using System;
using System.Collections.Generic;
using System.IO;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace AuroraCn
{
    internal static class CutAudit
    {
        internal static int Check(string originalPath, string patchedPath)
        {
            var original = ModuleDefMD.Load(originalPath);
            var patched = ModuleDefMD.Load(patchedPath);
            var failures = new List<string>();
            var notes = new List<string>();
            CheckWrappers(patched, failures);
            CheckRuntimeCuts(patched, failures, notes);
            CheckLiterals(original, patched, failures, notes);
            CheckSelection(patched, notes);
            string report = Path.Combine(Path.GetDirectoryName(originalPath), "localization", "scratch", "cut-audit.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(report));
            var lines = new List<string>();
            lines.AddRange(failures);
            lines.AddRange(notes);
            File.WriteAllLines(report, lines.ToArray());
            Console.WriteLine("cut failures=" + failures.Count + " notes=" + notes.Count);
            Console.WriteLine("report " + report);
            int shown = Math.Min(failures.Count, 40);
            for (int i = 0; i < shown; i++)
                Console.WriteLine(failures[i]);
            return failures.Count == 0 ? 0 : 1;
        }

        private static void CheckWrappers(ModuleDefMD patched, List<string> failures)
        {
            bool slice = false;
            bool sliceFrom = false;
            bool drop = false;
            bool dropCount = false;
            bool trim = false;
            bool shieldCall = false;
            foreach (TypeDef type in patched.GetTypes())
            {
                foreach (MethodDef method in type.Methods)
                {
                    if (!method.HasBody)
                        continue;
                    bool helper = type.Namespace == "AuroraLoc" && type.Name == "TextUtil"
                        && (method.Name == "Slice" || method.Name == "SliceFrom" || method.Name == "Drop" || method.Name == "DropCount");
                    if (type.Namespace == "AuroraLoc" && type.Name == "TextUtil")
                    {
                        if (method.Name == "Slice")
                            slice = true;
                        else if (method.Name == "SliceFrom")
                            sliceFrom = true;
                        else if (method.Name == "Drop")
                            drop = true;
                        else if (method.Name == "DropCount")
                            dropCount = true;
                        else if (method.Name == "TrimSuffix")
                            trim = true;
                    }
                    bool sawRangeBranch = false;
                    foreach (Instruction ins in method.Body.Instructions)
                    {
                        var code = ins.OpCode.Code;
                        if (code == Code.Bge || code == Code.Bge_S || code == Code.Blt || code == Code.Blt_S
                            || code == Code.Ble || code == Code.Ble_S || code == Code.Bgt || code == Code.Bgt_S)
                            sawRangeBranch = true;
                        string rendered = ins.Operand == null ? "" : ins.Operand.ToString();
                        if (rendered.IndexOf("TextUtil::TrimSuffix", StringComparison.Ordinal) >= 0)
                            shieldCall = true;
                        if (helper)
                            continue;
                        if (rendered.IndexOf("System.String::Substring", StringComparison.Ordinal) >= 0
                            || rendered.IndexOf("System.String::Remove", StringComparison.Ordinal) >= 0)
                            failures.Add("fail\traw-cut\t" + type.Name + "::" + method.Name + "\t" + rendered);
                    }
                    if (helper && !sawRangeBranch)
                        failures.Add("fail\twrapper-branch\t" + method.Name);
                }
            }
            if (!slice || !sliceFrom || !drop || !dropCount)
                failures.Add("fail\twrapper-missing\tSlice=" + slice + " SliceFrom=" + sliceFrom + " Drop=" + drop + " DropCount=" + dropCount);
            if (!trim || !shieldCall)
                failures.Add("fail\tshield-suffix\tTrimSuffix=" + trim + " call=" + shieldCall);
            bool pad = false;
            bool tail = false;
            foreach (TypeDef type in patched.GetTypes())
            {
                if (type.Name != "lg")
                    continue;
                foreach (MethodDef method in type.Methods)
                {
                    if (!method.HasBody)
                        continue;
                    if (!Calls(method, "TextUtil::Slice(") && !Calls(method, "TextUtil::SliceFrom("))
                        continue;
                    if (method.Name == "b" && method.Parameters.Count == 2
                        && method.Parameters[0].Type.FullName == "System.String"
                        && method.Parameters[1].Type.FullName == "System.Int32")
                        pad = true;
                    if (method.Name == "a" && method.Parameters.Count == 2
                        && method.Parameters[0].Type.FullName == "System.String"
                        && method.Parameters[1].Type.FullName == "System.Int32"
                        && Calls(method, "TextUtil::SliceFrom("))
                        tail = true;
                }
            }
            if (!pad)
                failures.Add("fail\tpad\tlg::b(string,int) does not call Slice");
            if (!tail)
                failures.Add("fail\ttail\tlg::a(string,int) does not call SliceFrom");
        }

        private static void CheckRuntimeCuts(ModuleDefMD patched, List<string> failures, List<string> notes)
        {
            bool wrapGuarded = false;
            bool wrapSeen = false;
            foreach (TypeDef type in patched.GetTypes())
            {
                if (type.Namespace == "AuroraLoc")
                    continue;
                foreach (MethodDef method in type.Methods)
                {
                    if (!method.HasBody)
                        continue;
                    var list = method.Body.Instructions;
                    bool measures = false;
                    for (int i = 2; i < list.Count; i++)
                    {
                        string rendered = list[i].Operand == null ? "" : list[i].Operand.ToString();
                        if (rendered.IndexOf("TextRenderer::MeasureText", StringComparison.Ordinal) >= 0)
                            measures = true;
                        int width = Ldc(list[i - 1]);
                        if (width < 1 || width > 64)
                            continue;
                        if (list[i - 2].Operand == null || list[i - 2].Operand.ToString().IndexOf("String::get_Length", StringComparison.Ordinal) < 0)
                            continue;
                        string call = i + 1 < list.Count && list[i + 1].Operand != null ? list[i + 1].Operand.ToString() : "";
                        if (!IsStringCut(call))
                            continue;
                        bool parsed = GuardedCut(list, i);
                        bool suffix = call.IndexOf("TrimSuffix", StringComparison.Ordinal) >= 0;
                        string where = type.Name + "::" + method.Name + "\t-" + width + "\t" + call;
                        if (suffix || parsed)
                            notes.Add("note\tguarded-cut\t" + where);
                        else if (width >= 3)
                            failures.Add("fail\tname-cut\t" + where);
                        else
                            notes.Add("note\ttail-cut\t" + where);
                    }
                    if (!measures)
                        continue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (list[i].OpCode.Code != Code.Ldstr || !string.Equals(list[i].Operand as string, " ", StringComparison.Ordinal))
                            continue;
                        string next = i + 1 < list.Count && list[i + 1].Operand != null ? list[i + 1].Operand.ToString() : "";
                        if (next.IndexOf("LastIndexOf", StringComparison.Ordinal) < 0)
                            continue;
                        wrapSeen = true;
                        int end = Math.Min(list.Count, i + 16);
                        bool compared = false;
                        bool left = false;
                        for (int k = i; k < end; k++)
                        {
                            var code = list[k].OpCode.Code;
                            if (code == Code.Bge || code == Code.Bge_S || code == Code.Blt || code == Code.Blt_S)
                                compared = true;
                            if (code == Code.Leave || code == Code.Leave_S)
                                left = true;
                        }
                        if (compared && left)
                            wrapGuarded = true;
                    }
                }
            }
            if (wrapSeen && !wrapGuarded)
                failures.Add("fail\twrap\tLastIndexOf space is not guarded");
            if (!wrapSeen)
                failures.Add("fail\twrap\tno space-wrap site");
        }

        private static void CheckLiterals(ModuleDefMD original, ModuleDefMD patched, List<string> failures, List<string> notes)
        {
            var patchedTypes = new Dictionary<string, TypeDef>();
            foreach (TypeDef type in patched.GetTypes())
            {
                if (type.Namespace == "AuroraLoc")
                    continue;
                patchedTypes[type.FullName] = type;
            }
            int skipped = 0;
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
                    if (left.Body.Instructions.Count != right.Body.Instructions.Count)
                    {
                        skipped++;
                        continue;
                    }
                    var li = left.Body.Instructions;
                    var ri = right.Body.Instructions;
                    for (int i = 0; i < li.Count; i++)
                    {
                        if (li[i].OpCode.Code != Code.Ldstr || ri[i].OpCode.Code != Code.Ldstr)
                            continue;
                        string en = li[i].Operand as string;
                        string zh = ri[i].Operand as string;
                        if (en == null || zh == null || en == zh)
                            continue;
                        int need = RequiredWidth(li, i);
                        if (need < 0)
                            continue;
                        if (zh.Length < need)
                            failures.Add("fail\tliteral-cut\t" + type.Name + "::" + left.Name + "\tneed " + need + "\t" + en + "\t=>\t" + zh);
                    }
                }
            }
            notes.Add("note\tskipped-methods\t" + skipped);
        }

        private static void CheckSelection(ModuleDefMD patched, List<string> notes)
        {
            int unguarded = 0;
            int shown = 0;
            foreach (TypeDef type in patched.GetTypes())
            {
                if (type.Namespace == "AuroraLoc")
                    continue;
                foreach (MethodDef method in type.Methods)
                {
                    if (!method.HasBody)
                        continue;
                    var list = method.Body.Instructions;
                    for (int i = 0; i < list.Count; i++)
                    {
                        string rendered = list[i].Operand == null ? "" : list[i].Operand.ToString();
                        if (rendered.IndexOf("SelectedListViewItemCollection::get_Item", StringComparison.Ordinal) < 0
                            && rendered.IndexOf("SelectedIndexCollection::get_Item", StringComparison.Ordinal) < 0)
                            continue;
                        if (Lookback(list, i, "get_Count"))
                            continue;
                        unguarded++;
                        if (shown < 15)
                        {
                            notes.Add("note\tselection\t" + type.Name + "::" + method.Name);
                            shown++;
                        }
                    }
                }
            }
            notes.Add("note\tselection-unguarded\t" + unguarded);
        }

        private static int RequiredWidth(IList<Instruction> ins, int index)
        {
            int lengthAt = -1;
            int end = Math.Min(ins.Count, index + 12);
            for (int i = index + 1; i < end; i++)
            {
                var code = ins[i].OpCode.Code;
                if (code == Code.Ldstr || code == Code.Stloc || code == Code.Stloc_S || code == Code.Ret
                    || code == Code.Br || code == Code.Br_S || code == Code.Leave || code == Code.Leave_S
                    || code == Code.Switch || code == Code.Throw)
                    return -1;
                if (code == Code.Ldloc || code == Code.Ldloc_S || code == Code.Ldloc_0 || code == Code.Ldloc_1
                    || code == Code.Ldloc_2 || code == Code.Ldloc_3 || code == Code.Ldarg || code == Code.Ldarg_S
                    || code == Code.Ldarg_0 || code == Code.Ldarg_1 || code == Code.Ldarg_2 || code == Code.Ldarg_3
                    || code == Code.Ldfld || code == Code.Ldsfld)
                    return -1;
                if (code != Code.Call && code != Code.Callvirt)
                    continue;
                string rendered = ins[i].Operand == null ? "" : ins[i].Operand.ToString();
                if (rendered.IndexOf("String::get_Length", StringComparison.Ordinal) >= 0)
                {
                    lengthAt = i;
                    continue;
                }
                if (rendered.IndexOf("String::get_Chars", StringComparison.Ordinal) >= 0)
                {
                    int at = i > 0 ? Ldc(ins[i - 1]) : int.MinValue;
                    return at >= 0 ? at + 1 : -1;
                }
                if (!IsStringCut(rendered))
                    return -1;
                if (lengthAt >= 0)
                {
                    for (int k = lengthAt + 1; k < i; k++)
                    {
                        if (ins[k].OpCode.Code == Code.Sub)
                            return Ldc(ins[k - 1]);
                    }
                    return -1;
                }
                int length = Ldc(ins[i - 1]);
                int start = i > 1 ? Ldc(ins[i - 2]) : int.MinValue;
                if (start >= 0 && length > 0)
                    return start + length;
                if (length > 0)
                    return length;
                return -1;
            }
            return -1;
        }

        private static bool IsStringCut(string rendered)
        {
            return rendered.IndexOf("String::Substring", StringComparison.Ordinal) >= 0
                || rendered.IndexOf("String::Remove", StringComparison.Ordinal) >= 0
                || rendered.IndexOf("lg::b(System.String,System.Int32)", StringComparison.Ordinal) >= 0
                || rendered.IndexOf("TextUtil::Slice", StringComparison.Ordinal) >= 0
                || rendered.IndexOf("TextUtil::Drop", StringComparison.Ordinal) >= 0
                || rendered.IndexOf("TextUtil::TrimSuffix", StringComparison.Ordinal) >= 0;
        }

        private static bool GuardedCut(IList<Instruction> list, int index)
        {
            int start = Math.Max(0, index - 24);
            for (int i = index - 1; i >= start; i--)
            {
                var code = list[i].OpCode.Code;
                if (code == Code.Br || code == Code.Br_S || code == Code.Leave || code == Code.Leave_S
                    || code == Code.Ret || code == Code.Throw || code == Code.Switch)
                    return false;
                if (list[i].Operand == null)
                    continue;
                string rendered = list[i].Operand.ToString();
                if (rendered.IndexOf("TryParse", StringComparison.Ordinal) >= 0
                    || rendered.IndexOf("EndsWith", StringComparison.Ordinal) >= 0)
                    return true;
            }
            return false;
        }

        private static bool Lookback(IList<Instruction> list, int index, string token)
        {
            int start = Math.Max(0, index - 24);
            for (int i = start; i < index; i++)
            {
                if (list[i].Operand != null && list[i].Operand.ToString().IndexOf(token, StringComparison.Ordinal) >= 0)
                    return true;
            }
            return false;
        }

        private static bool Calls(MethodDef method, string token)
        {
            foreach (Instruction ins in method.Body.Instructions)
            {
                if (ins.Operand != null && ins.Operand.ToString().IndexOf(token, StringComparison.Ordinal) >= 0)
                    return true;
            }
            return false;
        }

        private static int Ldc(Instruction ins)
        {
            if (ins == null)
                return int.MinValue;
            switch (ins.OpCode.Code)
            {
                case Code.Ldc_I4_M1: return -1;
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
                    return ins.Operand == null ? int.MinValue : Convert.ToInt32(ins.Operand);
                default:
                    return int.MinValue;
            }
        }
    }
}
