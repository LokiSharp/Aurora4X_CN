using System;
using System.Collections.Generic;
using System.Linq;
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
                        if (IsFolderName(text) || UsedAsPathOrIcon(ins, i) || IsDataKey(ins, i))
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
            int wrapSites = PatchUnbreakableLines(mod);
            int suffixSites = PatchKnownSuffixes(mod);
            int sliceSites = PatchStringCuts(mod);
            int mineralSites = PatchMineralNames(mod, tables.Display);
            var options = new ModuleWriterOptions(mod);
            options.Logger = DummyLogger.ThrowModuleWriterExceptionOnErrorInstance;
            mod.Write(outputPath, options);
            Console.WriteLine("exe replaced=" + replaced + " keptPathOrIcon=" + kept + " wrapSites=" + wrapSites + " suffixSites=" + suffixSites + " sliceSites=" + sliceSites + " mineralSites=" + mineralSites);
        }

        private static int PatchUnbreakableLines(ModuleDefMD mod)
        {
            int count = 0;
            foreach (TypeDef type in mod.GetTypes())
            {
                foreach (MethodDef method in type.Methods)
                {
                    if (!method.HasBody)
                        continue;
                    var list = method.Body.Instructions;
                    bool wraps = false;
                    foreach (Instruction ins in list)
                    {
                        if (ins.Operand != null && ins.Operand.ToString().IndexOf("TextRenderer::MeasureText", StringComparison.Ordinal) >= 0)
                            wraps = true;
                    }
                    if (!wraps)
                        continue;
                    for (int i = 0; i < list.Count - 3; i++)
                    {
                        if (list[i].OpCode.Code != Code.Ldstr || !string.Equals(list[i].Operand as string, " ", StringComparison.Ordinal))
                            continue;
                        string next = list[i + 1].Operand == null ? "" : list[i + 1].Operand.ToString();
                        if (next.IndexOf("LastIndexOf", StringComparison.Ordinal) < 0)
                            continue;
                        Local index = list[i + 2].GetLocal(method.Body.Variables);
                        if (index == null)
                            continue;
                        Instruction resume = list[i + 3];
                        Instruction done = null;
                        foreach (var handler in method.Body.ExceptionHandlers)
                        {
                            if (handler.HandlerType == ExceptionHandlerType.Catch && handler.HandlerEnd != null)
                                done = handler.HandlerEnd;
                        }
                        if (done == null)
                            continue;
                        Local lines = null;
                        Local result = null;
                        IMethod add = null;
                        for (int j = 2; j < list.Count - 2; j++)
                        {
                            var called = list[j].Operand as IMethod;
                            if (called == null || called.Name != "Add" || called.DeclaringType == null)
                                continue;
                            if (called.DeclaringType.ToString().IndexOf("List`1<System.String>", StringComparison.Ordinal) < 0)
                                continue;
                            if (list[j - 1].OpCode.Code != Code.Ldarg_1)
                                continue;
                            Local listLocal = list[j - 2].GetLocal(method.Body.Variables);
                            Local again = list[j + 1].GetLocal(method.Body.Variables);
                            Local stored = list[j + 2].GetLocal(method.Body.Variables);
                            if (listLocal == null || again != listLocal || stored == null)
                                continue;
                            lines = listLocal;
                            result = stored;
                            add = called;
                            break;
                        }
                        if (lines == null || result == null || add == null)
                            continue;
                        var injected = new[]
                        {
                            Instruction.Create(OpCodes.Ldloc, index),
                            Instruction.Create(OpCodes.Ldc_I4_0),
                            Instruction.Create(OpCodes.Bge, resume),
                            Instruction.Create(OpCodes.Ldloc, lines),
                            Instruction.Create(OpCodes.Ldarg_1),
                            Instruction.Create(OpCodes.Callvirt, add),
                            Instruction.Create(OpCodes.Ldloc, lines),
                            Instruction.Create(OpCodes.Stloc, result),
                            Instruction.Create(OpCodes.Leave, done)
                        };
                        for (int n = 0; n < injected.Length; n++)
                            list.Insert(i + 3 + n, injected[n]);
                        method.Body.SimplifyBranches();
                        method.Body.OptimizeBranches();
                        count++;
                        break;
                    }
                }
            }
            return count;
        }

        private static int PatchKnownSuffixes(ModuleDefMD mod)
        {
            MethodDef trim = AddTrimSuffix(mod);
            int count = 0;
            foreach (TypeDef type in mod.GetTypes())
            {
                foreach (MethodDef method in type.Methods)
                {
                    if (!method.HasBody || method == trim)
                        continue;
                    var list = method.Body.Instructions;
                    for (int i = 5; i < list.Count; i++)
                    {
                        string called = list[i].Operand == null ? "" : list[i].Operand.ToString();
                        if (called.IndexOf("lg::b(System.String,System.Int32)", StringComparison.Ordinal) < 0)
                            continue;
                        if (list[i - 1].OpCode.Code != Code.Sub)
                            continue;
                        if (!IsLdc(list[i - 2], 7))
                            continue;
                        if (list[i - 3].Operand == null || list[i - 3].Operand.ToString().IndexOf("get_Length", StringComparison.Ordinal) < 0)
                            continue;
                        list[i - 5].OpCode = OpCodes.Ldstr;
                        list[i - 5].Operand = "Shields";
                        list[i - 4].OpCode = OpCodes.Nop;
                        list[i - 4].Operand = null;
                        list[i - 3].OpCode = OpCodes.Nop;
                        list[i - 3].Operand = null;
                        list[i - 2].OpCode = OpCodes.Nop;
                        list[i - 2].Operand = null;
                        list[i - 1].OpCode = OpCodes.Nop;
                        list[i - 1].Operand = null;
                        list[i].OpCode = OpCodes.Call;
                        list[i].Operand = trim;
                        count++;
                    }
                }
            }
            return count;
        }

        private static bool IsLdc(Instruction ins, int value)
        {
            var code = ins.OpCode.Code;
            if (code == Code.Ldc_I4_7)
                return value == 7;
            if (code == Code.Ldc_I4_S || code == Code.Ldc_I4)
                return ins.Operand is int && (int)ins.Operand == value;
            return false;
        }

        private static MethodDef AddTrimSuffix(ModuleDefMD mod)
        {
            var method = new MethodDefUser(
                "TrimSuffix",
                MethodSig.CreateStatic(mod.CorLibTypes.String, mod.CorLibTypes.String, mod.CorLibTypes.String),
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig);
            method.Body = new CilBody();
            IMethod endsWith = mod.Import(typeof(string).GetMethod("EndsWith", new[] { typeof(string) }));
            IMethod getLength = mod.Import(typeof(string).GetProperty("Length").GetGetMethod());
            IMethod substring = mod.Import(typeof(string).GetMethod("Substring", new[] { typeof(int), typeof(int) }));
            var il = method.Body.Instructions;
            var hasValue = Instruction.Create(OpCodes.Ldarg_1);
            var keep = Instruction.Create(OpCodes.Ldarg_0);
            il.Add(Instruction.Create(OpCodes.Ldarg_0));
            il.Add(Instruction.Create(OpCodes.Brtrue, hasValue));
            il.Add(Instruction.Create(OpCodes.Ldstr, ""));
            il.Add(Instruction.Create(OpCodes.Ret));
            il.Add(hasValue);
            il.Add(Instruction.Create(OpCodes.Brfalse, keep));
            il.Add(Instruction.Create(OpCodes.Ldarg_0));
            il.Add(Instruction.Create(OpCodes.Ldarg_1));
            il.Add(Instruction.Create(OpCodes.Callvirt, endsWith));
            il.Add(Instruction.Create(OpCodes.Brfalse, keep));
            il.Add(Instruction.Create(OpCodes.Ldarg_0));
            il.Add(Instruction.Create(OpCodes.Ldc_I4_0));
            il.Add(Instruction.Create(OpCodes.Ldarg_0));
            il.Add(Instruction.Create(OpCodes.Callvirt, getLength));
            il.Add(Instruction.Create(OpCodes.Ldarg_1));
            il.Add(Instruction.Create(OpCodes.Callvirt, getLength));
            il.Add(Instruction.Create(OpCodes.Sub));
            il.Add(Instruction.Create(OpCodes.Callvirt, substring));
            il.Add(Instruction.Create(OpCodes.Ret));
            il.Add(keep);
            il.Add(Instruction.Create(OpCodes.Ret));
            TextUtil(mod).Methods.Add(method);
            return method;
        }

        private static int PatchStringCuts(ModuleDefMD mod)
        {
            MethodDef slice = AddSlice(mod, true);
            MethodDef sliceFrom = AddSlice(mod, false);
            MethodDef drop = AddDrop(mod, false);
            MethodDef dropCount = AddDrop(mod, true);
            var helpers = new HashSet<MethodDef> { slice, sliceFrom, drop, dropCount };
            int count = 0;
            foreach (TypeDef type in mod.GetTypes())
            {
                foreach (MethodDef method in type.Methods)
                {
                    if (!method.HasBody || helpers.Contains(method))
                        continue;
                    foreach (Instruction ins in method.Body.Instructions)
                    {
                        var called = ins.Operand as IMethod;
                        if (called == null || called.DeclaringType == null || called.DeclaringType.Name != "String")
                            continue;
                        int args = called.MethodSig == null ? 0 : called.MethodSig.Params.Count;
                        MethodDef replacement = null;
                        if (called.Name == "Substring" && args == 2)
                            replacement = slice;
                        else if (called.Name == "Substring" && args == 1)
                            replacement = sliceFrom;
                        else if (called.Name == "Remove" && args == 1)
                            replacement = drop;
                        else if (called.Name == "Remove" && args == 2)
                            replacement = dropCount;
                        if (replacement == null)
                            continue;
                        ins.Operand = replacement;
                        if (ins.OpCode.Code == Code.Callvirt)
                            ins.OpCode = OpCodes.Call;
                        count++;
                    }
                }
            }
            return count;
        }

        private static TypeDef TextUtil(ModuleDefMD mod)
        {
            var type = mod.Types.FirstOrDefault(item => item.Namespace == "AuroraLoc" && item.Name == "TextUtil");
            if (type != null)
                return type;
            type = new TypeDefUser("AuroraLoc", "TextUtil", mod.CorLibTypes.Object.TypeDefOrRef);
            type.Attributes = TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit;
            mod.Types.Add(type);
            return type;
        }

        private static MethodDef AddSlice(ModuleDefMD mod, bool withLength)
        {
            string name = withLength ? "Slice" : "SliceFrom";
            MethodSig sig = withLength
                ? MethodSig.CreateStatic(mod.CorLibTypes.String, mod.CorLibTypes.String, mod.CorLibTypes.Int32, mod.CorLibTypes.Int32)
                : MethodSig.CreateStatic(mod.CorLibTypes.String, mod.CorLibTypes.String, mod.CorLibTypes.Int32);
            var type = mod.Types.FirstOrDefault(item => item.Namespace == "AuroraLoc" && item.Name == "TextUtil");
            if (type == null)
            {
                type = new TypeDefUser("AuroraLoc", "TextUtil", mod.CorLibTypes.Object.TypeDefOrRef);
                type.Attributes = TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit;
                mod.Types.Add(type);
            }
            var method = new MethodDefUser(name, sig, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig);
            method.Body = new CilBody { InitLocals = true };
            var start = new Local(mod.CorLibTypes.Int32);
            method.Body.Variables.Add(start);
            Local taken = null;
            if (withLength)
            {
                taken = new Local(mod.CorLibTypes.Int32);
                method.Body.Variables.Add(taken);
            }
            IMethod substring = mod.Import(withLength
                ? typeof(string).GetMethod("Substring", new[] { typeof(int), typeof(int) })
                : typeof(string).GetMethod("Substring", new[] { typeof(int) }));
            IMethod getLength = mod.Import(typeof(string).GetProperty("Length").GetGetMethod());
            var il = method.Body.Instructions;
            var present = Instruction.Create(OpCodes.Ldarg_1);
            var useStart = Instruction.Create(OpCodes.Ldarg_1);
            var storeStart = Instruction.Create(OpCodes.Stloc, start);
            var startOk = Instruction.Create(OpCodes.Nop);
            il.Add(Instruction.Create(OpCodes.Ldarg_0));
            il.Add(Instruction.Create(OpCodes.Brtrue, present));
            il.Add(Instruction.Create(OpCodes.Ldstr, ""));
            il.Add(Instruction.Create(OpCodes.Ret));
            il.Add(present);
            il.Add(Instruction.Create(OpCodes.Ldc_I4_0));
            il.Add(Instruction.Create(OpCodes.Bge, useStart));
            il.Add(Instruction.Create(OpCodes.Ldc_I4_0));
            il.Add(Instruction.Create(OpCodes.Br, storeStart));
            il.Add(useStart);
            il.Add(storeStart);
            il.Add(Instruction.Create(OpCodes.Ldloc, start));
            il.Add(Instruction.Create(OpCodes.Ldarg_0));
            il.Add(Instruction.Create(OpCodes.Callvirt, getLength));
            il.Add(Instruction.Create(OpCodes.Ble, startOk));
            il.Add(Instruction.Create(OpCodes.Ldstr, ""));
            il.Add(Instruction.Create(OpCodes.Ret));
            il.Add(startOk);
            if (!withLength)
            {
                il.Add(Instruction.Create(OpCodes.Ldarg_0));
                il.Add(Instruction.Create(OpCodes.Ldloc, start));
                il.Add(Instruction.Create(OpCodes.Callvirt, substring));
                il.Add(Instruction.Create(OpCodes.Ret));
                TextUtil(mod).Methods.Add(method);
                return method;
            }
            var useTaken = Instruction.Create(OpCodes.Ldarg_2);
            var storeTaken = Instruction.Create(OpCodes.Stloc, taken);
            var fit = Instruction.Create(OpCodes.Ldarg_0);
            il.Add(Instruction.Create(OpCodes.Ldarg_2));
            il.Add(Instruction.Create(OpCodes.Ldc_I4_0));
            il.Add(Instruction.Create(OpCodes.Bge, useTaken));
            il.Add(Instruction.Create(OpCodes.Ldarg_0));
            il.Add(Instruction.Create(OpCodes.Callvirt, getLength));
            il.Add(Instruction.Create(OpCodes.Ldloc, start));
            il.Add(Instruction.Create(OpCodes.Sub));
            il.Add(Instruction.Create(OpCodes.Br, storeTaken));
            il.Add(useTaken);
            il.Add(storeTaken);
            il.Add(Instruction.Create(OpCodes.Ldloc, start));
            il.Add(Instruction.Create(OpCodes.Ldloc, taken));
            il.Add(Instruction.Create(OpCodes.Add));
            il.Add(Instruction.Create(OpCodes.Ldarg_0));
            il.Add(Instruction.Create(OpCodes.Callvirt, getLength));
            il.Add(Instruction.Create(OpCodes.Ble, fit));
            il.Add(Instruction.Create(OpCodes.Ldarg_0));
            il.Add(Instruction.Create(OpCodes.Callvirt, getLength));
            il.Add(Instruction.Create(OpCodes.Ldloc, start));
            il.Add(Instruction.Create(OpCodes.Sub));
            il.Add(Instruction.Create(OpCodes.Stloc, taken));
            il.Add(fit);
            il.Add(Instruction.Create(OpCodes.Ldloc, start));
            il.Add(Instruction.Create(OpCodes.Ldloc, taken));
            il.Add(Instruction.Create(OpCodes.Callvirt, substring));
            il.Add(Instruction.Create(OpCodes.Ret));
            TextUtil(mod).Methods.Add(method);
            return method;
        }

        private static MethodDef AddDrop(ModuleDefMD mod, bool withCount)
        {
            string name = withCount ? "DropCount" : "Drop";
            MethodSig sig = withCount
                ? MethodSig.CreateStatic(mod.CorLibTypes.String, mod.CorLibTypes.String, mod.CorLibTypes.Int32, mod.CorLibTypes.Int32)
                : MethodSig.CreateStatic(mod.CorLibTypes.String, mod.CorLibTypes.String, mod.CorLibTypes.Int32);
            var method = new MethodDefUser(name, sig, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig);
            method.Body = new CilBody { InitLocals = true };
            Local count = null;
            if (withCount)
            {
                count = new Local(mod.CorLibTypes.Int32);
                method.Body.Variables.Add(count);
            }
            IMethod remove = mod.Import(withCount
                ? typeof(string).GetMethod("Remove", new[] { typeof(int), typeof(int) })
                : typeof(string).GetMethod("Remove", new[] { typeof(int) }));
            IMethod getLength = mod.Import(typeof(string).GetProperty("Length").GetGetMethod());
            var il = method.Body.Instructions;
            var present = Instruction.Create(OpCodes.Nop);
            il.Add(Instruction.Create(OpCodes.Ldarg_0));
            il.Add(Instruction.Create(OpCodes.Brtrue, present));
            il.Add(Instruction.Create(OpCodes.Ldstr, ""));
            il.Add(Instruction.Create(OpCodes.Ret));
            il.Add(present);
            var same = Instruction.Create(OpCodes.Ldarg_0);
            if (!withCount)
            {
                var cut = Instruction.Create(OpCodes.Ldarg_0);
                il.Add(Instruction.Create(OpCodes.Ldarg_1));
                il.Add(Instruction.Create(OpCodes.Ldc_I4_0));
                il.Add(Instruction.Create(OpCodes.Blt, same));
                il.Add(Instruction.Create(OpCodes.Ldarg_1));
                il.Add(Instruction.Create(OpCodes.Ldarg_0));
                il.Add(Instruction.Create(OpCodes.Callvirt, getLength));
                il.Add(Instruction.Create(OpCodes.Bgt, same));
                il.Add(cut);
                il.Add(Instruction.Create(OpCodes.Ldarg_1));
                il.Add(Instruction.Create(OpCodes.Callvirt, remove));
                il.Add(Instruction.Create(OpCodes.Ret));
                il.Add(same);
                il.Add(Instruction.Create(OpCodes.Ret));
                TextUtil(mod).Methods.Add(method);
                return method;
            }
            var useCount = Instruction.Create(OpCodes.Ldarg_2);
            var storeCount = Instruction.Create(OpCodes.Stloc, count);
            var fit = Instruction.Create(OpCodes.Ldarg_0);
            il.Add(Instruction.Create(OpCodes.Ldarg_1));
            il.Add(Instruction.Create(OpCodes.Ldc_I4_0));
            il.Add(Instruction.Create(OpCodes.Blt, same));
            il.Add(Instruction.Create(OpCodes.Ldarg_1));
            il.Add(Instruction.Create(OpCodes.Ldarg_0));
            il.Add(Instruction.Create(OpCodes.Callvirt, getLength));
            il.Add(Instruction.Create(OpCodes.Bgt, same));
            il.Add(Instruction.Create(OpCodes.Ldarg_2));
            il.Add(Instruction.Create(OpCodes.Ldc_I4_0));
            il.Add(Instruction.Create(OpCodes.Bge, useCount));
            il.Add(Instruction.Create(OpCodes.Ldc_I4_0));
            il.Add(Instruction.Create(OpCodes.Br, storeCount));
            il.Add(useCount);
            il.Add(storeCount);
            il.Add(Instruction.Create(OpCodes.Ldarg_1));
            il.Add(Instruction.Create(OpCodes.Ldloc, count));
            il.Add(Instruction.Create(OpCodes.Add));
            il.Add(Instruction.Create(OpCodes.Ldarg_0));
            il.Add(Instruction.Create(OpCodes.Callvirt, getLength));
            il.Add(Instruction.Create(OpCodes.Ble, fit));
            il.Add(Instruction.Create(OpCodes.Ldarg_0));
            il.Add(Instruction.Create(OpCodes.Callvirt, getLength));
            il.Add(Instruction.Create(OpCodes.Ldarg_1));
            il.Add(Instruction.Create(OpCodes.Sub));
            il.Add(Instruction.Create(OpCodes.Stloc, count));
            il.Add(fit);
            il.Add(Instruction.Create(OpCodes.Ldarg_1));
            il.Add(Instruction.Create(OpCodes.Ldloc, count));
            il.Add(Instruction.Create(OpCodes.Callvirt, remove));
            il.Add(Instruction.Create(OpCodes.Ret));
            il.Add(same);
            il.Add(Instruction.Create(OpCodes.Ret));
            TextUtil(mod).Methods.Add(method);
            return method;
        }

        private static void PatchPad(ModuleDefMD mod)
        {
            foreach (TypeDef type in mod.GetTypes())
            {
                if (type.Name != "lg")
                    continue;
                foreach (MethodDef method in type.Methods)
                {
                    if (!method.HasBody || method.Name != "b" || method.Parameters.Count != 2)
                        continue;
                    if (method.Parameters[0].Type.FullName != "System.String" || method.Parameters[1].Type.FullName != "System.Int32")
                        continue;
                    IMethod substring = null;
                    foreach (Instruction ins in method.Body.Instructions)
                    {
                        var called = ins.Operand as IMethod;
                        if (called != null && called.Name == "Substring")
                            substring = called;
                    }
                    if (substring == null)
                        return;
                    var useArg = Instruction.Create(OpCodes.Ldarg_0);
                    method.Body.Instructions.Clear();
                    method.Body.ExceptionHandlers.Clear();
                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1));
                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Bge_S, useArg));
                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, substring));
                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                    method.Body.Instructions.Add(useArg);
                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1));
                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, substring));
                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                    Console.WriteLine("patched pad");
                    return;
                }
            }
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

        private static bool IsDataKey(IList<Instruction> ins, int index)
        {
            int end = Math.Min(ins.Count, index + 6);
            for (int i = index + 1; i < end; i++)
            {
                var code = ins[i].OpCode.Code;
                if (code == Code.Ldstr || code == Code.Ldloc || code == Code.Ldloc_S || code == Code.Ldloc_0
                    || code == Code.Ldloc_1 || code == Code.Ldloc_2 || code == Code.Ldloc_3 || code == Code.Ldc_I4
                    || code == Code.Ldc_I4_S || code == Code.Ldc_I4_0 || code == Code.Ldc_I4_1)
                    return false;
                if (code != Code.Call && code != Code.Callvirt && code != Code.Newobj)
                    continue;
                string rendered = ins[i].ToString();
                return rendered.IndexOf("get_Item(", StringComparison.Ordinal) >= 0
                    || rendered.IndexOf("set_Name(", StringComparison.Ordinal) >= 0
                    || rendered.IndexOf("GetObject", StringComparison.Ordinal) >= 0
                    || rendered.IndexOf("Substring", StringComparison.Ordinal) >= 0
                    || rendered.IndexOf("::Remove(", StringComparison.Ordinal) >= 0;
            }
            return false;
        }

        private static bool IsFolderName(string text)
        {
            return text == "Flags" || text == "Races" || text == "Icons" || text == "Medals"
                || text == "ShipIcons" || text == "StationIcons";
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
