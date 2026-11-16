using OpenScct.Editor.Core.Services;

namespace OpenScct.Editor.Core.Classes;

/// <summary>Supported runtime fields that supplement the original UC metadata.</summary>
internal static class RuntimePropertySchemas
{
    public static void Add(CatalogData data)
    {
        void Fields(string owner, params (string Name, string Type, string? Class)[] fields)
        {
            if (!data.ByPath.TryGetValue(owner, out ClassInfo? cls)) return;
            var properties = cls.Properties.ToList();
            foreach (var field in fields)
            {
                properties.RemoveAll(p => p.Name.Equals(field.Name, StringComparison.OrdinalIgnoreCase));
                properties.Add(new PropInfo(field.Name, field.Type, "PS2 objective", true, null, null, field.Class, 1, null, owner));
            }
            data.ByPath[owner] = cls with { Properties = properties };
        }
        Fields("SBase.SObjectiveTrigger", ("bPS2Objective", "bool", null), ("Ps2Computer", "object", "SBase.SComputerObjectiveTrigger"));
        Fields("SBase.SFlag", ("Ps2SourceStaticMesh", "object", "Engine.StaticMesh"), ("Ps2InteractionDistance", "float", null));
        Fields("SBase.SComputerObjectiveTrigger",
            ("Ps2SkinActive", "object", "Engine.Material"), ("Ps2SkinInactive", "object", "Engine.Material"),
            ("Ps2SkinHacked", "object", "Engine.Material"), ("Ps2SkinKeyboard", "object", "Engine.Material"),
            ("Ps2SkinKeyboardHacked", "object", "Engine.Material"), ("Ps2HackAnimation", "name", null),
            ("Ps2HackedAnimation", "name", null), ("Ps2AlarmAnimation", "name", null), ("Ps2CtfAnimation", "name", null));
    }
}
