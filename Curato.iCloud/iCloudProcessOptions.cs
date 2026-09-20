namespace Curato.iCloud;

/// <summary>
/// Where to find the Python tooling this feature shells out to (PLAN.md §4:
/// icloudpd/pyicloud, MIT-licensed, invoked as external processes rather
/// than ported into C#). All three are configurable since the executable
/// names/paths differ between the Docker image (where they're guaranteed to
/// exist on PATH — see Dockerfile) and a local Windows dev run (where
/// "python3" may need to be "python", and the auth helper script's default
/// path assumes <see cref="Curato.Api"/>'s build output has a sibling
/// "tools/icloud-sync" folder — see Curato.Api.csproj's Content item that
/// copies it there, mirroring what the Dockerfile COPYs into the image).
/// </summary>
public sealed class iCloudProcessOptions
{
    public string PythonExecutable { get; init; } = "python3";
    public string IcloudpdExecutable { get; init; } = "icloudpd";
    public string AuthHelperScriptPath { get; init; } =
        Path.Combine(AppContext.BaseDirectory, "tools", "icloud-sync", "auth_helper.py");
}
