namespace Curato.iCloud;

/// <summary>
/// Where to find the `icloudpd` CLI this feature shells out to (PLAN.md §4:
/// MIT-licensed, invoked as an external process rather than ported into
/// C#). Configurable since the executable name/path can differ between the
/// Docker image (guaranteed on PATH — see Dockerfile) and a local Windows
/// dev run (pip installs its console-script shims to a per-user Scripts
/// folder that isn't always already on PATH).
/// </summary>
public sealed class iCloudProcessOptions
{
    public string IcloudpdExecutable { get; init; } = "icloudpd";
}
