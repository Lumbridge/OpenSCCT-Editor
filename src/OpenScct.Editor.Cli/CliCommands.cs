using System.Reflection;

namespace OpenScct.Editor.Cli;

/// <summary>Marks a static method returning <c>IEnumerable&lt;CliCommand&gt;</c>: each feature file adds its own
/// commands this way, so features never edit a shared list.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class CliProviderAttribute : Attribute
{
}

public static class CliCommands
{
    public static IEnumerable<CliCommand> All() =>
        typeof(CliCommands).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Where(m => m.GetCustomAttribute<CliProviderAttribute>() is not null)
            .OrderBy(m => m.DeclaringType!.FullName, StringComparer.Ordinal)
            .SelectMany(m => (IEnumerable<CliCommand>)m.Invoke(null, null)!);
}
