using System;
using System.Collections.Generic;
using System.IO;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace AuroraCn
{
    internal static class StringAudit
    {
        internal static int Check(string originalPath, string patchedPath)
        {
            var original = ModuleDefMD.Load(originalPath);
            var patched = ModuleDefMD.Load(patchedPath);
            var patchedTypes = new Dictionary<string, TypeDef>();
            foreach (TypeDef type in patched.GetTypes())
            {
                if (type.Namespace == "AuroraLoc")
                    continue;
                patchedTypes[type.FullName] = type;
            }
            var failures = new List<string>();
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
                        string en = (string)li[i].Operand;
                        string zh = (string)ri[i].Operand;
                        if (en == zh || AllowedRename(en, zh))
                            continue;
                        string reason = Risk(li, i, en, zh);
                        if (reason != null)
                            failures.Add(reason + "\t" + en + "\t=>\t" + zh);
                    }
                }
            }
            string report = Path.Combine(Path.GetDirectoryName(originalPath), "localization", "scratch", "string-audit.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(report));
            File.WriteAllLines(report, failures.ToArray());
            Console.WriteLine("audit failures=" + failures.Count);
            Console.WriteLine("report " + report);
            int shown = Math.Min(failures.Count, 40);
            for (int i = 0; i < shown; i++)
                Console.WriteLine(failures[i]);
            return failures.Count == 0 ? 0 : 1;
        }

        private static bool AllowedRename(string en, string zh)
        {
            return en == "Data Source=AuroraDB.db;Version=3;New=False;Compress=True;" && zh.IndexOf("AuroraCN.db", StringComparison.Ordinal) >= 0
                || en == "AuroraDB.db" && zh == "AuroraCN.db"
                || en == "AuroraDBSaveBackup.db" && zh == "AuroraCNSaveBackup.db"
                || en == "AuroraDBPreviousSaveBackup.db" && zh == "AuroraCNPreviousSaveBackup.db";
        }

        private static string Risk(IList<Instruction> ins, int index, string en, string zh)
        {
            if (en == "Flags" || en == "Races" || en == "Icons" || en == "Medals" || en == "ShipIcons" || en == "StationIcons")
                return "folder";
            if (en.EndsWith(".png") || en.EndsWith(".bmp") || en.EndsWith(".svg") || en.EndsWith(".jpg") || en.EndsWith(".db") || en.EndsWith(".wav"))
                return "file";
            if (LooksLikeSql(en))
                return "sql";
            string call = FirstCall(ins, index);
            if (call == null)
                return null;
            if (call.IndexOf("get_Item(", StringComparison.Ordinal) >= 0)
                return "column";
            if (call.IndexOf("Path::", StringComparison.Ordinal) >= 0)
                return "path";
            if (call.IndexOf("set_Name(", StringComparison.Ordinal) >= 0)
                return "control-name";
            if (call.IndexOf("GetObject", StringComparison.Ordinal) >= 0)
                return "resource";
            if (call.IndexOf("Substring", StringComparison.Ordinal) >= 0 || call.IndexOf("::Remove(", StringComparison.Ordinal) >= 0)
                return "slice";
            return null;
        }

        private static string FirstCall(IList<Instruction> ins, int index)
        {
            int end = Math.Min(ins.Count, index + 6);
            for (int i = index + 1; i < end; i++)
            {
                var code = ins[i].OpCode.Code;
                if (code == Code.Call || code == Code.Callvirt || code == Code.Newobj)
                    return ins[i].ToString();
                if (code == Code.Ldstr || code == Code.Ldloc || code == Code.Ldloc_S || code == Code.Ldloc_0
                    || code == Code.Ldloc_1 || code == Code.Ldloc_2 || code == Code.Ldloc_3 || code == Code.Ldc_I4
                    || code == Code.Ldc_I4_S || code == Code.Ldc_I4_0 || code == Code.Ldc_I4_1)
                    return null;
            }
            return null;
        }

        private static bool LooksLikeSql(string text)
        {
            string lower = text.Trim().ToLowerInvariant();
            return lower.StartsWith("insert ") || lower.StartsWith("delete from")
                || lower.StartsWith("order by") || lower.StartsWith("group by");
        }
    }
}
