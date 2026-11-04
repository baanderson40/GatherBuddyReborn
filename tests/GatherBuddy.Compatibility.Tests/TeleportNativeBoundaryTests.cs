using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using GatherBuddy.Crafting;
using Xunit;

namespace GatherBuddy.Compatibility.Tests;

public class TeleportNativeBoundaryTests
{
    private static readonly Dictionary<ushort, OpCode> OpCodesByValue = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => unchecked((ushort)code.Value));

    [Fact]
    public void NativeTeleportRefreshHasOnlyOneCentralizedCallSite()
    {
        using var file = File.OpenRead(typeof(CraftingQueueProgress).Assembly.Location);
        using var pe = new PEReader(file);
        var metadata = pe.GetMetadataReader();
        var callers = metadata.MethodDefinitions.Where(method => Calls(pe, metadata, method).Contains("UpdateAetheryteList")).ToArray();
        var caller = Assert.Single(callers);
        var type = metadata.GetMethodDefinition(caller).GetDeclaringType();
        var owners = new List<string>();
        while (!type.IsNil)
        {
            var definition = metadata.GetTypeDefinition(type);
            owners.Add(metadata.GetString(definition.Name));
            type = definition.GetDeclaringType();
        }
        Assert.Contains("TeleportData", owners); // Includes compiler-generated refresh lambda.
    }

    [Theory]
    [InlineData("AutoGather", "get_DeferAutoRetainerForTimedNode", "TryReadTimedNodeDeferral")]
    [InlineData("ActiveItemList", "TryReadTimedNodeDeferral", "TryRead")]
    [InlineData("Teleporter", "IsAttuned", "TryGetCachedCosts")]
    public void ActualReadOnlyInspectionCannotInvokeRefreshingSelectorsOrInventory(string typeName, string methodName, string expectedCall)
    {
        using var file = File.OpenRead(typeof(CraftingQueueProgress).Assembly.Location);
        using var pe = new PEReader(file);
        var metadata = pe.GetMetadataReader();
        var type = Assert.Single(metadata.TypeDefinitions, handle => metadata.GetString(metadata.GetTypeDefinition(handle).Name) == typeName);
        var method = Assert.Single(metadata.GetTypeDefinition(type).GetMethods(),
            handle => metadata.GetString(metadata.GetMethodDefinition(handle).Name) == methodName);
        var calls = Calls(pe, metadata, method).ToArray();
        Assert.Contains(expectedCall, calls);
        foreach (var forbidden in new[] { "GetNextOrDefault", "get_CurrentOrDefault", "NeedsGathering", "UpdateAetheryteList", "TryGetCosts", "DoUpdate", "UpdateTeleportationCosts" })
            Assert.DoesNotContain(forbidden, calls);
    }

    [Theory]
    [InlineData("Teleport")]
    [InlineData("TeleportUnchecked")]
    public void NativeTeleportEntrypointsCheckReadinessBeforeInstanceAccess(string name)
    {
        using var file = File.OpenRead(typeof(CraftingQueueProgress).Assembly.Location);
        using var pe = new PEReader(file);
        var metadata = pe.GetMetadataReader();
        var type = Assert.Single(metadata.TypeDefinitions, handle => metadata.GetString(metadata.GetTypeDefinition(handle).Name) == "Teleporter");
        var method = Assert.Single(metadata.GetTypeDefinition(type).GetMethods(),
            handle => metadata.GetString(metadata.GetMethodDefinition(handle).Name) == name);
        var calls = Calls(pe, metadata, method).ToList();
        Assert.Contains("get_IsReady", calls);
        Assert.Contains("Instance", calls);
        Assert.True(calls.IndexOf("get_IsReady") < calls.IndexOf("Instance"));
        Assert.DoesNotContain("UpdateAetheryteList", calls);
    }

    internal static IEnumerable<string> Calls(PEReader pe, MetadataReader metadata, MethodDefinitionHandle handle)
    {
        var method = metadata.GetMethodDefinition(handle);
        if (method.RelativeVirtualAddress == 0) yield break;
        var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILReader();
        while (il.RemainingBytes > 0)
        {
            var first = il.ReadByte();
            var opcode = OpCodesByValue[first == 0xfe ? (ushort)(0xfe00 | il.ReadByte()) : first];
            if (opcode.OperandType == OperandType.InlineMethod)
            {
                var token = MetadataTokens.EntityHandle(il.ReadInt32());
                if (token.Kind == HandleKind.MethodSpecification) token = metadata.GetMethodSpecification((MethodSpecificationHandle)token).Method;
                if (token.Kind == HandleKind.MemberReference) yield return metadata.GetString(metadata.GetMemberReference((MemberReferenceHandle)token).Name);
                else if (token.Kind == HandleKind.MethodDefinition) yield return metadata.GetString(metadata.GetMethodDefinition((MethodDefinitionHandle)token).Name);
                continue;
            }
            var size = opcode.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => il.ReadInt32() * 4,
                _ => 4,
            };
            il.Offset += size;
        }
    }
}
