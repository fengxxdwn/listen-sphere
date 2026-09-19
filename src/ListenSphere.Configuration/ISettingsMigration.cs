namespace ListenSphere.Configuration;

public interface ISettingsMigration
{
    int SourceVersion { get; }
    int TargetVersion { get; }
    ListenSphereSettings Migrate(ListenSphereSettings settings);
}
