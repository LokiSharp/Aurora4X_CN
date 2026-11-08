using System.Globalization;
using System.Reflection;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace AuroraExeTests;

public sealed record ProbeResult(string Key, string Status, string Detail);

public static class IlProbe
{
    public const int StepBudget = 20000;
    public const int CallDepthBudget = 24;

    public static string FindGameExe()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            string candidate = Path.Combine(dir, "Aurora.exe");
            if (File.Exists(candidate))
                return candidate;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return @"C:\Game\Aurora\Aurora.exe";
    }

    public static IReadOnlyList<ProbeResult> RunAll(string exePath)
    {
        var module = ModuleDefMD.Load(exePath);
        var vm = new Vm(module);
        var results = new List<ProbeResult>();
        foreach (TypeDef type in module.GetTypes())
        {
            foreach (MethodDef method in type.Methods)
                results.Add(Probe(vm, method));
        }
        results.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        return results;
    }

    public static ProbeResult Probe(Vm vm, MethodDef method)
    {
        string key = method.FullName;
        if (!method.HasBody)
            return new ProbeResult(key, "skip", "no-body");
        if (method.IsAbstract || method.Name == ".cctor")
            return new ProbeResult(key, "skip", method.Name);
        if (method.HasGenericParameters)
            return new ProbeResult(key, "skip", "generic");
        object?[]? args = Defaults(method);
        if (args == null)
            return new ProbeResult(key, "skip", "byref");
        try
        {
            object? value = vm.Execute(method, args, 0);
            return new ProbeResult(key, "return", Format(value));
        }
        catch (ProbeLimitException ex)
        {
            return new ProbeResult(key, "limit", ex.Message);
        }
        catch (ProbeUnsupportedException ex)
        {
            return new ProbeResult(key, "unsupported", Trim(ex.Message));
        }
        catch (Exception ex)
        {
            return new ProbeResult(key, "throw", ex.GetType().Name);
        }
    }

    public static object? Execute(string exePath, string fullName, object?[] args)
    {
        var module = ModuleDefMD.Load(exePath);
        MethodDef? method = null;
        foreach (TypeDef type in module.GetTypes())
        {
            foreach (MethodDef candidate in type.Methods)
            {
                if (candidate.FullName == fullName)
                    method = candidate;
            }
        }
        if (method == null)
            throw new InvalidOperationException(fullName);
        return new Vm(module).Execute(method, args, 0);
    }

    private static object?[]? Defaults(MethodDef method)
    {
        var args = new object?[method.Parameters.Count];
        for (int i = 0; i < method.Parameters.Count; i++)
        {
            TypeSig type = method.Parameters[i].Type;
            if (type.IsByRef || type.ElementType == ElementType.Ptr)
                return null;
            if (method.HasThis && i == 0)
            {
                args[i] = new Slot();
                continue;
            }
            args[i] = DefaultValue(type);
        }
        return args;
    }

    private static object? DefaultValue(TypeSig type)
    {
        switch (type.ElementType)
        {
            case ElementType.Boolean: return false;
            case ElementType.Char: return '\0';
            case ElementType.I1:
            case ElementType.U1:
            case ElementType.I2:
            case ElementType.U2:
            case ElementType.I4:
            case ElementType.U4: return 0;
            case ElementType.I8:
            case ElementType.U8: return 0L;
            case ElementType.R4: return 0f;
            case ElementType.R8: return 0d;
            case ElementType.String: return "";
            case ElementType.I:
            case ElementType.U: return IntPtr.Zero;
            default:
                if (type.FullName == "System.Decimal")
                    return 0m;
                if (type.FullName == "System.DateTime")
                    return default(DateTime);
                return null;
        }
    }

    private static string Format(object? value)
    {
        if (value == null)
            return "null";
        if (value is string text)
            return "string:" + Trim(text);
        if (value is Slot)
            return "object";
        if (value is bool bit)
            return bit ? "true" : "false";
        return value.GetType().Name + ":" + Trim(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
    }

    private static string Trim(string text)
    {
        text = text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        return text.Length <= 80 ? text : text.Substring(0, 80);
    }
}

public sealed class ProbeLimitException : Exception
{
    public ProbeLimitException(string message) : base(message) { }
}

public sealed class ProbeUnsupportedException : Exception
{
    public ProbeUnsupportedException(string message) : base(message) { }
}

public sealed class Slot
{
    private readonly Dictionary<string, object?> fields = new Dictionary<string, object?>(StringComparer.Ordinal);

    public object? Get(string name)
    {
        fields.TryGetValue(name, out object? value);
        return value;
    }

    public void Set(string name, object? value)
    {
        fields[name] = value;
    }
}

public sealed class RefCell
{
    public object? Value;

    public override string ToString()
    {
        return Value == null ? "" : Convert.ToString(Value, CultureInfo.InvariantCulture) ?? "";
    }
}

public sealed class Vm
{
    private readonly ModuleDef module;
    private readonly Dictionary<string, MethodDef> methods = new Dictionary<string, MethodDef>(StringComparer.Ordinal);

    public Vm(ModuleDef module)
    {
        this.module = module;
        foreach (TypeDef type in module.GetTypes())
        {
            foreach (MethodDef method in type.Methods)
                methods[method.FullName] = method;
        }
    }

    public object? Execute(MethodDef method, object?[] args, int depth)
    {
        if (depth > IlProbe.CallDepthBudget)
            throw new ProbeLimitException("depth");
        if (!method.HasBody)
            throw new ProbeUnsupportedException("no-body " + method.FullName);
        var instructions = method.Body.Instructions;
        var offset = new Dictionary<uint, int>();
        for (int i = 0; i < instructions.Count; i++)
            offset[instructions[i].Offset] = i;
        var locals = new object?[method.Body.Variables.Count];
        var stack = new Stack<object?>();
        int ip = 0;
        int steps = 0;
        while (ip < instructions.Count)
        {
            if (++steps > IlProbe.StepBudget)
                throw new ProbeLimitException("steps");
            Instruction ins = instructions[ip];
            int next = ip + 1;
            try
            {
                next = Step(method, instructions, offset, args, locals, stack, ip, depth);
            }
            catch (ProbeLimitException)
            {
                throw;
            }
            catch (ProbeUnsupportedException)
            {
                throw;
            }
            catch (Exception ex)
            {
                int handler = FindHandler(method, ins.Offset, ex);
                if (handler < 0)
                    throw;
                var handle = method.Body.ExceptionHandlers[handler];
                stack.Clear();
                if (handle.HandlerType == ExceptionHandlerType.Catch)
                    stack.Push(ex);
                next = offset[handle.HandlerStart.Offset];
            }
            ip = next;
        }
        return stack.Count == 0 ? null : stack.Pop();
    }

    private int Step(MethodDef method, IList<Instruction> instructions, Dictionary<uint, int> offset, object?[] args, object?[] locals, Stack<object?> stack, int ip, int depth)
    {
        Instruction ins = instructions[ip];
        int next = ip + 1;
        switch (ins.OpCode.Code)
        {
            case Code.Nop:
            case Code.Conv_I:
            case Code.Conv_I1:
            case Code.Conv_I2:
            case Code.Conv_I4:
            case Code.Conv_U:
            case Code.Conv_U1:
            case Code.Conv_U2:
            case Code.Conv_U4:
            case Code.Conv_R_Un:
            case Code.Break:
                break;
            case Code.Conv_I8:
            case Code.Conv_U8:
                stack.Push(Convert.ToInt64(stack.Pop(), CultureInfo.InvariantCulture));
                break;
            case Code.Conv_R4:
                stack.Push(Convert.ToSingle(stack.Pop(), CultureInfo.InvariantCulture));
                break;
            case Code.Conv_R8:
                stack.Push(Convert.ToDouble(stack.Pop(), CultureInfo.InvariantCulture));
                break;
            case Code.Ldarg_0:
            case Code.Ldarg_1:
            case Code.Ldarg_2:
            case Code.Ldarg_3:
                stack.Push(args[ins.OpCode.Code - Code.Ldarg_0]);
                break;
            case Code.Ldarg:
            case Code.Ldarg_S:
                stack.Push(args[((Parameter)ins.Operand).Index]);
                break;
            case Code.Ldarga:
            case Code.Ldarga_S:
                stack.Push(new RefCell { Value = args[((Parameter)ins.Operand).Index] });
                break;
            case Code.Ldloc_0:
            case Code.Ldloc_1:
            case Code.Ldloc_2:
            case Code.Ldloc_3:
                stack.Push(locals[ins.OpCode.Code - Code.Ldloc_0]);
                break;
            case Code.Ldloc:
            case Code.Ldloc_S:
                stack.Push(locals[((Local)ins.Operand).Index]);
                break;
            case Code.Ldloca:
            case Code.Ldloca_S:
                stack.Push(new RefCell { Value = locals[((Local)ins.Operand).Index] });
                break;
            case Code.Stloc_0:
            case Code.Stloc_1:
            case Code.Stloc_2:
            case Code.Stloc_3:
                locals[ins.OpCode.Code - Code.Stloc_0] = stack.Pop();
                break;
            case Code.Stloc:
            case Code.Stloc_S:
                locals[((Local)ins.Operand).Index] = stack.Pop();
                break;
            case Code.Ldstr:
                stack.Push((string)ins.Operand);
                break;
            case Code.Ldnull:
                stack.Push(null);
                break;
            case Code.Ldc_I4_M1:
                stack.Push(-1);
                break;
            case Code.Ldc_I4_0:
            case Code.Ldc_I4_1:
            case Code.Ldc_I4_2:
            case Code.Ldc_I4_3:
            case Code.Ldc_I4_4:
            case Code.Ldc_I4_5:
            case Code.Ldc_I4_6:
            case Code.Ldc_I4_7:
            case Code.Ldc_I4_8:
                stack.Push((int)(ins.OpCode.Code - Code.Ldc_I4_0));
                break;
            case Code.Ldc_I4:
            case Code.Ldc_I4_S:
                stack.Push(Convert.ToInt32(ins.Operand, CultureInfo.InvariantCulture));
                break;
            case Code.Ldc_I8:
                stack.Push(Convert.ToInt64(ins.Operand, CultureInfo.InvariantCulture));
                break;
            case Code.Ldc_R4:
                stack.Push(Convert.ToSingle(ins.Operand, CultureInfo.InvariantCulture));
                break;
            case Code.Ldc_R8:
                stack.Push(Convert.ToDouble(ins.Operand, CultureInfo.InvariantCulture));
                break;
            case Code.Dup:
                stack.Push(stack.Peek());
                break;
            case Code.Pop:
                stack.Pop();
                break;
            case Code.Ldfld:
            case Code.Ldsfld:
                stack.Push(ReadField(ins.OpCode.Code == Code.Ldsfld ? null : stack.Pop(), (IField)ins.Operand));
                break;
            case Code.Stfld:
            case Code.Stsfld:
                {
                    object? value = stack.Pop();
                    object? target = ins.OpCode.Code == Code.Stsfld ? null : stack.Pop();
                    WriteField(target, (IField)ins.Operand, value);
                    break;
                }
            case Code.Ldlen:
                stack.Push(((Array)stack.Pop()!).Length);
                break;
            case Code.Newarr:
                stack.Push(Array.CreateInstance(typeof(object), Convert.ToInt32(stack.Pop(), CultureInfo.InvariantCulture)));
                break;
            case Code.Ldelem_Ref:
            case Code.Ldelem_I4:
            case Code.Ldelem_I:
            case Code.Ldelem_U1:
            case Code.Ldelem_U2:
            case Code.Ldelem_U4:
                {
                    int index = Convert.ToInt32(stack.Pop(), CultureInfo.InvariantCulture);
                    var array = (Array)stack.Pop()!;
                    stack.Push(array.GetValue(index));
                    break;
                }
            case Code.Stelem_Ref:
            case Code.Stelem_I4:
            case Code.Stelem_I:
                {
                    object? value = stack.Pop();
                    int index = Convert.ToInt32(stack.Pop(), CultureInfo.InvariantCulture);
                    var array = (Array)stack.Pop()!;
                    array.SetValue(value, index);
                    break;
                }
            case Code.Add:
            case Code.Sub:
            case Code.Mul:
            case Code.Div:
            case Code.Rem:
            case Code.And:
            case Code.Or:
            case Code.Xor:
            case Code.Shl:
            case Code.Shr:
                Binary(stack, ins.OpCode.Code);
                break;
            case Code.Neg:
                stack.Push(-Convert.ToDecimal(stack.Pop(), CultureInfo.InvariantCulture));
                break;
            case Code.Not:
                stack.Push(~Convert.ToInt32(stack.Pop(), CultureInfo.InvariantCulture));
                break;
            case Code.Ceq:
            case Code.Cgt:
            case Code.Cgt_Un:
            case Code.Clt:
            case Code.Clt_Un:
                stack.Push(Compare(stack.Pop(), stack.Pop(), ins.OpCode.Code == Code.Cgt_Un ? Code.Cgt : ins.OpCode.Code == Code.Clt_Un ? Code.Clt : ins.OpCode.Code) ? 1 : 0);
                break;
            case Code.Br:
            case Code.Br_S:
                next = offset[((Instruction)ins.Operand).Offset];
                break;
            case Code.Brfalse:
            case Code.Brfalse_S:
                next = IsTrue(stack.Pop()) ? next : offset[((Instruction)ins.Operand).Offset];
                break;
            case Code.Brtrue:
            case Code.Brtrue_S:
                next = IsTrue(stack.Pop()) ? offset[((Instruction)ins.Operand).Offset] : next;
                break;
            case Code.Beq:
            case Code.Beq_S:
            case Code.Bne_Un:
            case Code.Bne_Un_S:
            case Code.Bgt:
            case Code.Bgt_S:
            case Code.Bge:
            case Code.Bge_S:
            case Code.Blt:
            case Code.Blt_S:
            case Code.Ble:
            case Code.Ble_S:
            case Code.Bge_Un:
            case Code.Bge_Un_S:
            case Code.Bgt_Un:
            case Code.Bgt_Un_S:
            case Code.Ble_Un:
            case Code.Ble_Un_S:
            case Code.Blt_Un:
            case Code.Blt_Un_S:
                {
                    object? right = stack.Pop();
                    object? left = stack.Pop();
                    bool taken = Branch(left, right, ins.OpCode.Code);
                    next = taken ? offset[((Instruction)ins.Operand).Offset] : next;
                    break;
                }
            case Code.Switch:
                {
                    int value = Convert.ToInt32(stack.Pop(), CultureInfo.InvariantCulture);
                    var targets = (Instruction[])ins.Operand;
                    if (value >= 0 && value < targets.Length)
                        next = offset[targets[value].Offset];
                    break;
                }
            case Code.Call:
            case Code.Callvirt:
            case Code.Newobj:
                Call(stack, (IMethod)ins.Operand, ins.OpCode.Code == Code.Newobj, depth);
                break;
            case Code.Ret:
                next = instructions.Count;
                break;
            case Code.Castclass:
                stack.Push(Cast(stack.Pop(), (ITypeDefOrRef)ins.Operand, false));
                break;
            case Code.Isinst:
            case Code.Box:
            case Code.Unbox_Any:
                stack.Push(stack.Pop());
                break;
            case Code.Throw:
                throw (Exception)stack.Pop()!;
            case Code.Rethrow:
                throw new InvalidOperationException("rethrow");
            case Code.Leave:
            case Code.Leave_S:
                stack.Clear();
                next = offset[((Instruction)ins.Operand).Offset];
                break;
            case Code.Endfinally:
                break;
            case Code.Ldftn:
                stack.Push((IMethod)ins.Operand);
                break;
            case Code.Ldflda:
            case Code.Ldsflda:
                {
                    object? fieldTarget = ins.OpCode.Code == Code.Ldsflda ? null : stack.Pop();
                    stack.Push(new RefCell { Value = ReadField(fieldTarget, (IField)ins.Operand) });
                    break;
                }
            case Code.Initobj:
                {
                    object? address = stack.Pop();
                    if (address is RefCell addressed)
                        addressed.Value = null;
                    break;
                }
            case Code.Ldtoken:
                stack.Push(ins.Operand == null ? "token" : ins.Operand.ToString());
                break;
            default:
                throw new ProbeUnsupportedException(ins.OpCode.Name);
        }
        return next;
    }

    private void Call(Stack<object?> stack, IMethod target, bool constructor, int depth)
    {
        int count = target.MethodSig.Params.Count + (target.MethodSig.HasThis && !constructor ? 1 : 0);
        var popped = new object?[count];
        for (int i = count - 1; i >= 0; i--)
            popped[i] = stack.Pop();
        if (methods.TryGetValue(target.FullName, out MethodDef? local) && local.HasBody)
        {
            object? value = Execute(local, popped, depth + 1);
            if (target.MethodSig.RetType.ElementType != ElementType.Void)
                stack.Push(value);
            return;
        }
        object? result = InvokeExternal(target, popped, constructor);
        if (constructor || target.MethodSig.RetType.ElementType != ElementType.Void)
            stack.Push(result);
    }

    private static object? InvokeExternal(IMethod target, object?[] args, bool constructor)
    {
        string typeName = target.DeclaringType.FullName;
        if (IsBlocked(typeName))
            return constructor ? new Slot() : null;
        if (target.Name == ".ctor" && typeName == "System.Object")
            return args.Length > 0 ? args[0] : new object();
        Type? type = ResolveType(target.DeclaringType);
        if (type == null)
            return StubUnknown(typeName, target.Name, constructor);
        object? instance = null;
        object?[] callArgs = args;
        if (target.MethodSig.HasThis && !constructor)
        {
            instance = Unwrap(args[0]);
            callArgs = args.Skip(1).ToArray();
        }
        Type[] types = callArgs.Select(value => value?.GetType() ?? typeof(object)).ToArray();
        if (constructor)
        {
            ConstructorInfo? ctor = type.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, types, null);
            if (ctor == null)
                ctor = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).FirstOrDefault(item => item.GetParameters().Length == callArgs.Length);
            if (ctor == null)
                throw new ProbeUnsupportedException("new " + type.Name);
            return ctor.Invoke(callArgs);
        }
        MethodInfo? method = type.GetMethod(target.Name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance, null, types, null);
        if (method == null)
        {
            method = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                .FirstOrDefault(item => item.Name == target.Name && item.GetParameters().Length == callArgs.Length);
        }
        if (method == null)
            throw new ProbeUnsupportedException(type.Name + "." + target.Name);
        if (method.IsGenericMethodDefinition)
            method = method.MakeGenericMethod(callArgs.Select(value => value?.GetType() ?? typeof(object)).ToArray());
        try
        {
            return method.Invoke(method.IsStatic ? null : instance, callArgs);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }
    }

    private static object? StubUnknown(string typeName, string name, bool constructor)
    {
        bool collection = typeName.IndexOf("Dictionary`2", StringComparison.Ordinal) >= 0
            || typeName.IndexOf("List`1", StringComparison.Ordinal) >= 0
            || typeName.IndexOf("HashSet`1", StringComparison.Ordinal) >= 0
            || typeName.IndexOf("IEnumerable`1", StringComparison.Ordinal) >= 0
            || typeName.IndexOf("Enumerable", StringComparison.Ordinal) >= 0;
        if (!collection)
            throw new ProbeUnsupportedException(typeName + "::" + name);
        if (constructor || name == "ToList" || name == "ToArray" || name == "get_Values" || name == "get_Keys")
            return new Slot();
        if (name == "Contains" || name == "ContainsKey" || name == "TryGetValue")
            return false;
        if (name == "get_Count")
            return 0;
        return null;
    }

    private static object? Unwrap(object? value)
    {
        return value is RefCell cell ? cell.Value : value;
    }

    private static bool IsBlocked(string name)
    {
        return name.StartsWith("System.Windows.Forms", StringComparison.Ordinal)
            || name.StartsWith("System.Drawing", StringComparison.Ordinal)
            || name.StartsWith("System.Data", StringComparison.Ordinal)
            || name.IndexOf("SQLite", StringComparison.Ordinal) >= 0
            || name.IndexOf("OleDb", StringComparison.Ordinal) >= 0
            || name.StartsWith("System.Diagnostics.Process", StringComparison.Ordinal)
            || name.StartsWith("System.IO.File", StringComparison.Ordinal)
            || name.StartsWith("System.IO.Directory", StringComparison.Ordinal);
    }

    private static Type? ResolveType(ITypeDefOrRef type)
    {
        if (type is TypeSpec spec && spec.TypeSig is GenericInstSig generic)
        {
            Type? open = ResolveType(generic.GenericType.TypeDefOrRef);
            if (open == null)
                return null;
            Type[] arguments = generic.GenericArguments.Select(ResolveSig).ToArray();
            if (arguments.Any(item => item == null))
                return null;
            return open.MakeGenericType(arguments);
        }
        string name = type.FullName;
        return Type.GetType(name) ?? Type.GetType(name + ", System.Private.CoreLib") ?? Type.GetType(name + ", System.Runtime");
    }

    private static Type? ResolveSig(TypeSig sig)
    {
        switch (sig.ElementType)
        {
            case ElementType.String: return typeof(string);
            case ElementType.Boolean: return typeof(bool);
            case ElementType.Char: return typeof(char);
            case ElementType.I1: return typeof(sbyte);
            case ElementType.U1: return typeof(byte);
            case ElementType.I2: return typeof(short);
            case ElementType.U2: return typeof(ushort);
            case ElementType.I4: return typeof(int);
            case ElementType.U4: return typeof(uint);
            case ElementType.I8: return typeof(long);
            case ElementType.U8: return typeof(ulong);
            case ElementType.R4: return typeof(float);
            case ElementType.R8: return typeof(double);
            case ElementType.Object: return typeof(object);
            default:
                return Type.GetType(sig.FullName) ?? Type.GetType(sig.FullName + ", System.Private.CoreLib");
        }
    }

    private static object? ReadField(object? target, IField field)
    {
        string name = field.DeclaringType.FullName + "::" + field.Name;
        if (target == null && field.DeclaringType.IsValueType == false && !name.StartsWith("System.", StringComparison.Ordinal))
            return null;
        if (target is Slot slot)
            return slot.Get(name);
        if (target == null)
            return null;
        FieldInfo? info = target.GetType().GetField(field.Name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
        if (info == null)
            return null;
        return info.GetValue(info.IsStatic ? null : target);
    }

    private static void WriteField(object? target, IField field, object? value)
    {
        string name = field.DeclaringType.FullName + "::" + field.Name;
        if (target is Slot slot)
        {
            slot.Set(name, value);
            return;
        }
        if (target == null)
            return;
        FieldInfo? info = target.GetType().GetField(field.Name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
        info?.SetValue(info.IsStatic ? null : target, value);
    }

    private static object? Cast(object? value, ITypeDefOrRef type, bool throwOnMiss)
    {
        if (value == null)
            return null;
        Type? expected = ResolveType(type);
        if (expected == null || expected.IsInstanceOfType(value))
            return value;
        if (throwOnMiss)
            throw new InvalidCastException(type.FullName);
        return null;
    }

    private static void Binary(Stack<object?> stack, Code code)
    {
        object? right = stack.Pop();
        object? left = stack.Pop();
        if (left is string || right is string)
        {
            stack.Push(string.Concat(left, right));
            return;
        }
        if (left is decimal || right is decimal)
        {
            decimal a = Convert.ToDecimal(left, CultureInfo.InvariantCulture);
            decimal b = Convert.ToDecimal(right, CultureInfo.InvariantCulture);
            stack.Push(code switch
            {
                Code.Add => a + b,
                Code.Sub => a - b,
                Code.Mul => a * b,
                Code.Div => a / b,
                _ => a % b
            });
            return;
        }
        if (left is double || right is double || left is float || right is float)
        {
            double a = Convert.ToDouble(left, CultureInfo.InvariantCulture);
            double b = Convert.ToDouble(right, CultureInfo.InvariantCulture);
            stack.Push(code switch
            {
                Code.Add => a + b,
                Code.Sub => a - b,
                Code.Mul => a * b,
                Code.Div => a / b,
                _ => a % b
            });
            return;
        }
        long aInt = Convert.ToInt64(left, CultureInfo.InvariantCulture);
        long bInt = Convert.ToInt64(right, CultureInfo.InvariantCulture);
        long value = code switch
        {
            Code.Add => aInt + bInt,
            Code.Sub => aInt - bInt,
            Code.Mul => aInt * bInt,
            Code.Div => aInt / bInt,
            Code.Rem => aInt % bInt,
            Code.And => aInt & bInt,
            Code.Or => aInt | bInt,
            Code.Xor => aInt ^ bInt,
            Code.Shl => aInt << (int)bInt,
            _ => aInt >> (int)bInt
        };
        stack.Push(left is long || right is long ? value : (object)(int)value);
    }

    private static bool Compare(object? right, object? left, Code code)
    {
        if (code == Code.Ceq)
            return Equals(left, right) || NumericEquals(left, right);
        int order = CompareNumber(left, right);
        return code == Code.Cgt ? order > 0 : order < 0;
    }

    private static bool Branch(object? left, object? right, Code code)
    {
        if (code == Code.Beq || code == Code.Beq_S)
            return Equals(left, right) || NumericEquals(left, right);
        if (code == Code.Bne_Un || code == Code.Bne_Un_S)
            return !(Equals(left, right) || NumericEquals(left, right));
        int order = CompareNumber(left, right);
        if (code == Code.Bgt || code == Code.Bgt_S || code == Code.Bgt_Un || code == Code.Bgt_Un_S)
            return order > 0;
        if (code == Code.Bge || code == Code.Bge_S || code == Code.Bge_Un || code == Code.Bge_Un_S)
            return order >= 0;
        if (code == Code.Blt || code == Code.Blt_S || code == Code.Blt_Un || code == Code.Blt_Un_S)
            return order < 0;
        return order <= 0;
    }

    private static bool NumericEquals(object? left, object? right)
    {
        if (left == null || right == null || left is string || right is string)
            return false;
        try
        {
            return CompareNumber(left, right) == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static int CompareNumber(object? left, object? right)
    {
        decimal a = Convert.ToDecimal(left, CultureInfo.InvariantCulture);
        decimal b = Convert.ToDecimal(right, CultureInfo.InvariantCulture);
        return a.CompareTo(b);
    }

    private static bool IsTrue(object? value)
    {
        if (value == null)
            return false;
        if (value is bool bit)
            return bit;
        if (value is int number)
            return number != 0;
        if (value is long wider)
            return wider != 0;
        return true;
    }

    private static int FindHandler(MethodDef method, uint offset, Exception exception)
    {
        var handlers = method.Body.ExceptionHandlers;
        int best = -1;
        uint bestSpan = uint.MaxValue;
        for (int i = 0; i < handlers.Count; i++)
        {
            var handler = handlers[i];
            if (handler.TryStart == null || handler.TryEnd == null || handler.HandlerStart == null)
                continue;
            if (offset < handler.TryStart.Offset || offset >= handler.TryEnd.Offset)
                continue;
            bool matches = handler.HandlerType == ExceptionHandlerType.Finally
                || handler.HandlerType == ExceptionHandlerType.Fault
                || handler.CatchType == null
                || (Type.GetType(handler.CatchType.FullName) ?? typeof(Exception)).IsInstanceOfType(exception);
            if (!matches)
                continue;
            uint span = handler.TryEnd.Offset - handler.TryStart.Offset;
            if (span < bestSpan || (span == bestSpan && handler.HandlerType == ExceptionHandlerType.Catch))
            {
                best = i;
                bestSpan = span;
            }
        }
        return best;
    }
}


