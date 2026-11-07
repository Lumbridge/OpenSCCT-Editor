using System.Text.Json;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Model;
using OpenScct.Editor.Core.Stairs;

namespace OpenScct.Editor.Core.Scripting;

public static partial class ChangeApplier
{
    private static void StairOp(ChangeContext ctx, SvJsonObject op)
    {
        string? id = ApiJson.GetString(op, "id");
        if (ApiJson.GetBool(op, "convert", false) || ApiJson.GetBool(op, "breakApart", false))
        {
            if (id is null) throw new ApiException("give the selected stair id", "id");
            var r = ctx.Project.Find(id) ?? throw new ApiException("object not found", "id"); ctx.Touch(r);
            var replacement = (StairCommands.StairReplacement)(ApiJson.GetBool(op, "convert", false) ? StairCommands.Convert(ctx.Project, id) : StairCommands.BreakApart(ctx.Project, id));
            foreach (Brush removed in replacement.Removed) ctx.Touch(ObjectRef.ForBrush(removed));
            ctx.Run(replacement); foreach (ObjectRef added in replacement.Added) ctx.Created(added); return;
        }
        void Update(StairDesign design)
        {
            if (op["set"] is null) return;
            if (op["set"] is not SvJsonObject values) throw new ApiException("set must be an object of stair properties", "set");
            foreach (var (key, value) in values)
            {
                var prop = typeof(StairDesign).GetProperties().FirstOrDefault(p => p.CanWrite && string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ApiException($"unknown stair property '{key}'", "set." + key);
                try { prop.SetValue(design, JsonSerializer.Deserialize(ApiJson.Write(value), prop.PropertyType)); }
                catch (JsonException) { throw new ApiException($"invalid value for stair {key}", "set." + key); }
            }
        }
        if (id is not null && ctx.Project.FindBrush(id) is { Stair: not null } existing)
        {
            ctx.Touch(ObjectRef.Stair(id)); ctx.Run(StairCommands.Edit(ctx.Project, id, Update)); return;
        }
        var s = new StairDesign(); Update(s);
        Vec3 at = op["at"] is { } point ? ApiJson.RequireVec(point, "at") : Vec3.Zero;
        Rot rot = op["rot"] is { } rotation && ApiJson.TryRot(rotation, out Rot parsed) ? parsed : Rot.Zero;
        Brush stair = op["connectTo"] is { } top ? StairFit.Between(id ?? StairCommands.NewId(ctx.Project), s, at, ApiJson.RequireVec(top, "connectTo"))
            : StairGeometry.Create(id ?? StairCommands.NewId(ctx.Project), s, at, rot);
        if (ctx.Project.Find(stair.Id) is not null) throw new ApiException("id already exists", "id");
        if (s.Carve && Compile.BakedContent.BakedWorldKept(ctx.Project)) throw new ApiException("disable clearance cutting in retained baked worlds", "set.carve");
        ctx.Add("Place staircase", brushes: [stair]);
    }
}
