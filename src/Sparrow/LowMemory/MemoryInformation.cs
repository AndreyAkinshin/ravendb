﻿using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Sparrow.Logging;
using Sparrow.Platform;
using Sparrow.Platform.Posix;
using Sparrow.Platform.Win32;
using Sparrow.Utils;

namespace Sparrow.LowMemory
{
    public static class MemoryInformation
    {
        private static readonly Logger Logger = LoggingSource.Instance.GetLogger<MemoryInfoResult>("Raven/Server");

        private static readonly ConcurrentQueue<Tuple<long, DateTime>> MemByTime = new ConcurrentQueue<Tuple<long, DateTime>>();
        private static DateTime _memoryRecordsSet;
        private static readonly TimeSpan MemByTimeThrottleTime = TimeSpan.FromMilliseconds(100);

        public static byte[] VmRss = Encoding.UTF8.GetBytes("VmRSS:");
        public static byte[] MemAvailable = Encoding.UTF8.GetBytes("MemAvailable:");
        public static byte[] MemFree = Encoding.UTF8.GetBytes("MemFree:");
        public static byte[] Committed_AS = Encoding.UTF8.GetBytes("Committed_AS:");
        public static byte[] CommitLimit = Encoding.UTF8.GetBytes("CommitLimit:");

        public static long HighLastOneMinute;
        public static long LowLastOneMinute = long.MaxValue;
        public static long HighLastFiveMinutes;
        public static long LowLastFiveMinutes = long.MaxValue;
        public static long HighSinceStartup;
        public static long LowSinceStartup = long.MaxValue;


        private static bool _failedToGetAvailablePhysicalMemory;
        private static readonly MemoryInfoResult FailedResult = new MemoryInfoResult
        {
            AvailableMemory = new Size(256, SizeUnit.Megabytes),
            TotalPhysicalMemory = new Size(256, SizeUnit.Megabytes),
            TotalCommittableMemory = new Size(384, SizeUnit.Megabytes),// also include "page file"
            CurrentCommitCharge = new Size(256, SizeUnit.Megabytes),
            InstalledMemory = new Size(256, SizeUnit.Megabytes),
            MemoryUsageRecords =
            new MemoryInfoResult.MemoryUsageLowHigh
            {
                High = new MemoryInfoResult.MemoryUsageIntervals
                {
                    LastFiveMinutes = new Size(0, SizeUnit.Bytes),
                    LastOneMinute = new Size(0, SizeUnit.Bytes),
                    SinceStartup = new Size(0, SizeUnit.Bytes)
                },
                Low = new MemoryInfoResult.MemoryUsageIntervals
                {
                    LastFiveMinutes = new Size(0, SizeUnit.Bytes),
                    LastOneMinute = new Size(0, SizeUnit.Bytes),
                    SinceStartup = new Size(0, SizeUnit.Bytes)
                }
            }
        };

        [StructLayout(LayoutKind.Sequential)]
        public struct MemoryStatusEx
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
        }

        private static bool IsRunningOnDocker =>
          string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RAVEN_IN_DOCKER")) == false;

        public static void AssertNotAboutToRunOutOfMemory(float minimumFreeCommittedMemory)
        {
            if (IsRunningOnDocker)
                return;

            // we are about to create a new thread, might not always be a good idea:
            // https://ayende.com/blog/181537-B/production-test-run-overburdened-and-under-provisioned
            // https://ayende.com/blog/181569-A/threadpool-vs-pool-thread

            var memInfo = GetMemoryInfo();
            Size overage;
            if(memInfo.CurrentCommitCharge > memInfo.TotalCommittableMemory)
            {
                // this can happen on containers, since we get this information from the host, and
                // sometimes this kind of stat is shared, see: 
                // https://fabiokung.com/2014/03/13/memory-inside-linux-containers/

                overage = 
                    (memInfo.TotalPhysicalMemory * minimumFreeCommittedMemory) +  //extra to keep free
                    (memInfo.TotalPhysicalMemory - memInfo.AvailableMemory);      //actually in use now
                if (overage >= memInfo.TotalPhysicalMemory)
                {
                    ThrowInsufficentMemory(memInfo);
                    return;
                }

                return;
            }

            overage = (memInfo.TotalCommittableMemory * minimumFreeCommittedMemory) + memInfo.CurrentCommitCharge;
            if (overage >= memInfo.TotalCommittableMemory)
            {
                ThrowInsufficentMemory(memInfo);
            }
        }

        private static void ThrowInsufficentMemory(MemoryInfoResult memInfo)
        {
            throw new InsufficientExecutionStackException($"The amount of available memory to commit on the system is low. Commit charge: {memInfo.CurrentCommitCharge} / {memInfo.TotalCommittableMemory}. Memory: {memInfo.TotalPhysicalMemory - memInfo.AvailableMemory} / {memInfo.TotalPhysicalMemory}" +
                $" Will not create a new thread in this situation because it may result in a stack overflow error when trying to allocate stack space but there isn't sufficient memory for that.");
        }

        [return: MarshalAs(UnmanagedType.Bool)]
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern unsafe bool GlobalMemoryStatusEx(MemoryStatusEx* lpBuffer);

        [return: MarshalAs(UnmanagedType.Bool)]
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetPhysicallyInstalledSystemMemory(out long totalMemoryInKb);

        public static long GetRssMemoryUsage(int processId)
        {
            var path = $"/proc/{processId}/status";
            
            try
            {
                using (var bufferedReader =new KernelVirtualFileSystemUtils.BufferedPosixKeyValueOutputValueReader(path))
                {
                    bufferedReader.ReadFileIntoBuffer();
                    var vmrss = bufferedReader.ExtractNumericValueFromKeyValuePairsFormattedFile(VmRss);
                    return vmrss * 1024;// value is in KB, we need to return bytes
                }
            }
            catch (Exception ex)
            {
                if (Logger.IsInfoEnabled)
                    Logger.Info($"Failed to read value from {path}",ex);
                return -1;
            }
        }

        public static (long MemAvailable, long MemFree) GetAvailableAndFreeMemoryFromProcMemInfo()
        {
            var path = "/proc/meminfo";
            
            // this is different then sysinfo freeram+buffered (and the closest to the real free memory)
            try
            {
                using (var bufferedReader =new KernelVirtualFileSystemUtils.BufferedPosixKeyValueOutputValueReader(path))
                {
                    bufferedReader.ReadFileIntoBuffer();
                    var memAvailable = bufferedReader.ExtractNumericValueFromKeyValuePairsFormattedFile(MemAvailable);
                    var memFree = bufferedReader.ExtractNumericValueFromKeyValuePairsFormattedFile(MemFree);
                    return (MemAvailable:memAvailable, MemFree: memFree);
                }
            }
            catch (Exception ex)
            {
                if (Logger.IsInfoEnabled)
                    Logger.Info($"Failed to read value from {path}",ex);
                return (-1,-1);
            }
        }
        
        public static (long Committed_AS, long CommitLimit) GetCommitAsAndCommitLimitFromProcMemInfo()
        {
            // MemFree is really different then MemAvailable (while free is usually lower then the real free,
            // and available is only estimated free which sometimes higher then the real free memory)
            var path = "/proc/meminfo";
            
            try
            {
                using (var bufferedReader =new KernelVirtualFileSystemUtils.BufferedPosixKeyValueOutputValueReader(path))
                {
                    bufferedReader.ReadFileIntoBuffer();
                    var committedAs = bufferedReader.ExtractNumericValueFromKeyValuePairsFormattedFile(Committed_AS);
                    var commitLimit = bufferedReader.ExtractNumericValueFromKeyValuePairsFormattedFile(CommitLimit);
                    return (Committed_AS:committedAs, CommitLimit: commitLimit);
                }
            }
            catch (Exception ex)
            {
                if (Logger.IsInfoEnabled)
                    Logger.Info($"Failed to read value from {path}",ex);
                return (-1,-1);
            }
        }

        public static (double InstalledMemory, double UsableMemory) GetMemoryInfoInGb()
        {
            var memoryInformation = GetMemoryInfo();
            var installedMemoryInGb = memoryInformation.InstalledMemory.GetDoubleValue(SizeUnit.Gigabytes);
            var usableMemoryInGb = memoryInformation.TotalPhysicalMemory.GetDoubleValue(SizeUnit.Gigabytes);
            return (installedMemoryInGb, usableMemoryInGb);
        }

        public static unsafe MemoryInfoResult GetMemoryInfo()
        {
            if (_failedToGetAvailablePhysicalMemory)
            {
                if (Logger.IsInfoEnabled)
                    Logger.Info("Because of a previous error in getting available memory, we are now lying and saying we have 256MB free");
                return FailedResult;
            }

            try
            {
                if (PlatformDetails.RunningOnPosix == false)
                {
                    // windows
                    var memoryStatus = new MemoryStatusEx
                    {
                        dwLength = (uint)sizeof(MemoryStatusEx)
                    };

                    if (GlobalMemoryStatusEx(&memoryStatus) == false)
                    {
                        if (Logger.IsInfoEnabled)
                            Logger.Info("Failure when trying to read memory info from Windows, error code is: " + Marshal.GetLastWin32Error());
                        return FailedResult;
                    }

                    // The amount of physical memory retrieved by the GetPhysicallyInstalledSystemMemory function 
                    // must be equal to or greater than the amount reported by the GlobalMemoryStatusEx function
                    // if it is less, the SMBIOS data is malformed and the function fails with ERROR_INVALID_DATA. 
                    // Malformed SMBIOS data may indicate a problem with the user's computer.
                    var fetchedInstalledMemory = GetPhysicallyInstalledSystemMemory(out var installedMemoryInKb);

                    SetMemoryRecords((long)memoryStatus.ullAvailPhys);
                    
                    return new MemoryInfoResult
                    {
                        TotalCommittableMemory = new Size((long)memoryStatus.ullTotalPageFile, SizeUnit.Bytes),
                        CurrentCommitCharge = new Size((long)(memoryStatus.ullTotalPageFile - memoryStatus.ullAvailPageFile), SizeUnit.Bytes),
                        AvailableMemory = new Size((long)memoryStatus.ullAvailPhys, SizeUnit.Bytes),
                        TotalPhysicalMemory = new Size((long)memoryStatus.ullTotalPhys, SizeUnit.Bytes),
                        InstalledMemory = fetchedInstalledMemory ? 
                            new Size(installedMemoryInKb, SizeUnit.Kilobytes) : 
                            new Size((long)memoryStatus.ullTotalPhys, SizeUnit.Bytes),
                        MemoryUsageRecords = new MemoryInfoResult.MemoryUsageLowHigh
                        {
                            High = new MemoryInfoResult.MemoryUsageIntervals
                            {
                                LastOneMinute = new Size(HighLastOneMinute, SizeUnit.Bytes),
                                LastFiveMinutes = new Size(HighLastFiveMinutes, SizeUnit.Bytes),
                                SinceStartup = new Size(HighSinceStartup, SizeUnit.Bytes)
                            },
                            Low = new MemoryInfoResult.MemoryUsageIntervals
                            {
                                LastOneMinute = new Size(LowLastOneMinute, SizeUnit.Bytes),
                                LastFiveMinutes = new Size(LowLastFiveMinutes, SizeUnit.Bytes),
                                SinceStartup = new Size(LowSinceStartup, SizeUnit.Bytes)
                            }
                        }
                    };
                }

                // read both cgroup and sysinfo memory stats, and use the lowest if applicable
                var cgroupMemoryLimit = KernelVirtualFileSystemUtils.ReadNumberFromCgroupFile("/sys/fs/cgroup/memory/memory.limit_in_bytes");
                var cgroupMemoryUsage = KernelVirtualFileSystemUtils.ReadNumberFromCgroupFile("/sys/fs/cgroup/memory/memory.usage_in_bytes");

                ulong availableRamInBytes;
                ulong totalPhysicalMemoryInBytes;

                if (PlatformDetails.RunningOnMacOsx == false)
                {
                    // linux
                    int rc = 0;
                    ulong totalram = 0;
                    if (PlatformDetails.Is32Bits == false)
                    {
                        var info = new sysinfo_t();
                        rc = Syscall.sysinfo(ref info);
                        totalram = info.TotalRam;
                    }
                    else
                    {
                        var info = new sysinfo_t_32bit();
                        rc = Syscall.sysinfo(ref info);
                        totalram = info.TotalRam;
                    }
                    if (rc != 0)
                    {
                        if (Logger.IsInfoEnabled)
                            Logger.Info("Failure when trying to read memory info from posix, error code was: " + Marshal.GetLastWin32Error());
                        return FailedResult;
                    }

                    var availableAndFree = GetAvailableAndFreeMemoryFromProcMemInfo();
                    
                    availableRamInBytes = (ulong)availableAndFree.MemAvailable;
                    totalPhysicalMemoryInBytes = totalram;
                }
                else
                {
                    // macOS
                    var mib = new[] {(int)TopLevelIdentifiersMacOs.CTL_HW, (int)CtkHwIdentifiersMacOs.HW_MEMSIZE};
                    ulong physicalMemory = 0;
                    var len = sizeof(ulong);

                    if (Syscall.sysctl(mib, 2, &physicalMemory, &len, null, UIntPtr.Zero) != 0)
                    {
                        if (Logger.IsInfoEnabled)
                            Logger.Info("Failure when trying to read physical memory info from MacOS, error code was: " + Marshal.GetLastWin32Error());
                        return FailedResult;
                    }

                    totalPhysicalMemoryInBytes = physicalMemory;

                    uint pageSize;
                    var vmStats = new vm_statistics64();

                    var machPort = Syscall.mach_host_self();
                    var count = sizeof(vm_statistics64) / sizeof(uint);

                    if (Syscall.host_page_size(machPort, &pageSize) != 0 ||
                        Syscall.host_statistics64(machPort, (int)FlavorMacOs.HOST_VM_INFO64, &vmStats, &count) != 0)
                    {
                        if (Logger.IsInfoEnabled)
                            Logger.Info("Failure when trying to get vm_stats from MacOS, error code was: " + Marshal.GetLastWin32Error());
                        return FailedResult;
                    }

                    availableRamInBytes = (vmStats.FreePagesCount + vmStats.InactivePagesCount) * (ulong)pageSize;
                }

                Size availableRam, totalPhysicalMemory;
                if (cgroupMemoryLimit < (long)totalPhysicalMemoryInBytes)
                {
                    availableRam = new Size(cgroupMemoryLimit - cgroupMemoryUsage, SizeUnit.Bytes);
                    totalPhysicalMemory = new Size(cgroupMemoryLimit, SizeUnit.Bytes);
                }
                else
                {
                    availableRam = new Size((long)availableRamInBytes, SizeUnit.Bytes);
                    totalPhysicalMemory = new Size((long)totalPhysicalMemoryInBytes, SizeUnit.Bytes);
                }

                SetMemoryRecords((long)availableRamInBytes);
                
                var commitAsAndCommitLimit = GetCommitAsAndCommitLimitFromProcMemInfo();

                return new MemoryInfoResult
                {
                    TotalCommittableMemory = new Size(commitAsAndCommitLimit.CommitLimit,SizeUnit.Kilobytes),
                    CurrentCommitCharge = new Size(commitAsAndCommitLimit.Committed_AS,SizeUnit.Kilobytes),

                    AvailableMemory = availableRam,
                    TotalPhysicalMemory = totalPhysicalMemory,
                    InstalledMemory = totalPhysicalMemory,
                    MemoryUsageRecords = new MemoryInfoResult.MemoryUsageLowHigh
                    {
                        High = new MemoryInfoResult.MemoryUsageIntervals
                        {
                            LastOneMinute = new Size(HighLastOneMinute, SizeUnit.Bytes),
                            LastFiveMinutes = new Size(HighLastFiveMinutes, SizeUnit.Bytes),
                            SinceStartup = new Size(HighSinceStartup, SizeUnit.Bytes)
                        },
                        Low = new MemoryInfoResult.MemoryUsageIntervals
                        {
                            LastOneMinute = new Size(LowLastOneMinute, SizeUnit.Bytes),
                            LastFiveMinutes = new Size(LowLastFiveMinutes, SizeUnit.Bytes),
                            SinceStartup = new Size(LowSinceStartup, SizeUnit.Bytes)
                        }
                    }
                };
            }
            catch (Exception e)
            {
                if (Logger.IsInfoEnabled)
                    Logger.Info("Error while trying to get available memory, will stop trying and report that there is 256MB free only from now on", e);
                _failedToGetAvailablePhysicalMemory = true;
                return FailedResult;
            }
        }

        public static (long WorkingSet, long TotalUnmanagedAllocations, long ManagedMemory, long MappedTemp) MemoryStats()
        {
            using (var currentProcess = Process.GetCurrentProcess())
            {
                var workingSet =
                    PlatformDetails.RunningOnPosix == false || PlatformDetails.RunningOnMacOsx
                        ? currentProcess.WorkingSet64
                        : GetRssMemoryUsage(currentProcess.Id);

                long totalUnmanagedAllocations = 0;
                foreach (var stats in NativeMemory.ThreadAllocations.Values)
                {
                    if (stats == null)
                        continue;
                    if (stats.IsThreadAlive())
                        totalUnmanagedAllocations += stats.TotalAllocated;
                }

                // scratch buffers, compression buffers
                var totalMappedTemp = 0L;
                foreach (var mapping in NativeMemory.FileMapping)
                {
                    if (mapping.Key == null)
                        continue;

                    if (mapping.Key.EndsWith(".buffers", StringComparison.OrdinalIgnoreCase) == false)
                        continue;

                    var maxMapped = 0L;
                    foreach (var singleMapping in mapping.Value)
                    {
                        maxMapped = Math.Max(maxMapped, singleMapping.Value);
                    }

                    totalMappedTemp += maxMapped;
                }

                var managedMemory = GC.GetTotalMemory(false);
                return (workingSet, totalUnmanagedAllocations, managedMemory, totalMappedTemp);
            }
        }

        private static void SetMemoryRecords(long availableRamInBytes)
        {
            var now = DateTime.UtcNow;

            if (HighSinceStartup < availableRamInBytes)
                HighSinceStartup = availableRamInBytes;
            if (LowSinceStartup > availableRamInBytes)
                LowSinceStartup = availableRamInBytes;

            while (MemByTime.TryPeek(out var existing) && 
                (now - existing.Item2) > TimeSpan.FromMinutes(5))
            {
                if (MemByTime.TryDequeue(out _) == false)
                    break;
            }

            if (now - _memoryRecordsSet < MemByTimeThrottleTime)
                return;

            _memoryRecordsSet = now;

            MemByTime.Enqueue(new Tuple<long, DateTime>(availableRamInBytes, now));

            long highLastOneMinute = 0;
            long lowLastOneMinute = long.MaxValue;
            long highLastFiveMinutes = 0;
            long lowLastFiveMinutes = long.MaxValue;

            foreach (var item in MemByTime)
            {
                if (now - item.Item2 < TimeSpan.FromMinutes(1))
                {
                    if (highLastOneMinute < item.Item1)
                        highLastOneMinute = item.Item1;
                    if (lowLastOneMinute > item.Item1)
                        lowLastOneMinute = item.Item1;
                }
                if (highLastFiveMinutes < item.Item1)
                    highLastFiveMinutes = item.Item1;
                if (lowLastFiveMinutes > item.Item1)
                    lowLastFiveMinutes = item.Item1;
            }

            HighLastOneMinute = highLastOneMinute;
            LowLastOneMinute = lowLastOneMinute;
            HighLastFiveMinutes = highLastFiveMinutes;
            LowLastFiveMinutes = lowLastFiveMinutes;
        }

        public static string IsSwappingOnHddInsteadOfSsd()
        {
            if (PlatformDetails.RunningOnPosix)
                return CheckPageFileOnHdd.PosixIsSwappingOnHddInsteadOfSsd();
            return CheckPageFileOnHdd.WindowsIsSwappingOnHddInsteadOfSsd();
        }

        public static unsafe bool WillCauseHardPageFault(byte* addr, long length) => PlatformDetails.RunningOnPosix ? PosixMemoryQueryMethods.WillCauseHardPageFault(addr, length) : Win32MemoryQueryMethods.WillCauseHardPageFault(addr, length);
    }

    public struct MemoryInfoResult
    {
        public class MemoryUsageIntervals
        {
            public Size LastOneMinute;
            public Size LastFiveMinutes;
            public Size SinceStartup;
        }
        public class MemoryUsageLowHigh
        {
            public MemoryUsageIntervals High;
            public MemoryUsageIntervals Low;
        }

        public Size TotalCommittableMemory;
        public Size CurrentCommitCharge;

        public Size TotalPhysicalMemory;
        public Size InstalledMemory;
        public Size AvailableMemory;
        public MemoryUsageLowHigh MemoryUsageRecords;
    }
}
