using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OODD.Timer;

internal static class ThreadBoost
{
    private const int ThreadPriorityTimeCritical = 15;

    /// <summary>当前平台是否支持实时优先级提升。</summary>
    public static bool IsSupported => Platform.IsWindows;

    /// <summary>把当前线程提升到 <c>THREAD_PRIORITY_TIME_CRITICAL</c>。</summary>
    /// <returns>是否成功；不支持的平台或调用失败都返回 <see langword="false"/>，不抛异常。</returns>
    public static bool TryEnableTimeCritical()
    {
        if (!Platform.IsWindows)
        {
            return false;
        }

        try
        {
            return SetThreadPriority(GetCurrentThread(), ThreadPriorityTimeCritical);
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    public static void ValidateProcessorAffinity(int? processorIndex, ushort processorGroup)
    {
        if (!processorIndex.HasValue)
        {
            if (processorGroup != 0)
            {
                throw new ArgumentException("ProcessorGroup requires ProcessorIndex.", nameof(processorGroup));
            }

            return;
        }

        if (processorIndex.Value < 0 || (Platform.IsWindows && processorIndex.Value >= IntPtr.Size * 8))
        {
            throw new ArgumentOutOfRangeException(nameof(processorIndex));
        }

        if (!Platform.IsWindows)
        {
#if NET40
            throw new PlatformNotSupportedException("Processor affinity requires Windows for the net40 asset.");
#else
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                throw new PlatformNotSupportedException("Strict processor affinity is supported on Windows and Linux only.");
            }

            if (processorGroup != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(processorGroup), "Processor groups are Windows-only.");
            }
#endif
        }
    }

    public static void ApplyProcessorAffinity(int processorIndex, ushort processorGroup)
    {
        if (Platform.IsWindows)
        {
            var requested = new GroupAffinity
            {
                Mask = new UIntPtr(1UL << processorIndex),
                Group = processorGroup,
            };
            if (!SetThreadGroupAffinity(GetCurrentThread(), ref requested, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetThreadGroupAffinity failed.");
            }

            if (!GetThreadGroupAffinity(GetCurrentThread(), out GroupAffinity actual))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetThreadGroupAffinity failed.");
            }

            if (actual.Group != requested.Group || actual.Mask != requested.Mask)
            {
                throw new InvalidOperationException("The operating system did not apply the requested processor affinity.");
            }

            return;
        }

#if !NET40
        byte[] mask = GetLinuxAffinity();
        if (processorIndex / 8 >= mask.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(processorIndex), "Processor exceeds the kernel CPU mask size.");
        }

        // cpu_set_t is an array of native-endian unsigned long words, not a byte-oriented bitmap.
        int wordOffset = processorIndex / (IntPtr.Size * 8) * IntPtr.Size;
        int byteInWord = processorIndex / 8 % IntPtr.Size;
        int byteIndex = wordOffset + (BitConverter.IsLittleEndian ? byteInWord : IntPtr.Size - 1 - byteInWord);
        Array.Clear(mask, 0, mask.Length);
        mask[byteIndex] = (byte)(1 << (processorIndex % 8));
        if (sched_setaffinity(0, new UIntPtr((uint)mask.Length), mask) != 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "sched_setaffinity failed.");
        }

        byte[] actualMask = GetLinuxAffinity();
        for (int i = 0; i < actualMask.Length; i++)
        {
            byte expected = i == byteIndex ? (byte)(1 << (processorIndex % 8)) : (byte)0;
            if (actualMask[i] != expected)
            {
                throw new InvalidOperationException("The operating system did not apply the requested processor affinity.");
            }
        }
#endif
    }

#if !NET40
    private static byte[] GetLinuxAffinity()
    {
        for (int size = 128; size <= 1024 * 1024; size *= 2)
        {
            var mask = new byte[size];
            if (sched_getaffinity(0, new UIntPtr((uint)size), mask) == 0)
            {
                return mask;
            }

            int error = Marshal.GetLastWin32Error();
            if (error != 22)
            {
                throw new Win32Exception(error, "sched_getaffinity failed.");
            }
        }

        throw new PlatformNotSupportedException("The kernel CPU mask exceeds the supported buffer size.");
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int sched_getaffinity(int pid, UIntPtr size, [Out] byte[] mask);

    [DllImport("libc", SetLastError = true)]
    private static extern int sched_setaffinity(int pid, UIntPtr size, byte[] mask);
#endif

    [StructLayout(LayoutKind.Sequential)]
    private struct GroupAffinity
    {
        public UIntPtr Mask;
        public ushort Group;
        public ushort Reserved0;
        public ushort Reserved1;
        public ushort Reserved2;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadGroupAffinity(IntPtr thread, ref GroupAffinity affinity, out GroupAffinity previous);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadGroupAffinity(IntPtr thread, out GroupAffinity affinity);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadPriority(IntPtr thread, int priority);
}
