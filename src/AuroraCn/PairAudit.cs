using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace AuroraCn
{
    internal static class PairAudit
    {
        private static readonly Regex Slot = new Regex(@"\{[0-9]+\}", RegexOptions.Compiled);
        private static readonly Regex Unit = new Regex(
            @"(?<![A-Za-z])(km/s|k km|m km|b km|per second|per sec|tons|ton|atm|km|cm)(?![A-Za-z])",
            RegexOptions.Compiled);

        internal static int Check(string originalPath, string patchedPath)
        {
            string locDir = Path.Combine(Path.GetDirectoryName(originalPath), "localization");
            var original = ModuleDefMD.Load(originalPath);
            var patched = ModuleDefMD.Load(patchedPath);
            var failures = new List<string>();
            CheckUnits(locDir, failures);
            CheckPairs(original, patched, LoadDataText(locDir), LiteralStores(original), failures);
            string report = Path.Combine(locDir, "scratch", "pair-audit.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(report));
            File.WriteAllLines(report, failures.ToArray());
            Console.WriteLine("pair failures=" + failures.Count);
            Console.WriteLine("report " + report);
            int shown = Math.Min(failures.Count, 40);
            for (int i = 0; i < shown; i++)
                Console.WriteLine(failures[i]);
            return failures.Count == 0 ? 0 : 1;
        }

        private static void CheckUnits(string locDir, List<string> failures)
        {
            CheckUnitFile(Path.Combine(locDir, "exe", "ui.tsv"), 0, 1, 2, failures);
            CheckUnitFile(Path.Combine(locDir, "exe", "display.tsv"), 0, 1, 2, failures);
            string fields = Path.Combine(locDir, "db", "fields");
            if (!Directory.Exists(fields))
                return;
            foreach (string file in Directory.GetFiles(fields, "*.tsv"))
                CheckUnitFile(file, 2, 3, 4, failures);
        }

        private static void CheckUnitFile(string path, int enIndex, int zhIndex, int reasonIndex, List<string> failures)
        {
            if (!File.Exists(path))
                return;
            foreach (string line in File.ReadAllLines(path))
            {
                if (line.Length == 0 || line.StartsWith("english\t") || line.StartsWith("table\t"))
                    continue;
                string[] parts = line.Split('\t');
                if (parts.Length <= reasonIndex)
                    continue;
                if (parts[reasonIndex].StartsWith("不翻译"))
                    continue;
                string en = Unesc(parts[enIndex]);
                string zh = Unesc(parts[zhIndex]);
                if (zh.Length == 0 || zh == en)
                    continue;
                var missing = new HashSet<string>();
                foreach (Match match in Unit.Matches(en))
                {
                    if (zh.IndexOf(match.Value, StringComparison.Ordinal) < 0)
                        missing.Add(match.Value);
                }
                if (missing.Count > 0)
                    failures.Add("fail\tunit\t" + string.Join(",", missing) + "\t" + en + "\t=>\t" + zh);
            }
        }

        private static Dictionary<string, HashSet<string>> LoadDataText(string locDir)
        {
            var data = new Dictionary<string, HashSet<string>>();
            string fields = Path.Combine(locDir, "db", "fields");
            if (!Directory.Exists(fields))
                return data;
            foreach (string file in Directory.GetFiles(fields, "*.tsv"))
            {
                foreach (string line in File.ReadAllLines(file))
                {
                    if (line.Length == 0 || line.StartsWith("table\t"))
                        continue;
                    string[] parts = line.Split('\t');
                    if (parts.Length < 5)
                        continue;
                    string en = Unesc(parts[2]);
                    string zh = Unesc(parts[3]);
                    string shown = parts[4].StartsWith("不翻译") || zh.Length == 0 || zh == en ? en : zh;
                    HashSet<string> values;
                    if (!data.TryGetValue(en, out values))
                    {
                        values = new HashSet<string>();
                        data[en] = values;
                    }
                    values.Add(shown);
                }
            }
            return data;
        }

        private static Dictionary<string, HashSet<string>> LiteralStores(ModuleDefMD module)
        {
            var stores = new Dictionary<string, HashSet<string>>();
            foreach (TypeDef type in module.GetTypes())
            {
                foreach (MethodDef method in type.Methods)
                {
                    if (!method.HasBody)
                        continue;
                    var list = method.Body.Instructions;
                    for (int i = 0; i < list.Count - 1; i++)
                    {
                        if (list[i].OpCode.Code != Code.Ldstr || !IsStringField(list[i + 1]))
                            continue;
                        var field = (IField)list[i + 1].Operand;
                        HashSet<string> texts;
                        if (!stores.TryGetValue(field.FullName, out texts))
                        {
                            texts = new HashSet<string>();
                            stores[field.FullName] = texts;
                        }
                        texts.Add((string)list[i].Operand);
                    }
                }
            }
            return stores;
        }

        private static void CheckPairs(ModuleDefMD original, ModuleDefMD patched, Dictionary<string, HashSet<string>> data, Dictionary<string, HashSet<string>> stores, List<string> failures)
        {
            var patchedTypes = new Dictionary<string, TypeDef>();
            foreach (TypeDef type in patched.GetTypes())
            {
                if (type.Namespace == "AuroraLoc")
                    continue;
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
                    if (left.Body.Instructions.Count != right.Body.Instructions.Count)
                        continue;
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
                        string where = type.Name + "::" + left.Name;
                        if (Slot.Matches(en).Count != Slot.Matches(zh).Count)
                            failures.Add("fail\tformat\t" + where + "\t" + en + "\t=>\t" + zh);
                        string call = FirstUse(li, i);
                        if (call == null)
                            continue;
                        if (call.IndexOf("String::Split", StringComparison.Ordinal) >= 0)
                            failures.Add("fail\tsplit\t" + where + "\t" + en + "\t=>\t" + zh);
                        if (IsParse(call) && !SameNumber(en, zh))
                            failures.Add("fail\tparse\t" + where + "\t" + en + "\t=>\t" + zh);
                        if (IsLengthSwitch(li, i) && en.Length != zh.Length)
                            failures.Add("fail\tlength-switch\t" + where + "\t" + en.Length + "->" + zh.Length + "\t" + en + "\t=>\t" + zh);
                        if (!DataCompare(li, i) || RoundTrip(li, i, stores, en))
                            continue;
                        HashSet<string> values;
                        if (!data.TryGetValue(en, out values))
                        {
                            failures.Add("fail\tcompare-data\t" + where + "\t" + en + "\t=>\t" + zh);
                            continue;
                        }
                        if (values.Count != 1 || !values.Contains(zh))
                            failures.Add("fail\tcompare-db\t" + where + "\t" + en + "\t=>\t" + zh + "\tdb=" + string.Join("|", values));
                    }
                }
            }
        }

        private static bool RoundTrip(IList<Instruction> ins, int index, Dictionary<string, HashSet<string>> stores, string en)
        {
            int start = Math.Max(0, index - 4);
            int end = Math.Min(ins.Count, index + 4);
            for (int i = start; i < end; i++)
            {
                if (!IsStringField(ins[i]))
                    continue;
                var field = (IField)ins[i].Operand;
                if (field.Name == "il" && field.DeclaringType != null && field.DeclaringType.Name == "a2")
                    return true;
                HashSet<string> texts;
                if (stores.TryGetValue(field.FullName, out texts) && texts.Contains(en))
                    return true;
            }
            return false;
        }

        private static bool DataCompare(IList<Instruction> ins, int index)
        {
            return CompareAfter(ins, index, false) || CompareBefore(ins, index);
        }

        private static bool CompareAfter(IList<Instruction> ins, int index, bool dataAlready)
        {
            bool data = dataAlready;
            int end = Math.Min(ins.Count, index + 6);
            for (int i = index + 1; i < end; i++)
            {
                var code = ins[i].OpCode.Code;
                if (code == Code.Ldstr || code == Code.Stloc || code == Code.Stloc_S || IsStop(code))
                    return false;
                if (IsStringField(ins[i]))
                {
                    data = true;
                    continue;
                }
                if (code != Code.Call && code != Code.Callvirt)
                    continue;
                string rendered = ins[i].Operand == null ? "" : ins[i].Operand.ToString();
                if (IsCompare(rendered))
                    return data;
                if (rendered.IndexOf("get_Item(", StringComparison.Ordinal) >= 0
                    || rendered.IndexOf("get_Name(", StringComparison.Ordinal) >= 0)
                {
                    data = true;
                    continue;
                }
                return false;
            }
            return false;
        }

        private static bool CompareBefore(IList<Instruction> ins, int index)
        {
            bool data = false;
            int start = Math.Max(0, index - 3);
            for (int i = index - 1; i >= start; i--)
            {
                var code = ins[i].OpCode.Code;
                if (code == Code.Ldstr || IsStop(code))
                    break;
                if (IsStringField(ins[i]))
                    data = true;
                if (code == Code.Call || code == Code.Callvirt)
                {
                    string rendered = ins[i].Operand == null ? "" : ins[i].Operand.ToString();
                    if (rendered.IndexOf("get_Item(", StringComparison.Ordinal) >= 0
                        || rendered.IndexOf("get_Name(", StringComparison.Ordinal) >= 0)
                        data = true;
                }
            }
            if (!data)
                return false;
            return CompareAfter(ins, index, true);
        }

        private static bool IsLengthSwitch(IList<Instruction> ins, int index)
        {
            bool length = false;
            bool sub = false;
            int end = Math.Min(ins.Count, index + 16);
            for (int i = index + 1; i < end; i++)
            {
                var code = ins[i].OpCode.Code;
                if (code == Code.Ldstr || code == Code.Stloc || code == Code.Stloc_S || IsStop(code))
                    return false;
                if (code == Code.Sub)
                    sub = length;
                if (code == Code.Switch)
                    return length && sub;
                if (code != Code.Call && code != Code.Callvirt)
                    continue;
                string rendered = ins[i].Operand == null ? "" : ins[i].Operand.ToString();
                if (rendered.IndexOf("String::get_Length", StringComparison.Ordinal) >= 0)
                    length = true;
                else
                    return false;
            }
            return false;
        }

        private static string FirstUse(IList<Instruction> ins, int index)
        {
            int end = Math.Min(ins.Count, index + 8);
            for (int i = index + 1; i < end; i++)
            {
                var code = ins[i].OpCode.Code;
                if (code == Code.Ldstr || code == Code.Ldloc || code == Code.Ldloc_S || code == Code.Ldloc_0
                    || code == Code.Ldloc_1 || code == Code.Ldloc_2 || code == Code.Ldloc_3 || code == Code.Ldc_I4
                    || code == Code.Ldc_I4_S || code == Code.Ldc_I4_0 || code == Code.Ldc_I4_1 || IsStop(code))
                    return null;
                if (code == Code.Call || code == Code.Callvirt)
                    return ins[i].Operand == null ? "" : ins[i].Operand.ToString();
            }
            return null;
        }

        private static bool IsStringField(Instruction ins)
        {
            var code = ins.OpCode.Code;
            if (code != Code.Ldfld && code != Code.Ldsfld && code != Code.Stfld && code != Code.Stsfld)
                return false;
            var field = ins.Operand as IField;
            return field != null && field.FieldSig != null && field.FieldSig.Type != null
                && field.FieldSig.Type.FullName == "System.String";
        }

        private static bool IsCompare(string rendered)
        {
            return rendered.IndexOf("op_Equality", StringComparison.Ordinal) >= 0
                || rendered.IndexOf("op_Inequality", StringComparison.Ordinal) >= 0
                || rendered.IndexOf("::Equals(", StringComparison.Ordinal) >= 0
                || rendered.IndexOf("::StartsWith(", StringComparison.Ordinal) >= 0
                || rendered.IndexOf("::EndsWith(", StringComparison.Ordinal) >= 0
                || rendered.IndexOf("::Contains(", StringComparison.Ordinal) >= 0
                || rendered.IndexOf("String::Compare", StringComparison.Ordinal) >= 0
                || rendered.IndexOf("::IndexOf(", StringComparison.Ordinal) >= 0
                || rendered.IndexOf("::LastIndexOf(", StringComparison.Ordinal) >= 0
                || rendered.IndexOf("::Replace(", StringComparison.Ordinal) >= 0;
        }

        private static bool IsParse(string rendered)
        {
            return rendered.IndexOf("::Parse(", StringComparison.Ordinal) >= 0
                || rendered.IndexOf("::TryParse(", StringComparison.Ordinal) >= 0
                || rendered.IndexOf("Convert::ToInt32", StringComparison.Ordinal) >= 0
                || rendered.IndexOf("Convert::ToDouble", StringComparison.Ordinal) >= 0
                || rendered.IndexOf("Convert::ToDecimal", StringComparison.Ordinal) >= 0;
        }

        private static bool SameNumber(string en, string zh)
        {
            double ignored;
            return double.TryParse(en, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out ignored)
                && double.TryParse(zh, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out ignored);
        }

        private static bool IsStop(Code code)
        {
            return code == Code.Br || code == Code.Br_S || code == Code.Leave || code == Code.Leave_S
                || code == Code.Ret || code == Code.Throw || code == Code.Switch;
        }

        private static string Unesc(string text)
        {
            var chars = new System.Text.StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c != '\\' || i + 1 >= text.Length)
                {
                    chars.Append(c);
                    continue;
                }
                char next = text[++i];
                if (next == 'n')
                    chars.Append('\n');
                else if (next == 'r')
                    chars.Append('\r');
                else if (next == 't')
                    chars.Append('\t');
                else if (next == '\\')
                    chars.Append('\\');
                else
                {
                    chars.Append('\\');
                    chars.Append(next);
                }
            }
            return chars.ToString();
        }
    }
}
