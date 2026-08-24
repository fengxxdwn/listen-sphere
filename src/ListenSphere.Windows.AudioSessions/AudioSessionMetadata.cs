namespace ListenSphere.Windows.AudioSessions;

public static class AudioSessionMetadata
{
    public static string ResolveDisplayName(
        string? sessionDisplayName,
        bool isSystemSounds,
        string? fileDescription,
        string? processName,
        int processId)
    {
        if (isSystemSounds)
        {
            return "系统声音";
        }

        if (!string.IsNullOrWhiteSpace(sessionDisplayName))
        {
            return sessionDisplayName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(fileDescription))
        {
            return fileDescription.Trim();
        }

        if (!string.IsNullOrWhiteSpace(processName))
        {
            return processName.Trim();
        }

        return processId > 0 ? $"进程 {processId}" : "未知应用";
    }
}

