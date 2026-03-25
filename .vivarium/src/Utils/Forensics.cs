//@VIVARIUM@
//@description: Session introspection utilities

public static class Forensics
{
    public static string Fingerprint()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== Vivarium Session Fingerprint ===");
        sb.AppendLine($"CLR Version:    {Environment.Version}");
        sb.AppendLine($"OS:             {Environment.OSVersion}");
        sb.AppendLine($"Machine:        {Environment.MachineName}");
        sb.AppendLine($"Processors:     {Environment.ProcessorCount}");
        sb.AppendLine($"64-bit OS:      {Environment.Is64BitOperatingSystem}");
        sb.AppendLine($"Working Set:    {Environment.WorkingSet / 1024 / 1024} MB");
        sb.AppendLine($"Loaded Asms:    {AppDomain.CurrentDomain.GetAssemblies().Length}");
        return sb.ToString();
    }
}