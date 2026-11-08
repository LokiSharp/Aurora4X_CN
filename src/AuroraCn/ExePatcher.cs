using System;
using System.Collections.Generic;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

namespace AuroraCn
{
    internal static class ExePatcher
    {
        internal static void Write(string inputPath, string outputPath, TranslationTables tables)
        {
            var mod = ModuleDefMD.Load(inputPath);
            int replaced = 0;
            int kept = 0;
            foreach (var type in mod.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody)
                        continue;
                    var ins = method.Body.Instructions;
                    for (int i = 0; i < ins.Count; i++)
                    {
                        if (ins[i].OpCode.Code != Code.Ldstr || !(ins[i].Operand is string))
                            continue;
                        string text = (string)ins[i].Operand;
                        if (UsedAsPathOrIcon(ins, i))
                        {
                            kept++;
                            continue;
                        }
                        string chinese;
                        if (IsDisplaySite(ins, i) && tables.Display.TryGetValue(text, out chinese))
                        {
                            ins[i].Operand = chinese;
                            replaced++;
                        }
                        else if (tables.Ui.TryGetValue(text, out chinese))
                        {
                            ins[i].Operand = chinese;
                            replaced++;
                        }
                    }
                }
            }
            int mineralSites = PatchMineralNames(mod, tables.Display);
            var options = new ModuleWriterOptions(mod);
            options.Logger = DummyLogger.ThrowModuleWriterExceptionOnErrorInstance;
            mod.Write(outputPath, options);
            Console.WriteLine("exe replaced=" + replaced + " keptPathOrIcon=" + kept + " mineralSites=" + mineralSites);
        }

        private static int PatchMineralNames(ModuleDefMD mod, Dictionary<string, string> display)
        {
            var helper = AddMineralMethod(mod, display);
            if (helper == null)
                return 0;
            int count = 0;
            foreach (var type in mod.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody || method == helper)
                        continue;
                    var list = method.Body.Instructions;
                    for (int i = list.Count - 1; i >= 1; i--)
                    {
                        if (list[i].OpCode.Code != Code.Callvirt)
                            continue;
                        string called = list[i].Operand == null ? "" : list[i].Operand.ToString();
                        if (called.IndexOf("Object::ToString()", StringComparison.Ordinal) < 0)
                            continue;
                        if (list[i - 1].OpCode.Code != Code.Constrained)
                            continue;
                        var constrained = list[i - 1].Operand as ITypeDefOrRef;
                        if (constrained == null || constrained.Name != "AuroraElement")
                            continue;
                        list.Insert(i + 1, Instruction.Create(OpCodes.Call, helper));
                        count++;
                    }
                }
            }
            return count;
        }

        private static MethodDef AddMineralMethod(ModuleDefMD mod, Dictionary<string, string> display)
        {
            string[] names =
            {
                "Duranium", "Neutronium", "Corbomite", "Tritanium", "Boronide", "Mercassium",
                "Vendarite", "Sorium", "Uridium", "Corundium", "Gallicite"
            };
            var pairs = new List<string[]>();
            foreach (string name in names)
            {
                string chinese;
                if (display.TryGetValue(name, out chinese))
                    pairs.Add(new[] { name, chinese });
            }
            if (pairs.Count == 0)
                return null;
            var type = new TypeDefUser("AuroraLoc", "MineralName", mod.CorLibTypes.Object.TypeDefOrRef);
            type.Attributes = TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit;
            var method = new MethodDefUser(
                "T",
                MethodSig.CreateStatic(mod.CorLibTypes.String, mod.CorLibTypes.String),
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig);
            method.Body = new CilBody();
            var equals = mod.Import(typeof(string).GetMethod("op_Equality", new[] { typeof(string), typeof(string) }));
            var instructions = method.Body.Instructions;
            foreach (string[] pair in pairs)
            {
                var next = Instruction.Create(OpCodes.Nop);
                instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                instructions.Add(Instruction.Create(OpCodes.Ldstr, pair[0]));
                instructions.Add(Instruction.Create(OpCodes.Call, equals));
                instructions.Add(Instruction.Create(OpCodes.Brfalse, next));
                instructions.Add(Instruction.Create(OpCodes.Ldstr, pair[1]));
                instructions.Add(Instruction.Create(OpCodes.Ret));
                instructions.Add(next);
            }
            instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
            instructions.Add(Instruction.Create(OpCodes.Ret));
            type.Methods.Add(method);
            mod.Types.Add(type);
            return method;
        }

        private static bool UsedAsPathOrIcon(IList<Instruction> ins, int index)
        {
            int end = Math.Min(ins.Count, index + 7);
            for (int i = index + 1; i < end; i++)
            {
                var method = ins[i].Operand as IMethod;
                if (method == null)
                    continue;
                string full = method.FullName;
                if (full.IndexOf("System.IO.Path::Combine", StringComparison.Ordinal) >= 0)
                    return true;
                if (full.IndexOf("Control::set_Name", StringComparison.Ordinal) >= 0)
                    return true;
                if (method.DeclaringType != null && method.DeclaringType.Name == "lg")
                {
                    if (method.Name == "d" && full.IndexOf("System.String", StringComparison.Ordinal) >= 0)
                        return true;
                    if (method.Name == "e" && full.IndexOf("(System.String)", StringComparison.Ordinal) >= 0)
                        return true;
                    if (method.Name == "a" && full.IndexOf("System.Drawing.Color", StringComparison.Ordinal) >= 0)
                        return true;
                }
            }
            return false;
        }

        private static bool IsDisplaySite(IList<Instruction> ins, int index)
        {
            int end = Math.Min(ins.Count, index + 40);
            for (int i = index + 1; i < end; i++)
            {
                if (ins[i].Operand == null)
                    continue;
                string text = ins[i].Operand.ToString();
                if (text.IndexOf("get_Item(", StringComparison.Ordinal) >= 0
                    || text.IndexOf("System.IO.Path::", StringComparison.Ordinal) >= 0)
                    return false;
                if (text.IndexOf("::set_Text(", StringComparison.Ordinal) >= 0
                    || text.IndexOf("::set_ToolTipText(", StringComparison.Ordinal) >= 0
                    || text.IndexOf("::SetToolTip(", StringComparison.Ordinal) >= 0
                    || text.IndexOf("::set_HeaderText(", StringComparison.Ordinal) >= 0
                    || text.IndexOf("MessageBox::Show", StringComparison.Ordinal) >= 0
                    || text.IndexOf("ListViewItem::.ctor(", StringComparison.Ordinal) >= 0
                    || text.IndexOf("TreeNode::set_Text(", StringComparison.Ordinal) >= 0
                    || text.IndexOf("::set_AccessibleName(", StringComparison.Ordinal) >= 0
                    || text.IndexOf("::set_AccessibleDescription(", StringComparison.Ordinal) >= 0
                    || text.IndexOf("ObjectCollection::Add", StringComparison.Ordinal) >= 0
                    || text.IndexOf("ObjectCollection::Insert", StringComparison.Ordinal) >= 0
                    || text.IndexOf("op_Equality", StringComparison.Ordinal) >= 0
                    || text.IndexOf("a2::a6(", StringComparison.Ordinal) >= 0)
                    return true;
            }
            return false;
        }
    }
}
