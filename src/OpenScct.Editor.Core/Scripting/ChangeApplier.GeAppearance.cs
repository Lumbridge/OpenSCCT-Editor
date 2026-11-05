using OpenScct.Content.Json;
using OpenScct.Editor.Core.Gameplay;
using OpenScct.Editor.Core.Model;

namespace OpenScct.Editor.Core.Scripting;

public static partial class ChangeApplier
{
    private static void SetGeAppearance(SvJsonObject op, Ge ge)
    {
        if (op["appearance"] is { } value)
        {
            if (value.Kind is SvJsonKind.False or SvJsonKind.Null)
            {
                ge.Appearance = null;
                return;
            }

            if (!GeGeometry.Supports(ge.Kind))
            {
                throw new ApiException($"{ge.Kind} has no generated appearance", "appearance");
            }

            GeAppearance appearance = ge.Appearance?.Clone() ?? GeGeometry.Defaults(ge.Kind);
            if (value is SvJsonObject fields)
            {
                foreach ((string key, SvJsonNode field) in fields)
                {
                    switch (key)
                    {
                        case "width": appearance.Width = ApiJson.RequireNumber(field, "appearance.width"); break;
                        case "radius": appearance.Radius = ApiJson.RequireNumber(field, "appearance.radius"); break;
                        case "spacing": appearance.Spacing = ApiJson.RequireNumber(field, "appearance.spacing"); break;
                        case "material":
                            appearance.Material = field is SvJsonString material ? material.Value
                                : field.Kind == SvJsonKind.Null ? null
                                : throw new ApiException("material is an asset path or null", "appearance.material");
                            break;
                        default: throw new ApiException($"unknown appearance field '{key}'", "appearance." + key);
                    }
                }
            }
            else if (value.Kind != SvJsonKind.True)
            {
                throw new ApiException("appearance is true, false, null or an object with width, radius, spacing and material", "appearance");
            }

            ge.Appearance = appearance;
        }

        if (ge.Appearance is not null)
        {
            if (!GeGeometry.Supports(ge.Kind))
            {
                throw new ApiException("clear appearance when changing to a GE kind without generated geometry", "appearance");
            }

            try
            {
                if (GeGeometry.Build(ge).Count == 0)
                {
                    throw new ArgumentException("appearance needs a non-zero GE segment");
                }
            }
            catch (ArgumentException ex)
            {
                throw new ApiException(ex.Message, "appearance");
            }
        }
    }
}
