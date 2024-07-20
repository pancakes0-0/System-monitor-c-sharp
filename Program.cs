using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using ClickableTransparentOverlay;
using System.Net.NetworkInformation;
using System.ServiceProcess;
using System.Management;

class Program
{
    static void Main(string[] args)
    {
        using (var overlay = new SystemMonitorOverlay())
        {
            overlay.Start();
        }
    }
}

class SystemMonitorOverlay : Overlay
{
    private const int MaxDataPoints = 100;
    private List<float> cpuUsageHistory = new List<float>();
    private List<float> ramUsageHistory = new List<float>();
    private PerformanceCounter cpuCounter;
    private bool isVisible = true;
    private string searchTerm = "";
    private List<ProcessInfo> filteredProcesses = new List<ProcessInfo>(); // Change type to ProcessInfo
    private List<ServiceController> filteredServices = new List<ServiceController>();
    private DateTime lastUpdateTime = DateTime.MinValue;
    private const int UpdateIntervalMs = 1000; // 1 second delay


    private class ProcessInfo
    {
        public Process InfoProcess { get; set; }
        public bool IsService { get; set; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
        public MEMORYSTATUSEX()
        {
            this.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        }
    }

    [return: MarshalAs(UnmanagedType.Bool)]
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);
    [DllImport("shell32.dll", SetLastError = true)]
    public static extern IntPtr ShellExecute(IntPtr hwnd, string lpOperation, string lpFile, string lpParameters, string lpDirectory, int nShowCmd);


    public SystemMonitorOverlay()
    {
        cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
        UpdateFilteredLists();
    }
    protected override void Render()
    {
        ImGui.SetNextWindowSize(new Vector2(600, 400), ImGuiCond.FirstUseEver);
        ImGui.Begin("System Monitor", ref isVisible);

        RenderSearchBar();

        if (ImGui.BeginTabBar("Tabs"))
        {
            if (ImGui.BeginTabItem("Performance"))
            {
                RenderPerformanceTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Processes"))
            {
                RenderProcessesTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Services"))
            {
                RenderServicesTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Network"))
            {
                RenderNetworkTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        ImGui.End();
    }

    private void RenderSearchBar()
    {
        ImGui.InputText("Search", ref searchTerm, 100);
        if (ImGui.IsItemEdited())
        {
            UpdateFilteredLists();
        }
    }

    private HashSet<int> GetServiceProcessIds()
    {
        var serviceProcessIds = new HashSet<int>();
        using (var searcher = new ManagementObjectSearcher("SELECT ProcessId FROM Win32_Service"))
        {
            foreach (ManagementObject obj in searcher.Get())
            {
                if (obj["ProcessId"] != null)
                {
                    serviceProcessIds.Add(Convert.ToInt32(obj["ProcessId"]));
                }
            }
        }
        return serviceProcessIds;
    }

    private void UpdateFilteredLists()
    {
        var allProcesses = Process.GetProcesses();
        var serviceProcessIds = GetServiceProcessIds();

        filteredProcesses = allProcesses
            .Where(p => p.ProcessName.ToLower().Contains(searchTerm.ToLower()))
            .Select(p => new ProcessInfo
            {
                InfoProcess = p,
                IsService = serviceProcessIds.Contains(p.Id)
            })
            .ToList();

        filteredServices = ServiceController.GetServices()
            .Where(s => s.ServiceName.ToLower().Contains(searchTerm.ToLower()))
            .ToList();
    }


    private void RenderPerformanceTab()
    {
        UpdatePerformanceData();

        ImGui.Text($"CPU Usage: {cpuUsageHistory.LastOrDefault():F1}%");
        ImGui.Text($"RAM Usage: {ramUsageHistory.LastOrDefault():F1}%");

        var cpuPlotValues = cpuUsageHistory.ToArray();
        var ramPlotValues = ramUsageHistory.ToArray();

        ImGui.PlotLines("CPU Usage", ref cpuPlotValues[0], cpuPlotValues.Length, 0, null, 0, 100, new Vector2(0, 80));
        ImGui.PlotLines("RAM Usage", ref ramPlotValues[0], ramPlotValues.Length, 0, null, 0, 100, new Vector2(0, 80));
    }

    private void UpdatePerformanceData()
    {
        if ((DateTime.Now - lastUpdateTime).TotalMilliseconds < UpdateIntervalMs)
        {
            return;
        }

        float cpuUsage = cpuCounter.NextValue();
        cpuUsageHistory.Add(cpuUsage);
        if (cpuUsageHistory.Count > MaxDataPoints) cpuUsageHistory.RemoveAt(0);

        float ramUsage = GetRamUsage();
        ramUsageHistory.Add(ramUsage);
        if (ramUsageHistory.Count > MaxDataPoints) ramUsageHistory.RemoveAt(0);

        lastUpdateTime = DateTime.Now;
    }

    private float GetRamUsage()
    {
        var memStatus = new MEMORYSTATUSEX();
        if (GlobalMemoryStatusEx(memStatus))
        {
            return 100f * (1 - (float)memStatus.ullAvailPhys / memStatus.ullTotalPhys);
        }
        return 0;
    }

    private void RenderProcessesTab()
    {
        if (ImGui.BeginTable("ProcessesTable", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Name");
            ImGui.TableSetupColumn("ID");
            ImGui.TableSetupColumn("Memory (MB)");
            ImGui.TableSetupColumn("Type");
            ImGui.TableHeadersRow();

            foreach (var processInfo in filteredProcesses)
            {
                var process = processInfo.InfoProcess; // Adjust usage
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                if (ImGui.Selectable(process.ProcessName, false, ImGuiSelectableFlags.SpanAllColumns))
                {
                    ImGui.OpenPopup($"ProcessOptions_{process.Id}");
                }
                ImGui.TableSetColumnIndex(1);
                ImGui.Text(process.Id.ToString());
                ImGui.TableSetColumnIndex(2);
                ImGui.Text($"{process.WorkingSet64 / 1024 / 1024:F2}");
                ImGui.TableSetColumnIndex(3);
                ImGui.Text(processInfo.IsService ? "Service" : "Application");

                if (ImGui.BeginPopup($"ProcessOptions_{process.Id}"))
                {
                    if (ImGui.MenuItem("View Properties"))
                    {
                        try
                        {
                            ShellExecute(IntPtr.Zero, "properties", process.MainModule.FileName, null, null, 1);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Failed to view properties: {ex.Message}");
                        }
                    }
                    if (ImGui.MenuItem("End Task"))
                    {
                        try
                        {
                            process.Kill();
                            UpdateFilteredLists();
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Failed to end task: {ex.Message}");
                        }
                    }
                    if (ImGui.MenuItem("Open File Location"))
                    {
                        try
                        {
                            string filePath = process.MainModule.FileName;
                            Process.Start("explorer.exe", $"/select,\"{filePath}\"");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Failed to open file location: {ex.Message}");
                        }
                    }
                    ImGui.EndPopup();
                }
            }

            ImGui.EndTable();
        }
    }

    private void RenderServicesTab()
    {
        if (ImGui.BeginTable("ServicesTable", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Name");
            ImGui.TableSetupColumn("Status");
            ImGui.TableHeadersRow();

            foreach (var service in filteredServices)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.Text(service.ServiceName);
                ImGui.TableSetColumnIndex(1);
                ImGui.Text(service.Status.ToString());
            }

            ImGui.EndTable();
        }
    }

    private void RenderNetworkTab()
    {
        var networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();

        foreach (var ni in networkInterfaces)
        {
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            ImGui.Text($"Interface: {ni.Name}");
            ImGui.Text($"Type: {ni.NetworkInterfaceType}");
            ImGui.Text($"Status: {ni.OperationalStatus}");

            var stats = ni.GetIPv4Statistics();
            ImGui.Text($"Bytes Sent: {FormatBytes(stats.BytesSent)}");
            ImGui.Text($"Bytes Received: {FormatBytes(stats.BytesReceived)}");

            ImGui.Separator();
        }
    }

    private string FormatBytes(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024) >= 1)
        {
            number = number / 1024;
            counter++;
        }
        return string.Format("{0:n1}{1}", number, suffixes[counter]);
    }
}