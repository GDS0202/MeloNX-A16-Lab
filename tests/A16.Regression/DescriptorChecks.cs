using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

internal static class DescriptorChecks
{
    // Inspect the built production method, without loading Vulkan or calling a GPU.
    // This verifies removal of temporary arrays, not absence of every possible allocation.
    public static int Inspect(string path)
    {
        using FileStream file = File.OpenRead(path);
        using PEReader pe = new(file);
        MetadataReader metadata = pe.GetMetadataReader();
        Dictionary<ushort, OpCode> codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null))
            .GroupBy(o => unchecked((ushort)o.Value)).ToDictionary(g => g.Key, g => g.First());
        int methods = 0;
        int totalArrays = 0;
        foreach (TypeDefinitionHandle typeHandle in metadata.TypeDefinitions)
        {
            TypeDefinition type = metadata.GetTypeDefinition(typeHandle);
            if (metadata.GetString(type.Name) != "DescriptorSetUpdater") continue;
            foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
            {
                MethodDefinition method = metadata.GetMethodDefinition(methodHandle);
                if (metadata.GetString(method.Name) != "UpdateAndBind") continue;
                byte[] il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
                int arrays = 0;
                for (int position = 0; position < il.Length;)
                {
                    ushort value = il[position++];
                    if (value == 0xfe) value = (ushort)(0xfe00 | il[position++]);
                    OpCode code = codes[value];
                    if (code == OpCodes.Newarr) arrays++;
                    position += code.OperandType switch
                    {
                        OperandType.InlineNone => 0,
                        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                        OperandType.InlineVar => 2,
                        OperandType.InlineI8 or OperandType.InlineR => 8,
                        OperandType.InlineSwitch => 4 + BitConverter.ToInt32(il, position) * 4,
                        _ => 4,
                    };
                }
                Console.WriteLine($"Production DescriptorSetUpdater.UpdateAndBind/{method.GetParameters().Count} parameters: {arrays} newarr instructions.");
                methods++;
                totalArrays += arrays;
            }
        }
        if (methods == 0) throw new Exception("Production descriptor method not found");
        Console.WriteLine($"Total: {totalArrays} newarr instructions across {methods} overloads.");
        return totalArrays == 0 ? 0 : 1;
    }
}
