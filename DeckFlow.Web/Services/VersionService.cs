using System.Reflection;

namespace DeckFlow.Web.Services;

/// <summary>
/// Reads the running assembly's <see cref="AssemblyInformationalVersionAttribute"/> when present,
/// falling back to <see cref="AssemblyName.Version"/> and finally to "unknown".
/// </summary>
public sealed class VersionService : IVersionService
{
    private readonly Assembly _assembly;

    public VersionService() : this(typeof(VersionService).Assembly) { }

    public VersionService(Assembly assembly) => _assembly = assembly;

    public string GetVersion()
    {
        var informational = _assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // Strip the "+<commitHash>" suffix that the SDK appends.
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }

        var name = _assembly.GetName().Version?.ToString();
        return string.IsNullOrWhiteSpace(name) ? "unknown" : name!;
    }
}
