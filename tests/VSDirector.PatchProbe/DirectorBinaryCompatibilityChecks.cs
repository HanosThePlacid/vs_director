using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

internal static class DirectorBinaryCompatibilityChecks
{
    internal static void Run(Assembly assembly)
    {
        var ops = typeof(OpCodes).GetFields().Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => unchecked((ushort)o.Value));
        var errors = new Dictionary<string, HashSet<string>>();
        using var stream = File.OpenRead(assembly.Location);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        int count = 0;
        foreach (var type in assembly.GetTypes())
        foreach (var method in type.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly).Cast<MethodBase>()
            .Concat(type.GetConstructors(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static)))
        {
            var il = method.GetMethodBody()?.GetILAsByteArray();
            if (il is null) continue;
            count++;
            for (int p = 0; p < il.Length;)
            {
                ushort code = il[p++]; if (code == 0xfe) code = (ushort)(0xfe00 | il[p++]);
                var op = ops[code];
                if (op.OperandType is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok)
                {
                    try { method.Module.ResolveMember(BitConverter.ToInt32(il, p), type.GetGenericArguments(), method.IsGenericMethod ? method.GetGenericArguments() : null); }
                    catch(Exception e)
                    {
                        string detail = e.Message;
                        var handle = MetadataTokens.Handle(BitConverter.ToInt32(il,p));
                        if (handle.Kind == HandleKind.MemberReference)
                        {
                            var reference = metadata.GetMemberReference((MemberReferenceHandle)handle);
                            detail += " MEMBER=" + metadata.GetString(reference.Name);
                        }
                        if (!errors.TryGetValue(detail, out var methods)) errors[detail] = methods = new();
                        methods.Add(type.FullName + "." + method.Name);
                    }
                }
                p += op.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il,p),
                    _ => 4
                };
            }
        }
        Console.WriteLine($"MEMBER AUDIT {assembly.GetName().Name}: {count} methods, {errors.Count} distinct errors");
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors.Select(error => error.Key + " in " + string.Join(", ", error.Value))));
    }
}
