using LibreHardwareMonitor.Hardware;

namespace GlanceBar;

// Reads CPU sensors in the privileged service through the installed PawnIO driver.
internal sealed class CpuTemperatureSampler : IDisposable
{
    private readonly Computer _computer = new() { IsCpuEnabled = true };

    // Open only CPU hardware, so unrelated sensors are not polled.
    internal CpuTemperatureSampler()
    {
        _computer.Open();
    }

    // Prefer a package/die sensor; otherwise report the hottest available CPU core.
    // For multiple CPUs, report the highest chosen temperature.
    internal double? Sample()
    {
        double? hottest = null;
        foreach (var hardware in _computer.Hardware)
        {
            if (hardware.HardwareType != HardwareType.Cpu)
            {
                continue;
            }

            hardware.Update();
            double? preferred = null;
            double? cores = null;
            foreach (var sensor in hardware.Sensors)
            {
                if (sensor.SensorType != SensorType.Temperature || sensor.Value is not float value || !float.IsFinite(value))
                {
                    continue;
                }

                if (sensor.Name is "CPU Package" or "Core (Tdie)" or "Core (Tctl/Tdie)" or "CPU Cores")
                {
                    preferred = Math.Max(preferred ?? value, value);
                }
                else if (sensor.Name is "Core Max" or "CPU Core" ||
                         sensor.Name.StartsWith("CPU Core #", StringComparison.Ordinal) ||
                         sensor.Name.StartsWith("P-Core #", StringComparison.Ordinal) ||
                         sensor.Name.StartsWith("E-Core #", StringComparison.Ordinal))
                {
                    cores = Math.Max(cores ?? value, value);
                }
            }

            var temperature = preferred ?? cores;
            if (temperature.HasValue)
            {
                hottest = Math.Max(hottest ?? temperature.Value, temperature.Value);
            }
        }
        return hottest;
    }

    // Release the library's hardware handles when the service stops.
    public void Dispose() => _computer.Close();
}
