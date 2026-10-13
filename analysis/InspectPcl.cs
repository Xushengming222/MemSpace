using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Text;

// Metadata and IL only: ReflectionOnlyLoad cannot execute PCL code.
internal static class InspectPcl
{
    private static Dictionary<ushort, OpCode> codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)).ToDictionary(o => (ushort)o.Value);
    private static int Main(string[] args)
    {
        if (args.Length < 2) return 1;
        AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += delegate(object sender, ResolveEventArgs e) {
            try { return Assembly.ReflectionOnlyLoad(e.Name); } catch { return null; }
        };
        var b = new StringBuilder();
        try {
            var a = Assembly.ReflectionOnlyLoadFrom(args[0]);
            b.AppendLine("ASSEMBLY " + a.FullName);
            foreach (string r in a.GetManifestResourceNames()) b.AppendLine("RESOURCE " + r);
            Type[] types;
            try { types = a.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); foreach (var error in e.LoaderExceptions.Take(10)) b.AppendLine("TYPE ERROR " + error.Message); }
            foreach (var type in types)
            {
                MethodInfo[] methods;
                try { methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly); } catch { continue; }
                foreach (var m in methods)
                {
                    bool nameMatch = m.Name.IndexOf("Memory", StringComparison.OrdinalIgnoreCase) >= 0 || m.Name.IndexOf("WorkingSet", StringComparison.OrdinalIgnoreCase) >= 0 || m.Name.IndexOf("NtSet", StringComparison.OrdinalIgnoreCase) >= 0 || m.Name.IndexOf("CacheSize", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (args.Length > 2) nameMatch = m.Name.IndexOf(args[2], StringComparison.OrdinalIgnoreCase) >= 0 || type.FullName.IndexOf(args[2], StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!nameMatch) continue;
                    b.AppendLine("\r\nMETHOD " + type.FullName + "::" + m.Name + " TOKEN 0x" + m.MetadataToken.ToString("x8"));
                    try { foreach (var attr in CustomAttributeData.GetCustomAttributes(m)) if (attr.AttributeType.Name == "DllImportAttribute") b.AppendLine("PINVOKE " + attr); } catch { }
                    try { Dump(m, b); } catch (Exception e) { b.AppendLine("IL ERROR " + e.Message); }
                }
            }
            File.WriteAllText(args[1], b.ToString(), Encoding.UTF8); return 0;
        } catch (Exception e) { File.WriteAllText(args[1], b + e.ToString(), Encoding.UTF8); return 1; }
    }
    private static void Dump(MethodInfo m, StringBuilder b)
    {
        var body = m.GetMethodBody(); if (body == null) return; byte[] il = body.GetILAsByteArray();
        int index = 0;
        while (index < il.Length)
        {
            int offset = index; ushort value = il[index++]; if (value == 0xfe) value = (ushort)(0xfe00 | il[index++]);
            OpCode code = codes[value]; string operand = "";
            switch (code.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineI: operand = ((sbyte)il[index++]).ToString(); break;
                case OperandType.ShortInlineVar: operand = il[index++].ToString(); break;
                case OperandType.InlineVar: operand = BitConverter.ToUInt16(il, index).ToString(); index += 2; break;
                case OperandType.InlineI: operand = BitConverter.ToInt32(il, index).ToString(); index += 4; break;
                case OperandType.InlineI8: operand = BitConverter.ToInt64(il, index).ToString(); index += 8; break;
                case OperandType.ShortInlineR: operand = BitConverter.ToSingle(il, index).ToString(); index += 4; break;
                case OperandType.InlineR: operand = BitConverter.ToDouble(il, index).ToString(); index += 8; break;
                case OperandType.ShortInlineBrTarget: { int delta = (sbyte)il[index++]; operand = "IL_" + (index + delta).ToString("x4"); break; }
                case OperandType.InlineBrTarget: { int delta = BitConverter.ToInt32(il, index); index += 4; operand = "IL_" + (index + delta).ToString("x4"); break; }
                case OperandType.InlineSwitch: { int n = BitConverter.ToInt32(il, index); index += 4; operand = "[" + n + " targets]"; index += n * 4; break; }
                default:
                    int token = BitConverter.ToInt32(il, index); index += 4;
                    try {
                        if (code.OperandType == OperandType.InlineString) operand = "\"" + m.Module.ResolveString(token).Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
                        else { var member = m.Module.ResolveMember(token); operand = (member.DeclaringType == null ? "" : member.DeclaringType.FullName + "::") + member; }
                    } catch { operand = "TOKEN 0x" + token.ToString("x8"); }
                    break;
            }
            b.AppendLine("IL_" + offset.ToString("x4") + " " + code.Name + " " + operand);
        }
    }
}
