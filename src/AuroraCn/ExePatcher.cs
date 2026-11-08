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
            int attributeSites = PatchAttributeStrings(mod, tables);
            int enumSites = PatchEnumNames(mod, tables);
            int switchSites = PatchStringSwitches(mod);
            int wrapSites = PatchUnbreakableLines(mod);
            int suffixSites = PatchKnownSuffixes(mod);
            int sliceSites = PatchStringCuts(mod);
            int mineralSites = PatchMineralNames(mod, tables.Display);
            var options = new ModuleWriterOptions(mod);
            options.Logger = DummyLogger.ThrowModuleWriterExceptionOnErrorInstance;
            mod.Write(outputPath, options);
            Console.WriteLine("exe replaced=" + replaced + " keptPathOrIcon=" + kept + " attributeSites=" + attributeSites + " enumSites=" + enumSites + " switchSites=" + switchSites + " wrapSites=" + wrapSites + " suffixSites=" + suffixSites + " sliceSites=" + sliceSites + " mineralSites=" + mineralSites);
        }

        private static int PatchStringSwitches(ModuleDefMD mod)
        {
            int count = 0;
            foreach (TypeDef type in mod.GetTypes())
            {
                foreach (MethodDef method in type.Methods)
                {
                    if (!method.HasBody)
                        continue;
                    while (RewriteOneStringSwitch(method))
                        count++;
                }
            }
            return count;
        }

        private static bool RewriteOneStringSwitch(MethodDef method)
        {
            var list = method.Body.Instructions;
            for (int lengthCall = 1; lengthCall < list.Count; lengthCall++)
            {
                string rendered = list[lengthCall].Operand == null ? "" : list[lengthCall].Operand.ToString();
                if (rendered.IndexOf("String::get_Length", StringComparison.Ordinal) < 0)
                    continue;
                if (!HasLengthBranch(list, lengthCall) || !HasAsciiCharTest(list, lengthCall))
                    continue;
                if (RewriteAt(method, lengthCall))
                    return true;
            }
            return false;
        }

        private static bool RewriteAt(MethodDef method, int lengthCall)
        {
            var list = method.Body.Instructions;
            Instruction sample = list[lengthCall - 1];
            if (!IsCopyableLoad(sample))
                return false;
            if (lengthCall < 3 || !IsBrfalse(list[lengthCall - 2]) || !SameLoad(list[lengthCall - 3], sample))
                return false;
            Instruction fail = list[lengthCall - 2].Operand as Instruction;
            int start = lengthCall - 2;
            if (fail == null)
                return false;
            var cases = new List<KeyValuePair<string, Instruction>>();
            int end = list.Count;
            for (int i = start; i < list.Count - 2 && i < end; i++)
            {
                if (list[i].OpCode.Code != Code.Ldstr || !(list[i].Operand is string))
                    continue;
                if (list[i + 1].Operand == null || list[i + 1].Operand.ToString().IndexOf("op_Equality", StringComparison.Ordinal) < 0)
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
                    cases.Add(new KeyValuePair<string, Instruction>((string)list[i].Operand, target));
            }
            if (cases.Count == 0 || end <= start)
                return false;
            if (LengthsStillMatch(list, lengthCall, end, cases))
                return false;
            object equality = null;
            for (int i = start; i < end; i++)
            {
                if (list[i].Operand != null && list[i].Operand.ToString().IndexOf("op_Equality", StringComparison.Ordinal) >= 0)
                    equality = list[i].Operand;
            }
            if (!(equality is IMethod))
                return false;
            var rewritten = new List<Instruction>();
            rewritten.Add(Instruction.Create(OpCodes.Brfalse, fail));
            foreach (var item in cases)
            {
                rewritten.Add(CopyLoad(sample));
                rewritten.Add(Instruction.Create(OpCodes.Ldstr, item.Key));
                rewritten.Add(Instruction.Create(OpCodes.Call, (IMethod)equality));
                rewritten.Add(Instruction.Create(OpCodes.Brtrue, item.Value));
            }
            rewritten.Add(Instruction.Create(OpCodes.Br, fail));
            for (int i = end - 1; i >= start; i--)
                list.RemoveAt(i);
            for (int i = 0; i < rewritten.Count; i++)
                list.Insert(start + i, rewritten[i]);
            method.Body.SimplifyBranches();
            method.Body.OptimizeBranches();
            Console.WriteLine("switch " + method.DeclaringType.Name + "::" + method.Name);
            return true;
        }

        private static bool LengthsStillMatch(IList<Instruction> list, int lengthCall, int end, List<KeyValuePair<string, Instruction>> cases)
        {
            if (lengthCall + 1 >= list.Count)
                return true;
            int lengthLocal = LocalIndex(list[lengthCall + 1]);
            if (lengthLocal < 0)
                return true;
            var accepted = new HashSet<int>();
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
            if (accepted.Count == 0)
                return true;
            foreach (var item in cases)
            {
                if (!accepted.Contains(item.Key.Length) || HasNonAscii(item.Key))
                    return false;
            }
            return true;
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
                string rendered = list[i].Operand == null ? "" : list[i].Operand.ToString();
                if (rendered.IndexOf("String::get_Chars", StringComparison.Ordinal) < 0)
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

        private static bool IsBrfalse(Instruction ins)
        {
            return ins.OpCode.Code == Code.Brfalse || ins.OpCode.Code == Code.Brfalse_S;
        }

        private static bool IsCopyableLoad(Instruction ins)
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

        private static Instruction CopyLoad(Instruction sample)
        {
            if (sample.Operand == null)
                return Instruction.Create(sample.OpCode);
            if (sample.Operand is Local local)
                return Instruction.Create(sample.OpCode, local);
            return Instruction.Create(sample.OpCode, (Parameter)sample.Operand);
        }

        private static int PatchEnumNames(ModuleDefMD mod, TranslationTables tables)
        {
            MethodDef translator = AddEnumTranslator(mod, tables);
            int count = 0;
            foreach (TypeDef type in mod.GetTypes())
            {
                if (type.Name != "lg")
                    continue;
                foreach (MethodDef method in type.Methods)
                {
                    if (!method.HasBody || method.Name != "a" || method.MethodSig == null || method.MethodSig.Params.Count != 1)
                        continue;
                    if (method.MethodSig.RetType.FullName != "System.String" || method.MethodSig.Params[0].FullName != "System.Enum")
                        continue;
                    var list = method.Body.Instructions;
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (list[i].OpCode.Code != Code.Ret)
                            continue;
                        list.Insert(i, Instruction.Create(OpCodes.Call, translator));
                        count++;
                        i++;
                    }
                }
            }
            return count;
        }

        private static MethodDef AddEnumTranslator(ModuleDefMD mod, TranslationTables tables)
        {
            var type = TextUtil(mod);
            var dictSig = mod.Import(typeof(Dictionary<string, string>)).ToTypeSig();
            var field = new FieldDefUser("enumMap", new FieldSig(dictSig), FieldAttributes.Private | FieldAttributes.Static);
            type.Fields.Add(field);
            var ctor = mod.Import(typeof(Dictionary<string, string>).GetConstructor(Type.EmptyTypes));
            var add = mod.Import(typeof(Dictionary<string, string>).GetMethod("Add", new[] { typeof(string), typeof(string) }));
            var tryGet = mod.Import(typeof(Dictionary<string, string>).GetMethod("TryGetValue"));
            var cctor = new MethodDefUser(".cctor", MethodSig.CreateStatic(mod.CorLibTypes.Void), MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName | MethodAttributes.HideBySig);
            cctor.Body = new CilBody();
            var init = cctor.Body.Instructions;
            init.Add(Instruction.Create(OpCodes.Newobj, ctor));
            init.Add(Instruction.Create(OpCodes.Stsfld, field));
            var seen = new HashSet<string>();
            AddEnumPairs(init, field, add, tables.Display, seen);
            AddEnumPairs(init, field, add, tables.Ui, seen);
            init.Add(Instruction.Create(OpCodes.Ret));
            type.Methods.Add(cctor);
            var method = new MethodDefUser("EnumName", MethodSig.CreateStatic(mod.CorLibTypes.String, mod.CorLibTypes.String), MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig);
            method.Body = new CilBody { InitLocals = true };
            var local = new Local(mod.CorLibTypes.String);
            method.Body.Variables.Add(local);
            var keep = Instruction.Create(OpCodes.Ldarg_0);
            var il = method.Body.Instructions;
            var present = Instruction.Create(OpCodes.Ldsfld, field);
            il.Add(Instruction.Create(OpCodes.Ldarg_0));
            il.Add(Instruction.Create(OpCodes.Brtrue, present));
            il.Add(Instruction.Create(OpCodes.Ldnull));
            il.Add(Instruction.Create(OpCodes.Ret));
            il.Add(present);
            il.Add(Instruction.Create(OpCodes.Ldarg_0));
            il.Add(Instruction.Create(OpCodes.Ldloca, local));
            il.Add(Instruction.Create(OpCodes.Callvirt, tryGet));
            il.Add(Instruction.Create(OpCodes.Brfalse, keep));
            il.Add(Instruction.Create(OpCodes.Ldloc, local));
            il.Add(Instruction.Create(OpCodes.Ret));
            il.Add(keep);
            il.Add(Instruction.Create(OpCodes.Ret));
            type.Methods.Add(method);
            return method;
        }

        private static void AddEnumPairs(IList<Instruction> il, FieldDef field, IMethod add, Dictionary<string, string> pairs, HashSet<string> seen)
        {
            foreach (var pair in pairs)
            {
                if (pair.Key.Length == 0 || seen.Contains(pair.Key) || IsFolderName(pair.Key))
                    continue;
                seen.Add(pair.Key);
                il.Add(Instruction.Create(OpCodes.Ldsfld, field));
                il.Add(Instruction.Create(OpCodes.Ldstr, pair.Key));
                il.Add(Instruction.Create(OpCodes.Ldstr, pair.Value));
                il.Add(Instruction.Create(OpCodes.Callvirt, add));
            }
        }

        private static int PatchAttributeStrings(ModuleDefMD mod, TranslationTables tables)
        {
            int count = 0;
            foreach (TypeDef type in mod.GetTypes())
            {
                count += PatchAttributes(type.CustomAttributes, tables);
                foreach (FieldDef field in type.Fields)
                {
                    count += PatchAttributes(field.CustomAttributes, tables);
                    count += PatchConstant(field.Constant, tables);
                }
                foreach (MethodDef method in type.Methods)
                {
                    count += PatchAttributes(method.CustomAttributes, tables);
                    foreach (ParamDef param in method.ParamDefs)
                    {
                        count += PatchAttributes(param.CustomAttributes, tables);
                        count += PatchConstant(param.Constant, tables);
                    }
                }
                foreach (PropertyDef property in type.Properties)
                    count += PatchAttributes(property.CustomAttributes, tables);
                foreach (EventDef evt in type.Events)
                    count += PatchAttributes(evt.CustomAttributes, tables);
            }
            return count;
        }

        private static int PatchAttributes(CustomAttributeCollection attributes, TranslationTables tables)
        {
            if (attributes == null)
                return 0;
            int count = 0;
            foreach (CustomAttribute attribute in attributes)
            {
                for (int i = 0; i < attribute.ConstructorArguments.Count; i++)
                {
                    CAArgument argument = attribute.ConstructorArguments[i];
                    if (!TryTranslate(argument, tables, out CAArgument updated))
                        continue;
                    attribute.ConstructorArguments[i] = updated;
                    count++;
                }
                for (int i = 0; i < attribute.NamedArguments.Count; i++)
                {
                    CANamedArgument named = attribute.NamedArguments[i];
                    if (!TryTranslate(named.Argument, tables, out CAArgument updated))
                        continue;
                    named.Argument = updated;
                    attribute.NamedArguments[i] = named;
                    count++;
                }
            }
            return count;
        }

        private static int PatchConstant(Constant constant, TranslationTables tables)
        {
            if (constant == null || constant.Value == null)
                return 0;
            string text = constant.Value as string;
            if (text == null && constant.Value is UTF8String)
                text = ((UTF8String)constant.Value).String;
            if (text == null || IsFolderName(text))
                return 0;
            if (!TryMap(text, tables, out string chinese))
                return 0;
            constant.Value = chinese;
            return 1;
        }

        private static bool TryTranslate(CAArgument argument, TranslationTables tables, out CAArgument updated)
        {
            updated = argument;
            string text = argument.Value as string;
            if (text == null && argument.Value is UTF8String)
                text = ((UTF8String)argument.Value).String;
            if (text == null || argument.Type == null || argument.Type.FullName != "System.String" || IsFolderName(text))
                return false;
            if (!TryMap(text, tables, out string chinese))
                return false;
            updated = new CAArgument(argument.Type, new UTF8String(chinese));
            return true;
        }

        private static bool TryMap(string text, TranslationTables tables, out string chinese)
        {
            if (tables.Display.TryGetValue(text, out chinese))
                return true;
            return tables.Ui.TryGetValue(text, out chinese);
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
